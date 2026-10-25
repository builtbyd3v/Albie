using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

// REST shapes of the Slack panel that aren't /ws events (those are in Core/Net/ForgeEvents.cs: NotifyMessage,
// SlackChannel, NotifyChannels, NotifyDraft, ...). Contract: PROJECT_CONTEXT 7 / 8.8.

/// <summary>GET /messages/history?channel=&amp;limit=&amp;before= : one conversation, oldest first.</summary>
public class SlackHistory
{
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;
    [JsonProperty("kind")] public string kind;
    [JsonProperty("messages")] public System.Collections.Generic.List<NotifyMessage> messages = new System.Collections.Generic.List<NotifyMessage>();
    [JsonProperty("has_more")] public bool hasMore;
}

/// <summary>GET /messages/channels.</summary>
public class SlackChannelList
{
    [JsonProperty("channels")] public System.Collections.Generic.List<SlackChannel> channels = new System.Collections.Generic.List<SlackChannel>();
}

/// <summary>GET /messages/users/me: who the user token belongs to (so the panel knows which mentions are "me").</summary>
public class SlackMe
{
    [JsonProperty("user_id")] public string userId;
    [JsonProperty("name")] public string name;
    [JsonProperty("display_name")] public string displayName;
    [JsonProperty("team")] public string team;                // the workspace's name (the sidebar header); "" if unknown
}

/// <summary>
/// ws <c>messages.open</c> (hub -> Unity): Albie's messages_open. Show this conversation, opening MSG mode if needed;
/// <see cref="SlackRemote"/> answers <c>messages.opened {pet_id, request_id, channel, ok, reason?}</c>.
/// </summary>
public class SlackOpenRequest
{
    [JsonProperty("pet_id")] public string petId;
    [JsonProperty("request_id")] public string requestId;
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;  // "#general", the DM partner
    [JsonProperty("kind")] public string kind;                 // channel | private | dm | group-dm
}

/// <summary>POST /messages/drafts/{id}/send.</summary>
public class SlackSendReply
{
    [JsonProperty("ts")] public string ts;
}

/// <summary>
/// Text helpers shared by the Slack panel, the Mentions panel and the cards: Slack ts maths, relative times, initials,
/// and Slack's message markup (&lt;@U..&gt;, &lt;#C..|name&gt;, &lt;url|label&gt;, &amp;amp;) rendered as TMP rich text.
/// Called when data changes, never per frame.
/// </summary>
public static class SlackText
{
    static readonly char[] LineBreaks = { '\n', '\r' };
    static readonly StringBuilder sb = new StringBuilder(512);
    static string accentHex;

    /// <summary>Slack ts ("1729800000.123456") as seconds; 0 when unparsable.</summary>
    public static double Ts(string ts) =>
        double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>"now" / "5m" / "2h" / "3d".</summary>
    public static string RelativeTime(string ts)
    {
        double seconds = Ts(ts);
        if (seconds <= 0) return "";
        double age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
        if (age < 60) return "now";
        if (age < 3600) return ((int)(age / 60)) + "m";
        if (age < 86400) return ((int)(age / 3600)) + "h";
        return ((int)(age / 86400)) + "d";
    }

