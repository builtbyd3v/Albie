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

    /// <summary>"#forge-team" for channels, the person's (or group's) name for DMs.</summary>
    public static string DisplayName(SlackChannel c)
    {
        if (c == null) return "";
        string name = string.IsNullOrEmpty(c.name) ? c.id : c.name;
        return IsDmKind(c.kind) ? name : "#" + name;
    }

    /// <summary>"#forge-team" / "DM" for a message when its channel isn't in the list.</summary>
    public static string ChannelLabel(NotifyMessage m)
    {
        if (m == null) return "";
        if (m.isDm || string.IsNullOrEmpty(m.channelName) || m.channelName == "dm" || m.channelName == "group-dm") return "DM";
        return "#" + m.channelName;
    }

    // string.GetHashCode is randomized per process; keep bubble colors stable across runs.
    public static int StableHash(string s)
    {
        int h = 17;
        if (s != null) for (int i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
        return h & 0x7fffffff;
    }

    /// <summary>
    /// Slack markup to TMP rich text: &lt;@U..&gt; becomes @name (the user's own mentions in the accent, bold), &lt;#C..|x&gt;
    /// becomes #x, &lt;!here&gt; @here, links show their label, HTML entities are unescaped and stray angle brackets are
    /// kept literal. <paramref name="nameOf"/> resolves a user id to a name (null = unknown -> "user").
    /// </summary>
    public static string Render(string text, string meId, Func<string, string> nameOf, Color accent)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (accentHex == null) accentHex = "#" + ColorUtility.ToHtmlStringRGB(accent);
        sb.Clear();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end < 0) { sb.Append("<noparse><</noparse>"); i++; continue; }
                string token = text.Substring(i + 1, end - i - 1);
                AppendToken(token, meId, nameOf);
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
        return sb.ToString();
    }

    static void AppendToken(string token, string meId, Func<string, string> nameOf)
    {
        if (token.Length == 0) return;
        int bar = token.IndexOf('|');
        string head = bar >= 0 ? token.Substring(0, bar) : token;
        string label = bar >= 0 ? token.Substring(bar + 1) : null;
        if (head.StartsWith("@"))
        {
            string id = head.Substring(1);
            string name = label ?? nameOf?.Invoke(id) ?? "user";
            if (!string.IsNullOrEmpty(meId) && id == meId) sb.Append("<b><color=").Append(accentHex).Append(">@").Append(name).Append("</color></b>");
            else sb.Append("<alpha=#D0>@").Append(name).Append("<alpha=#FF>");
        }
        else if (head.StartsWith("#"))
        {
            sb.Append("<alpha=#D0>#").Append(label ?? head.Substring(1)).Append("<alpha=#FF>");
        }
        else if (head.StartsWith("!"))
        {
            string what = head.Substring(1);
            int caret = what.IndexOf('^');
            if (caret >= 0) sb.Append(label ?? "@group");  // <!subteam^S..|@group>
            else sb.Append("<b><color=").Append(accentHex).Append(">@").Append(label ?? what).Append("</color></b>");
        }
        else
        {
            sb.Append("<u>").Append(label ?? head).Append("</u>");  // a link
        }
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
