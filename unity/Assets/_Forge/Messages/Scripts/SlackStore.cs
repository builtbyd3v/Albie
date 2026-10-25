using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Slack model both panels read: the conversation list (notify.channels, sticky on connect; GET /messages/channels
/// on open), each conversation's history (GET /messages/history, cached oldest -> newest, grown by "load earlier" and
/// notify.message), the user id -> name map learnt from messages (for &lt;@U..&gt; mentions), who "me" is
/// (GET /messages/users/me) and the recent messages across conversations the Mentions panel lists. Static, so the
/// Mentions panel and Slack panel agree; subscribes to ForgeEvents once. Live hub only:
/// with no hub nothing loads and the panels show their offline line. Events fire on the main thread when data changes;
/// nothing here runs per frame.
/// </summary>
public static class SlackStore
{
    public const int RecentCap = 200;

    /// <summary>The conversations in the hub's order (channels first, then DMs, as the hub lists them).</summary>
    public static readonly List<SlackChannel> Channels = new List<SlackChannel>();
    /// <summary>True once a channel list has arrived (even an empty one).</summary>
    public static bool ChannelsKnown { get; private set; }
    /// <summary>Recent messages across conversations, newest first, capped (digest + live + loaded history).</summary>
    public static readonly List<NotifyMessage> Recent = new List<NotifyMessage>(RecentCap + 1);
    public static string MeId { get; private set; }
    public static string MeName { get; private set; }
    /// <summary>The Slack workspace's name (the sidebar header), or null until the hub says.</summary>
    public static string TeamName { get; private set; }

    /// <summary>The list or a count changed (notify.channels, a local seen, a live message).</summary>
    public static event Action ChannelsChanged;
    /// <summary>A conversation's history arrived or grew at the top (channel id).</summary>
    public static event Action<string> HistoryChanged;
    /// <summary>A live message landed in a conversation (channel id, the message).</summary>
    public static event Action<string, NotifyMessage> MessageAdded;

    static readonly Dictionary<string, List<NotifyMessage>> history = new Dictionary<string, List<NotifyMessage>>();
    static readonly Dictionary<string, bool> hasMore = new Dictionary<string, bool>();
    static readonly HashSet<string> loading = new HashSet<string>();
    static readonly Dictionary<string, string> userNames = new Dictionary<string, string>();
    static readonly HashSet<string> echoes = new HashSet<string>();  // ts of messages added locally by AddOwn
    static bool hooked, channelsRequested, meRequested;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        // Play mode without a domain reload keeps static state: start clean and let Ensure hook again.
        Unhook();
        Channels.Clear();
        Recent.Clear();
        history.Clear();
        hasMore.Clear();
        loading.Clear();
        userNames.Clear();
        echoes.Clear();
        ChannelsKnown = channelsRequested = meRequested = false;
        MeId = MeName = TeamName = null;
        ChannelsChanged = null;
        HistoryChanged = null;
        MessageAdded = null;
    }

    /// <summary>Hook the hub events (once) and ask for the list and "me" if the hub is live. Panels call it in OnEnable.</summary>
    public static void Ensure()
    {
        if (!hooked)
        {
            hooked = true;
            ForgeEvents.OnNotifyChannels += OnChannels;
            ForgeEvents.OnNotifyMessage += OnMessage;
            ForgeEvents.OnNotifyDigest += OnDigest;
            ForgeEvents.OnNotifySent += OnSent;
            ForgeNet.StatusChanged += OnStatus;
            if (ForgeNet.TryGetLast(ForgeEvents.NotifyChannelsType, out NotifyChannels sticky)) OnChannels(sticky);
        }
        if (ForgeNet.Status == ForgeNetStatus.Live) Fetch();
    }

    static void Unhook()
    {
        if (!hooked) return;
        hooked = false;
        ForgeEvents.OnNotifyChannels -= OnChannels;
        ForgeEvents.OnNotifyMessage -= OnMessage;
        ForgeEvents.OnNotifyDigest -= OnDigest;
        ForgeEvents.OnNotifySent -= OnSent;
        ForgeNet.StatusChanged -= OnStatus;
    }

    static void OnStatus(ForgeNetStatus status)
    {
        if (status != ForgeNetStatus.Live) return;
        // Reconnected: the cached histories may have gaps, so they reload on the next open.
        history.Clear();
        hasMore.Clear();
        loading.Clear();
        channelsRequested = false;
        Fetch();
    }

    static void Fetch()
    {
        if (!channelsRequested)
        {
            channelsRequested = true;
            ForgeNet.Get<SlackChannelList>("/messages/channels", list =>
            {
                if (list != null) ReplaceChannels(list.channels);
            }, error =>
            {
                channelsRequested = false;
                Debug.LogWarning($"[SlackStore] GET /messages/channels: {error}");
            });
        }
        if (!meRequested)
        {
            meRequested = true;
            ForgeNet.Get<SlackMe>("/messages/users/me", me =>
            {
                if (me == null) return;
                MeId = me.userId;
                MeName = string.IsNullOrEmpty(me.displayName) ? me.name : me.displayName;
                TeamName = string.IsNullOrEmpty(me.team) ? null : me.team;
                if (!string.IsNullOrEmpty(MeId) && !string.IsNullOrEmpty(MeName)) userNames[MeId] = MeName;
                ChannelsChanged?.Invoke();  // the sidebar header shows the workspace's name
            }, error =>
            {
                meRequested = false;
                Debug.LogWarning($"[SlackStore] GET /messages/users/me: {error}");
            });
        }
    }

    /// <summary>Ask the hub for the list again (a panel opening, a reconnect).</summary>
    public static void RefreshChannels()
    {
        channelsRequested = false;
        if (ForgeNet.Status == ForgeNetStatus.Live) Fetch();
    }

    // ---- Channels ----

    static void OnChannels(NotifyChannels n)
    {
        if (n == null) return;
        ReplaceChannels(n.channels);
    }

    static void ReplaceChannels(List<SlackChannel> list)
    {
        Channels.Clear();
        if (list != null) for (int i = 0; i < list.Count; i++) if (list[i] != null && !string.IsNullOrEmpty(list[i].id)) Channels.Add(list[i]);
        ChannelsKnown = true;
        ChannelsChanged?.Invoke();
    }

    /// <summary>
    /// A conversation the list doesn't have yet (Albie opened a new DM before notify.channels caught up): a row for it
    /// until the hub's next list replaces the whole set.
    /// </summary>
    public static void Provisional(string id, string name, string kind)
    {
        if (string.IsNullOrEmpty(id) || Find(id) != null) return;
        Channels.Add(new SlackChannel { id = id, name = string.IsNullOrEmpty(name) ? id : name, kind = string.IsNullOrEmpty(kind) ? "channel" : kind });
        ChannelsChanged?.Invoke();
    }

    public static SlackChannel Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < Channels.Count; i++) if (Channels[i].id == id) return Channels[i];
        return null;
    }

    /// <summary>The conversation to show first for one provider: default, else unread, else first.</summary>
    public static SlackChannel DefaultChannel(bool imessage = false)
    {
        for (int i = 0; i < Channels.Count; i++)
            if (IsProvider(Channels[i], imessage) && Channels[i].isDefault) return Channels[i];
        for (int i = 0; i < Channels.Count; i++)
            if (IsProvider(Channels[i], imessage) && Channels[i].unread > 0) return Channels[i];
        for (int i = 0; i < Channels.Count; i++) if (IsProvider(Channels[i], imessage)) return Channels[i];
        return null;
    }

    public static bool IsProvider(SlackChannel channel, bool imessage)
    {
        if (channel == null) return false;
        bool byId = !string.IsNullOrEmpty(channel.id) && channel.id.StartsWith("im:", StringComparison.Ordinal);
        return imessage ? channel.source == "imessage" || byId : channel.source != "imessage" && !byId;
    }

    public static bool IsIMessageSource(string source, string channel = null) => source == "imessage" ||
        (!string.IsNullOrEmpty(channel) && channel.StartsWith("im:", StringComparison.Ordinal));

    public static int TotalUnread
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Channels.Count; i++) n += Channels[i].unread;
            return n;
        }
    }

    /// <summary>
    /// POST /messages/channels/{id}/seen and clear the counts locally right away (the hub's next notify.channels
    /// confirms). Called when a conversation is shown and when new messages arrive while it is.
    /// </summary>
    public static void MarkSeen(string channelId)
    {
        var c = Find(channelId);
        bool changed = c != null && (c.unread != 0 || c.mentions != 0);
        if (c != null) { c.unread = 0; c.mentions = 0; }
        if (changed) ChannelsChanged?.Invoke();
        if (ForgeNet.Status == ForgeNetStatus.Live && !string.IsNullOrEmpty(channelId))
            ForgeNet.Post($"/messages/channels/{channelId}/seen", null, null, error => Debug.LogWarning($"[SlackStore] seen {channelId}: {error}"));
    }

    // ---- History ----

    /// <summary>The cached messages of a conversation, oldest first; null before the first load.</summary>
    public static List<NotifyMessage> History(string channelId) =>
        channelId != null && history.TryGetValue(channelId, out var list) ? list : null;

    public static bool HasMore(string channelId) => channelId != null && hasMore.TryGetValue(channelId, out bool more) && more;
    public static bool IsLoading(string channelId) => channelId != null && loading.Contains(channelId);

    /// <summary>
    /// GET /messages/history for a conversation: the newest 50 on the first call, or, with <paramref name="earlier"/>,
    /// 50 more before what is cached ("load earlier"). Raises <see cref="HistoryChanged"/>; no-op while loading or offline.
    /// </summary>
    public static void LoadHistory(string channelId, bool earlier = false)
    {
        if (string.IsNullOrEmpty(channelId) || loading.Contains(channelId) || ForgeNet.Status != ForgeNetStatus.Live) return;
        string path = "/messages/history?channel=" + Uri.EscapeDataString(channelId) + "&limit=50";
        if (earlier)
        {
            var cached = History(channelId);
            if (cached == null || cached.Count == 0 || !HasMore(channelId)) return;
            path += "&before=" + Uri.EscapeDataString(cached[0].ts ?? "");
        }
        loading.Add(channelId);
        ForgeNet.Get<SlackHistory>(path, h =>
        {
            loading.Remove(channelId);
            if (h == null) return;
            var list = History(channelId);
            if (list == null) history[channelId] = list = new List<NotifyMessage>(64);
            if (h.messages != null)
            {
                if (earlier)
                {
                    // Older ones go in front, skipping any the cache already has.
                    int at = 0;
                    for (int i = 0; i < h.messages.Count; i++)
                    {
                        var m = h.messages[i];
                        if (m == null || IndexOf(list, m.ts) >= 0) continue;
                        list.Insert(at++, m);
                        Note(m);
                    }
                }
                else
                {
                    for (int i = 0; i < h.messages.Count; i++)
                    {
                        var m = h.messages[i];
                        if (m == null || IndexOf(list, m.ts) >= 0) continue;
                        InsertByTime(list, m);
                        Note(m);
                    }
                }
            }
            hasMore[channelId] = h.hasMore;
            HistoryChanged?.Invoke(channelId);
        }, error =>
        {
            loading.Remove(channelId);
            Debug.LogWarning($"[SlackStore] history {channelId}: {error}");
            HistoryChanged?.Invoke(channelId);  // so the panel can drop its "Loading…"
        });
    }

    static void OnMessage(NotifyMessage m)
    {
        if (m == null || string.IsNullOrEmpty(m.channel)) return;
        Note(m);
        var list = History(m.channel);
        bool appended = false;
        int at = list != null ? IndexOf(list, m.ts) : -1;
        if (list != null && at < 0)
        {
            InsertByTime(list, m);
            appended = true;
        }
        else if (at >= 0 && echoes.Remove(m.ts))
        {
            list[at] = m;  // the hub's copy of a message sent from here: it knows the thread and the initials
            HistoryChanged?.Invoke(m.channel);
        }
        var c = Find(m.channel);
        if (c != null)
        {
            c.lastTs = m.ts;
            c.lastText = m.text;
            if (!m.isMe)
            {
                // Counted here so the list reacts at once; the hub's notify.channels is the truth and overwrites it.
                c.unread++;
                if (m.mentionsMe) c.mentions++;
            }
            ChannelsChanged?.Invoke();
        }
        if (appended) MessageAdded?.Invoke(m.channel, m);
    }

    /// <summary>
    /// The user's own message, just sent from the headset: into the conversation right away. The hub also sends it as
    /// notify.message (and notify.sent), so whichever lands first adds it and the others are no-ops (same ts).
    /// </summary>
    public static void AddOwn(string channelId, string text, string ts, string threadTs = null)
    {
        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(text)) return;
        var list = History(channelId);
        if (list != null && IndexOf(list, ts) >= 0) return;
        var c = Find(channelId);
        bool dm = c != null && SlackText.IsDmKind(c.kind);
        echoes.Add(ts);
        OnMessage(new NotifyMessage
        {
            channel = channelId,
            channelName = dm ? c.kind : SlackText.Bare(c?.name) ?? "",
            isDm = dm,
            user = MeId,
            userName = string.IsNullOrEmpty(MeName) ? "You" : MeName,
            text = text,
            ts = ts,
            threadTs = threadTs,
            isMe = true,
            source = IsIMessageSource(null, channelId) ? "imessage" : "slack",
        });
    }

    // notify.sent: covers a send from the floating card or by voice ("send it") on a hub that predates notify.message
    // for its own posts. A thread reply lands without its thread here; the hub's notify.message carries it.
    static void OnSent(NotifySent s)
    {
        if (s != null) AddOwn(s.channel, s.text, s.ts);
    }

    static void OnDigest(NotifyDigest d)
    {
        if (d?.messages == null) return;
        for (int i = 0; i < d.messages.Count; i++) Note(d.messages[i]);
    }

    /// <summary>Learn the sender's name and keep the message in <see cref="Recent"/> (deduped by channel + ts).</summary>
    static void Note(NotifyMessage m)
    {
        if (m == null) return;
        if (!string.IsNullOrEmpty(m.user) && !string.IsNullOrEmpty(m.userName)) userNames[m.user] = m.userName;
        if (m.isMe && !string.IsNullOrEmpty(m.user) && string.IsNullOrEmpty(MeId)) MeId = m.user;
        for (int i = 0; i < Recent.Count; i++)
            if (Recent[i].ts == m.ts && Recent[i].channel == m.channel) return;
        double t = SlackText.Ts(m.ts);
        int at = Recent.Count;
        for (int i = 0; i < Recent.Count; i++)
            if (SlackText.Ts(Recent[i].ts) < t) { at = i; break; }
        Recent.Insert(at, m);
        if (Recent.Count > RecentCap) Recent.RemoveAt(Recent.Count - 1);
    }

    /// <summary>A user's name from what has been seen so far, else null.</summary>
    public static string NameOf(string userId) =>
        userId != null && userNames.TryGetValue(userId, out string name) ? name : null;

    // Oldest first; Slack ts is "<seconds>.<micros>" so a numeric compare orders it.
    static void InsertByTime(List<NotifyMessage> list, NotifyMessage m)
    {
        double t = SlackText.Ts(m.ts);
        int at = list.Count;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (SlackText.Ts(list[i].ts) <= t) { at = i + 1; break; }
            at = i;
        }
        list.Insert(at, m);
    }

    static int IndexOf(List<NotifyMessage> list, string ts)
    {
        if (string.IsNullOrEmpty(ts)) return -1;
        for (int i = list.Count - 1; i >= 0; i--) if (list[i].ts == ts) return i;
        return -1;
    }
}