    /// <summary>A draft's files in one line: "4 files · STEP, STL, Drawing PDF, Parts list · 1.2 MB"; "" for none.</summary>
    public static string Attachments(System.Collections.Generic.List<DraftAttachment> files)
    {
        if (files == null || files.Count == 0) return "";
        sb.Clear();
        sb.Append(files.Count == 1 ? "1 file · " : files.Count + " files · ");
        long bytes = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (i > 0) sb.Append(", ");
            sb.Append(f.kind switch
            {
                "step" => "STEP",
                "stl" => "STL",
                "drawing" => "Drawing PDF",
                "parts" => "Parts list",
                _ => f.name,
            });
            bytes += Math.Max(0, f.bytes);
        }
        return sb.Append(" · ").Append(Size(bytes)).ToString();
    }

    /// <summary>"820 KB" / "1.2 MB".</summary>
    public static string Size(long bytes) =>
        bytes >= 1_000_000 ? (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (bytes + 999) / 1000) + " KB";

    public static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int cut = text.IndexOfAny(LineBreaks);
        return cut < 0 ? text : text.Substring(0, cut);
    }

    /// <summary>The sender's name: user_name, else the user id, else "?".</summary>
    public static string SenderName(NotifyMessage m)
    {
        if (m == null) return "?";
        if (!string.IsNullOrEmpty(m.userName)) return m.userName;
        return string.IsNullOrEmpty(m.user) ? "?" : m.user;
    }

    /// <summary>Up to two initials: the hub's user_initials, else from the name ("Hudson Marr" -> "HM").</summary>
    public static string Initials(NotifyMessage m)
    {
        if (m != null && !string.IsNullOrEmpty(m.userInitials)) return m.userInitials.Length > 2 ? m.userInitials.Substring(0, 2) : m.userInitials;
        return Initials(SenderName(m));
    }

    public static string Initials(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        sb.Clear();
        bool wordStart = true;
        for (int i = 0; i < name.Length && sb.Length < 2; i++)
        {
            char c = name[i];
            if (c == ' ' || c == '.' || c == '_' || c == '-') { wordStart = true; continue; }
            if (wordStart && char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            wordStart = false;
        }
        if (sb.Length == 0) sb.Append(char.ToUpperInvariant(name[0]));
        return sb.ToString();
    }

    public static bool IsDmKind(string kind) => kind == "dm" || kind == "group-dm";

    /// <summary>A channel name without its '#' (the hub's list already says "#general"; messages say "general").</summary>
    public static string Bare(string name) => string.IsNullOrEmpty(name) || name[0] != '#' ? name : name.TrimStart('#');

    /// <summary>"#forge-team" for channels (exactly one '#'), the person's (or group's) name for DMs.</summary>
    public static string DisplayName(SlackChannel c)
    {
        if (c == null) return "";
        string name = string.IsNullOrEmpty(c.name) ? c.id : c.name;
        return IsDmKind(c.kind) ? name : "#" + Bare(name);
    }

    /// <summary>What the sidebar row and the header show next to their '#' / lock / avatar: the name with no '#'.</summary>
    public static string PlainName(SlackChannel c)
    {
        if (c == null) return "";
        return Bare(string.IsNullOrEmpty(c.name) ? c.id : c.name);
    }

    /// <summary>The message's time on this machine's clock.</summary>
    public static DateTime Local(string ts)
    {
        double s = Ts(ts);
        return s <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000.0)).LocalDateTime;
    }

    /// <summary>"4:15 PM", like Slack beside a name.</summary>
    public static string ClockTime(string ts)
    {
        var t = Local(ts);
        return t == DateTime.MinValue ? "" : t.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>"4:15", the time in the gutter of a grouped message.</summary>
    public static string ShortClockTime(string ts)
    {
        var t = Local(ts);
        return t == DateTime.MinValue ? "" : t.ToString("h:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Slack's day divider: "Today", "Yesterday", "Monday", "Wednesday, October 22nd", "March 3rd, 2025".</summary>
    public static string DayLabel(DateTime day)
    {
        var today = DateTime.Now.Date;
        int ago = (int)(today - day.Date).TotalDays;
        if (ago == 0) return "Today";
        if (ago == 1) return "Yesterday";
        if (ago > 1 && ago < 7) return day.ToString("dddd", CultureInfo.InvariantCulture);
        string ordinal = day.Day + Ordinal(day.Day);
        return day.Year == today.Year
            ? day.ToString("dddd, MMMM ", CultureInfo.InvariantCulture) + ordinal
            : day.ToString("MMMM ", CultureInfo.InvariantCulture) + ordinal + day.ToString(", yyyy", CultureInfo.InvariantCulture);
    }

    static string Ordinal(int d)
    {
        if (d % 100 >= 11 && d % 100 <= 13) return "th";
        switch (d % 10) { case 1: return "st"; case 2: return "nd"; case 3: return "rd"; default: return "th"; }
    }

    /// <summary>A reply inside a thread (the panel shows it in the conversation with "replied in a thread").</summary>
    public static bool IsThreadReply(NotifyMessage m) =>
        m != null && !string.IsNullOrEmpty(m.threadTs) && m.threadTs != m.ts;

    /// <summary>"#forge-team" / "DM" for a message when its channel isn't in the list.</summary>
    public static string ChannelLabel(NotifyMessage m)
    {
        if (m == null) return "";
        if (m.isDm || string.IsNullOrEmpty(m.channelName) || m.channelName == "dm" || m.channelName == "group-dm") return "DM";
        return "#" + Bare(m.channelName);
    }

    // string.GetHashCode is randomized per process; keep bubble colors stable across runs.
    public static int StableHash(string s)
    {
        int h = 17;
        if (s != null) for (int i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
        return h & 0x7fffffff;
    }

    /// <summary>How mentions and links look: the Mentions cards' accent, or Slack's own (blue links, yellow for you).</summary>
    public enum Look { Accent, Slack }

    // Slack's dark theme: links and other people's mentions blue on a faint blue, the user's own mentions and @here
    // yellow on a faint yellow, inline code orange on a faint white.
    const string SlackBlue = "#1D9BD1", SlackBlueBg = "#1D9BD126", SlackYellow = "#F2C744", SlackYellowBg = "#F2C74438";
    const string CodeOpen = "<color=#E8912D><mark=#FFFFFF12>", CodeClose = "</mark></color>";
    // Bold without most of TMP's faux-bold letter spacing (SlackUi.SetBold does the same for whole texts).
    public const string BoldOpen = "<b><cspace=-0.055em>", BoldClose = "</cspace></b>";

    /// <summary>
    /// Slack markup to TMP rich text: &lt;@U..&gt; becomes @name (the user's own mentions in the accent, bold), &lt;#C..|x&gt;
    /// becomes #x, &lt;!here&gt; @here, links show their label, HTML entities are unescaped and stray angle brackets are
    /// kept literal. <paramref name="nameOf"/> resolves a user id to a name (null = unknown -> "user").
    /// </summary>
    public static string Render(string text, string meId, Func<string, string> nameOf, Color accent) =>
        Render(text, meId, nameOf, accent, Look.Accent);

    /// <summary>
    /// <see cref="Render(string, string, Func{string, string}, Color)"/> in Slack's colours, plus Slack's inline
    /// formatting: *bold*, _italic_, ~strike~, `code` and ```code blocks```.
    /// </summary>
    public static string RenderSlack(string text, string meId, Func<string, string> nameOf) =>
        Render(text, meId, nameOf, default, Look.Slack);

    // Closing tags waiting at a later index (the formatting pairs found ahead of time); small and reused.
    static readonly int[] closeAt = new int[16];
    static readonly string[] closeTag = new string[16];
    static readonly int[] closeLen = new int[16];
    static int closes;

    static string Render(string text, string meId, Func<string, string> nameOf, Color accent, Look look)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (accentHex == null && look == Look.Accent) accentHex = "#" + ColorUtility.ToHtmlStringRGB(accent);
        sb.Clear();
        closes = 0;
        int i = 0, n = text.Length;
        while (i < n)
        {
            if (closes > 0 && TryClose(i, out int skip)) { i += skip; continue; }
            char c = text[i];
            if (look == Look.Slack && TryOpen(text, i, out int open)) { i += open; continue; }
            if (c == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end < 0) { sb.Append("<noparse><</noparse>"); i++; continue; }
                string token = text.Substring(i + 1, end - i - 1);
                AppendToken(token, meId, nameOf, look);
                i = end + 1;
                continue;
            }
            if (c == '&')
            {
                if (Matches(text, i, "&amp;")) { sb.Append('&'); i += 5; continue; }
                if (Matches(text, i, "&lt;")) { sb.Append("<noparse><</noparse>"); i += 4; continue; }
                if (Matches(text, i, "&gt;")) { sb.Append('>'); i += 4; continue; }
            }
            sb.Append(c);
            i++;
        }
        for (int k = closes - 1; k >= 0; k--) sb.Append(closeTag[k]);  // a pair a token swallowed the end of: close it anyway
        closes = 0;
        return sb.ToString();
    }

    // At i: the closing marker queued when its opening one was read. Emits the tag; skip = the marker's length.
    static bool TryClose(int i, out int skip)
    {
        skip = 0;
        int k = closes - 1;
        if (closeAt[k] != i) return false;
        sb.Append(closeTag[k]);
        skip = closeLen[k];
        closes--;
        return true;
    }

    // Slack's inline formatting at i: a marker at a word start, its twin later on the same line at a word end, and no
    // space just inside either. Emits the opening tag and queues the closing one.
    static bool TryOpen(string s, int i, out int skip)
    {
        skip = 0;
        if (closes >= closeAt.Length) return false;
        char c = s[i];
        if (c == '`' && Matches(s, i, "```"))
        {
            int end = s.IndexOf("```", i + 3, StringComparison.Ordinal);
            if (end < 0) return false;
            int from = i + 3;
            if (from < end && (s[from] == '\n' || s[from] == '\r')) from++;  // the block's text starts on its own line
            sb.Append(CodeOpen);
            Queue(end, CodeClose, 3);
            skip = from - i;
            return true;
        }
        string open, close;
        switch (c)
        {
            case '*': open = BoldOpen; close = BoldClose; break;
            case '_': open = "<i>"; close = "</i>"; break;
            case '~': open = "<s>"; close = "</s>"; break;
            case '`': open = CodeOpen; close = CodeClose; break;
            default: return false;
        }
        if (i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;          // inside a word (snake_case, 2*3)
        if (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])) return false;
        for (int j = i + 1; j < s.Length; j++)
        {
            char d = s[j];
            if (d == '\n' || d == '\r') return false;
            if (d != c) continue;
            if (j == i + 1 || char.IsWhiteSpace(s[j - 1])) return false;
            if (j + 1 < s.Length && char.IsLetterOrDigit(s[j + 1])) continue;  // not a word end yet
            if (closes > 0 && j > closeAt[closes - 1]) return false;            // would cross the enclosing pair
            sb.Append(open);
            Queue(j, close, 1);
            skip = 1;
            return true;
        }
        return false;
    }

    static void Queue(int at, string tag, int len)
    {
        closeAt[closes] = at;
        closeTag[closes] = tag;
        closeLen[closes] = len;
        closes++;
    }

    static void AppendToken(string token, string meId, Func<string, string> nameOf, Look look)
    {
        if (token.Length == 0) return;
        bool slack = look == Look.Slack;
        int bar = token.IndexOf('|');
        string head = bar >= 0 ? token.Substring(0, bar) : token;
        string label = bar >= 0 ? token.Substring(bar + 1) : null;
        if (head.StartsWith("@"))
        {
            string id = head.Substring(1);
            string name = label ?? nameOf?.Invoke(id) ?? "user";
            bool me = !string.IsNullOrEmpty(meId) && id == meId;
            if (slack) Highlight(me ? SlackYellow : SlackBlue, me ? SlackYellowBg : SlackBlueBg, name);
            else if (me) sb.Append("<b><color=").Append(accentHex).Append(">@").Append(name).Append("</color></b>");
            else sb.Append("<alpha=#D0>@").Append(name).Append("<alpha=#FF>");
        }
        else if (head.StartsWith("#"))
        {
            string name = label ?? head.Substring(1);
            if (slack) sb.Append("<color=").Append(SlackBlue).Append(">#").Append(name).Append("</color>");
            else sb.Append("<alpha=#D0>#").Append(name).Append("<alpha=#FF>");
        }
        else if (head.StartsWith("!"))
        {
            string what = head.Substring(1);
            int caret = what.IndexOf('^');
            if (caret >= 0) sb.Append(label ?? "@group");  // <!subteam^S..|@group>
            else if (slack) Highlight(SlackYellow, SlackYellowBg, label ?? what);
            else sb.Append("<b><color=").Append(accentHex).Append(">@").Append(label ?? what).Append("</color></b>");
        }
        else if (slack)
        {
            sb.Append("<color=").Append(SlackBlue).Append('>').Append(label ?? head).Append("</color>");  // a link
        }
        else
        {
            sb.Append("<u>").Append(label ?? head).Append("</u>");  // a link
        }
    }

    // "@name" in Slack's mention colours. A label that already starts with '@' (<!here|@here>) keeps just the one.
    static void Highlight(string color, string background, string name)
    {
        sb.Append("<mark=").Append(background).Append("><color=").Append(color).Append('>');
        if (string.IsNullOrEmpty(name) || name[0] != '@') sb.Append('@');
        sb.Append(name).Append("</color></mark>");
    }

    static bool Matches(string s, int at, string what)
    {
        if (at + what.Length > s.Length) return false;
        for (int k = 0; k < what.Length; k++) if (s[at + k] != what[k]) return false;
        return true;
    }
}

/// <summary>
/// The one HTTP verb ForgeNet lacks. PATCH /messages/drafts/{id} edits a draft Bit wrote before it is sent (the hub
/// re-emits notify.draft with edited: true). Same base URL (<see cref="ForgeNet.Url"/>) and the same JSON / callback
/// shape as ForgeNet.Post; the coroutine runs on the ForgeNet instance. Live hub only: no mock path.
/// </summary>
public static class SlackNet
{
    public static Coroutine Patch<T>(string path, object body, Action<T> ok, Action<string> fail = null) =>
        ForgeNet.Instance.StartCoroutine(PatchRoutine(path, body, text => ok?.Invoke(JsonConvert.DeserializeObject<T>(text)), fail));

    static IEnumerator PatchRoutine(string path, object body, Action<string> ok, Action<string> fail)
    {
        fail = fail ?? (error => Debug.LogWarning($"[SlackNet] PATCH {path} failed: {error}"));
        if (ForgeNet.Status != ForgeNetStatus.Live)
        {
            yield return null;
            fail("hub not live");
            yield break;
        }
        string json = body == null ? "{}" : JsonConvert.SerializeObject(body);
        using (var request = new UnityWebRequest(ForgeNet.Url(path), "PATCH"))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 15;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success) ok(request.downloadHandler.text);
            else fail($"{request.responseCode} {request.error} {request.downloadHandler?.text}");
        }
    }
}
