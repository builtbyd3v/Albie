using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Slack panel (SlackPanel.prefab: a ForgePanel variant, panel id <c>messages</c>, slot Main of the MSG mode),
/// laid out like the Slack app in its dark aubergine theme, simplified. Builds its UI at runtime under Content:
/// the sidebar on the left (the workspace's name, Channels A-Z with '#' or a lock, Direct messages with an avatar,
/// unread rows bold with a count pill, the open one lit), and the conversation on the right as a rounded pane: its
/// header ('#' name or avatar + person, topic), the messages as <see cref="SlackRow"/>s (oldest at the top, grouped
/// by sender, day dividers, a red "New" line above what was unread when it opened, "Load earlier" when the hub has
/// more, auto-scroll to the bottom unless you scrolled up) and the <see cref="SlackComposer"/>.
/// Data comes from <see cref="SlackStore"/> (notify.channels / notify.message / GET history); showing a conversation
/// and new messages in it POST /seen. A message's hover "Ask Albie" (and the composer's) sends messages.focus.
/// One open panel at a time is <see cref="Active"/>: the Mentions panel, DraftPresenter and SlackRemote (Albie's
/// messages_open) route through it. Lists are rebuilt only when data or the panel's size changes; rows are pooled.
/// Live hub only: offline it says so.
/// </summary>
public class SlackPanel : MonoBehaviour
{
    public const int MaxRows = 80;
    const string FocusType = "messages.focus";
    const string LeadPetId = "bit";
    // Geometry (mm).
    const float WindowCorner = 12f, PaneInset = 6f, PaneCorner = 10f;
    const float TeamH = 44f, SectionH = 28f, SideRowH = 28f, SideRowGap = 2f, SidePad = 8f;
    const float HeaderH = 48f, DayH = 36f, NewH = 22f, ComposerPad = 14f, GroupSeconds = 300f;

    /// <summary>The open Slack panel (MSG mode open), or null.</summary>
    public static SlackPanel Active { get; private set; }
    /// <summary>Enabled Slack panels in the scene.</summary>
    public static int ActiveCount { get; private set; }
    public static event Action ActiveChanged;
    /// <summary>A conversation to open as soon as a panel comes up (set by a Mentions row before opening MSG mode).</summary>
    public static string PendingChannel { get; set; }

    /// <summary>The ForgePanel this lives on; null in a bare test scene.</summary>
    public ForgePanel Panel { get; private set; }
    /// <summary>Id of the open conversation, or null.</summary>
    public string Selected => selected;

    sealed class SideRow
    {
        public SlackButton button;
        public TextMeshProUGUI glyph, name, count;
        public Image lockIcon;
        public ForgeSurface avatar, pill;
        public TextMeshProUGUI avatarText;
        public string id;
    }

    sealed class DayDivider
    {
        public RectTransform root;
        public ForgeSurface line, edge, fill;
        public TextMeshProUGUI label;
    }

    enum ItemKind : byte { Message, Day, New }

    struct Item
    {
        public ItemKind kind;
        public int index;  // into rows / days
    }

    RectTransform content;
    ForgeSurface window, pane, headLine;
    // Sidebar.
    TextMeshProUGUI team, channelsHead, dmsHead, leftEmpty;
    Image teamCaret, channelsCaret, dmsCaret;
    ScrollRect sideScroll;
    RectTransform sideContent;
    readonly List<SideRow> sideRows = new List<SideRow>(24);
    readonly List<SlackChannel> order = new List<SlackChannel>(24);
    int shownSide, channelSide;
    // Conversation.
    TextMeshProUGUI headGlyph, headName, headTopic, rightEmpty;
    Image headLock;
    ForgeSurface headAvatar;
    TextMeshProUGUI headAvatarText;
    ScrollRect msgScroll;
    RectTransform msgContent;
    SlackButton loadEarlier;
    readonly List<SlackRow> rows = new List<SlackRow>(MaxRows);
    readonly List<DayDivider> days = new List<DayDivider>(8);
    readonly List<Item> items = new List<Item>(MaxRows + 16);
    RectTransform newRoot;
    ForgeSurface newLine;
    TextMeshProUGUI newLabel;
    int shownRows, shownDays;
    SlackComposer composer;

    // Rendered rich text per message (the store's objects are stable), so a refresh re-renders only new ones.
    readonly Dictionary<NotifyMessage, string> rendered = new Dictionary<NotifyMessage, string>(128);

    string selected;
    string newMarkTs;      // the first message that was unread when the conversation opened: the "New" line sits above it
    int unreadAtOpen;      // its unread count then (MarkSeen zeroes it right away)
    bool built, dataDirty, layoutDirty, scrollToBottom = true, keepOffset;
    bool awaitingHistory;  // the open conversation's messages haven't arrived: hold the scroll-to-bottom until they do
    float lastContentH;
    Vector2 lastSize;

    static readonly Comparison<SlackChannel> ByName = (a, b) =>
        string.Compare(SlackText.PlainName(a), SlackText.PlainName(b), StringComparison.OrdinalIgnoreCase);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Active = null;
        ActiveCount = 0;
        ActiveChanged = null;
        PendingChannel = null;
    }

    void Awake()
    {
        Panel = GetComponent<ForgePanel>();
        content = Panel != null ? Panel.Content : (RectTransform)transform;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        Build();
    }

    void OnEnable()
    {
        ActiveCount++;
        if (Active == null) Active = this;
        SlackStore.Ensure();
        SlackStore.ChannelsChanged += OnChannels;
        SlackStore.HistoryChanged += OnHistory;
        SlackStore.MessageAdded += OnMessageAdded;
        ForgeEvents.OnNotifyDraft += OnDraft;
        ForgeEvents.OnNotifySent += OnSent;
        ForgeEvents.OnNotifyDraftCancelled += OnDraftCancelled;
        ForgeNet.StatusChanged += OnStatus;
        SlackStore.RefreshChannels();
        if (!string.IsNullOrEmpty(PendingChannel))
        {
            Open(PendingChannel);
            PendingChannel = null;
        }
        else if (selected != null) Open(selected);  // re-enabled: reload and mark seen
        dataDirty = layoutDirty = true;
        ActiveChanged?.Invoke();
    }

    void OnDisable()
    {
        SlackStore.ChannelsChanged -= OnChannels;
        SlackStore.HistoryChanged -= OnHistory;
        SlackStore.MessageAdded -= OnMessageAdded;
        ForgeEvents.OnNotifyDraft -= OnDraft;
        ForgeEvents.OnNotifySent -= OnSent;
        ForgeEvents.OnNotifyDraftCancelled -= OnDraftCancelled;
        ForgeNet.StatusChanged -= OnStatus;
        composer?.Disabled();
        ActiveCount = Mathf.Max(0, ActiveCount - 1);
        if (Active == this) Active = null;
        ActiveChanged?.Invoke();
    }

    // ---- Build (once) ----

    void Build()
    {
        if (built) return;
        built = true;
        // The window: aubergine everywhere, the conversation a dark rounded pane on it.
        window = SlackUi.Surface(content, "Window", SlackUi.SkWindow, WindowCorner);
        SlackUi.Stretch(window.rectTransform, 0f, 0f, 0f, 0f);

        // Sidebar.
        team = SlackUi.Text(content, "Team", 15f, Color.white, bold: true);
        team.overflowMode = TextOverflowModes.Ellipsis;
        teamCaret = SlackUi.Icon(content, "TeamCaret", "caret", SlackUi.SkSideText);
        sideScroll = SlackUi.ScrollList(content, "Conversations", out sideContent);
        channelsHead = SlackUi.Text(sideContent, "ChannelsHead", 12.5f, SlackUi.SkSideText);
        channelsHead.text = "Channels";
        channelsCaret = SlackUi.Icon(sideContent, "ChannelsCaret", "caret", SlackUi.SkSideText);
        dmsHead = SlackUi.Text(sideContent, "DmsHead", 12.5f, SlackUi.SkSideText);
        dmsHead.text = "Direct messages";
        dmsCaret = SlackUi.Icon(sideContent, "DmsCaret", "caret", SlackUi.SkSideText);
        leftEmpty = SlackUi.Text(content, "LeftEmpty", SlackUi.Small, SlackUi.SkSideText, wrap: true);

        // Conversation.
        pane = SlackUi.Surface(content, "Pane", SlackUi.SkPane, PaneCorner);
        headGlyph = SlackUi.Text(content, "HeadGlyph", 17f, SlackUi.SkName, bold: true, align: TextAlignmentOptions.Center);
        headGlyph.text = "#";
        headLock = SlackUi.Icon(content, "HeadLock", "lock", SlackUi.SkName);
        headAvatar = SlackUi.Surface(content, "HeadAvatar", SlackUi.BubbleColors[0], 5f);
        headAvatarText = SlackUi.Text(headAvatar.transform, "Initials", 9.5f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(headAvatarText.rectTransform, 0f, 0f, 0f, 0f);
        headName = SlackUi.Text(content, "Name", 16.5f, SlackUi.SkName, bold: true);
        headName.overflowMode = TextOverflowModes.Ellipsis;
        headTopic = SlackUi.Text(content, "Topic", 11.5f, SlackUi.SkMuted);
        headTopic.overflowMode = TextOverflowModes.Ellipsis;
        headLine = SlackUi.Surface(content, "HeadLine", SlackUi.SkLine, 0f);

        msgScroll = SlackUi.ScrollList(content, "Messages", out msgContent);
        loadEarlier = SlackButton.CreatePill(msgContent, "Load earlier messages", SlackButton.Ghost, LoadEarlier, 26f, 11.5f);
        SlackUi.Show(loadEarlier, false);
        newRoot = SlackUi.Rect(msgContent, "NewLine");
        newLine = SlackUi.Surface(newRoot, "Line", SlackUi.SkNew, 0f);
        newLabel = SlackUi.Text(newRoot, "Label", 11f, SlackUi.SkNew, bold: true, align: TextAlignmentOptions.Right);
        newLabel.text = "New";
        SlackUi.Show(newRoot, false);
        rightEmpty = SlackUi.Text(content, "RightEmpty", 13f, SlackUi.SkMuted, align: TextAlignmentOptions.Center, wrap: true);
        composer = new SlackComposer(content, this);
    }

    // ---- Events in: marked, handled once in LateUpdate ----

    void OnChannels() => dataDirty = true;

    void OnHistory(string channel)
    {
        if (channel == selected) dataDirty = true;
    }

    void OnMessageAdded(string channel, NotifyMessage m)
    {
        if (channel != selected) return;
        // Follow new messages while already at the bottom (a reader who scrolled up keeps their place); always follow
        // the user's own, like Slack does after you send.
        if (m.isMe || msgScroll.verticalNormalizedPosition <= 0.05f) scrollToBottom = true;
        dataDirty = true;
        if (!m.isMe) SlackStore.MarkSeen(selected);
    }

    void OnDraft(NotifyDraft d)
    {
        if (Active == this) composer.ReceiveDraft(d);
    }

    void OnSent(NotifySent s) => composer.OnSent(s);
    void OnDraftCancelled(NotifyDraftCancelled c) => composer.OnDraftCancelled(c);

    void OnStatus(ForgeNetStatus status)
    {
        composer.OnStatus();
        if (status == ForgeNetStatus.Live && selected != null) SlackStore.LoadHistory(selected);
        dataDirty = true;
    }

    public void MarkLayoutDirty() => layoutDirty = true;

    // ---- Actions ----

    /// <summary>Show a conversation: load its history if needed, mark it seen, scroll to the newest message.</summary>
    public void Open(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return;
        bool changed = channelId != selected;
        selected = channelId;
        scrollToBottom = true;
        keepOffset = false;
        if (changed)
        {
            var c = SlackStore.Find(channelId);
            unreadAtOpen = c != null ? c.unread : 0;  // before MarkSeen clears it
            newMarkTs = null;
        }
        if (SlackStore.History(channelId) == null) SlackStore.LoadHistory(channelId);
        if (isActiveAndEnabled) SlackStore.MarkSeen(channelId);
        composer.SetChannel(SlackStore.Find(channelId));
        if (changed && msgScroll != null) msgScroll.velocity = Vector2.zero;
        dataDirty = layoutDirty = true;
    }

    // "Load earlier": remember the content height so the view holds still when the older messages land at the top.
    void LoadEarlier()
    {
        if (selected == null || SlackStore.IsLoading(selected)) return;
        keepOffset = true;
        lastContentH = msgContent.rect.height;
        SlackStore.LoadHistory(selected, earlier: true);
        dataDirty = true;
    }

    /// <summary>The newest message of the open conversation, or null.</summary>
    public NotifyMessage LatestMessage()
    {
        var list = SlackStore.History(selected);
        return list != null && list.Count > 0 ? list[list.Count - 1] : null;
    }

    /// <summary>
    /// messages.focus {pet_id, ts, channel}: Albie reads the message aloud, asks whether to answer, and seeds the reply
    /// thread, so what you say next becomes a draft (notify.draft -> the composer). False when the hub isn't live.
    /// </summary>
    public bool FocusBit(NotifyMessage m)
    {
        if (m == null || string.IsNullOrEmpty(m.ts)) return false;
        bool sent = ForgeNet.Send(FocusType, new JObject { ["pet_id"] = LeadPetId, ["ts"] = m.ts, ["channel"] = m.channel ?? selected ?? "" });
        if (!sent) Debug.LogWarning($"[SlackPanel] Hub not live; {FocusType} for ts {m.ts} dropped.");
        return sent;
    }

    /// <summary>notify.draft routed here by DraftPresenter while this panel is open.</summary>
    public void ReceiveDraft(NotifyDraft d) => composer.ReceiveDraft(d);

    // ---- Frame ----

    void Update() => composer?.Tick(Time.deltaTime);

    void LateUpdate()
    {
        Vector2 size = content.rect.size;
        if ((size - lastSize).sqrMagnitude > 0.01f)
        {
            lastSize = size;
            layoutDirty = true;
        }
        if (dataDirty) Refresh();
        if (layoutDirty) Layout();
    }

    // ---- Data -> views ----

    void Refresh()
    {
        dataDirty = false;
        layoutDirty = true;
        bool live = ForgeNet.Status == ForgeNetStatus.Live;

        if (selected == null)
        {
            var first = SlackStore.DefaultChannel();
            if (first != null) { Open(first.id); dataDirty = false; }
        }
        var channel = SlackStore.Find(selected);

        // Sidebar: the workspace, channels A-Z, then DMs in the hub's order (unread first, then most recent).
        team.text = string.IsNullOrEmpty(SlackStore.TeamName) ? "Slack" : SlackStore.TeamName;
        order.Clear();
        for (int i = 0; i < SlackStore.Channels.Count; i++)
            if (!SlackText.IsDmKind(SlackStore.Channels[i].kind)) order.Add(SlackStore.Channels[i]);
        order.Sort(ByName);
        channelSide = order.Count;
        for (int i = 0; i < SlackStore.Channels.Count; i++)
            if (SlackText.IsDmKind(SlackStore.Channels[i].kind)) order.Add(SlackStore.Channels[i]);
        shownSide = order.Count;
        for (int i = 0; i < shownSide; i++)
        {
            while (sideRows.Count <= i) sideRows.Add(NewSideRow());
            FillSideRow(sideRows[i], order[i]);
        }
        for (int i = shownSide; i < sideRows.Count; i++) SlackUi.Show(sideRows[i].button, false);
        SlackUi.Show(channelsHead, channelSide > 0);
        SlackUi.Show(channelsCaret, channelSide > 0);
        SlackUi.Show(dmsHead, shownSide > channelSide);
        SlackUi.Show(dmsCaret, shownSide > channelSide);
        bool noList = shownSide == 0;
        SlackUi.Show(leftEmpty, noList);
        if (noList) leftEmpty.text = !live ? "Hub offline" : SlackStore.ChannelsKnown ? "No conversations" : "Loading…";

        // Header + the frame's subtitle.
        bool dm = channel != null && SlackText.IsDmKind(channel.kind);
        bool locked = channel != null && channel.kind == "private";
        headName.text = channel != null ? SlackText.PlainName(channel) : "";
        headTopic.text = channel != null && !string.IsNullOrEmpty(channel.topic) ? channel.topic : "";
        SlackUi.Show(headGlyph, channel != null && !dm && !locked);
        SlackUi.Show(headLock, locked);
        SlackUi.Show(headAvatar, dm);
        if (dm)
        {
            headAvatarText.text = channel.kind == "group-dm" ? GroupCount(channel) : SlackText.Initials(channel.name);
            headAvatar.color = SlackUi.BubbleColor(channel.name ?? channel.id);
        }
        if (Panel != null)
        {
            int unread = SlackStore.TotalUnread;
            Panel.Subtitle = channel == null ? (live ? "" : "Hub offline")
                : SlackText.DisplayName(channel) + " · " + (unread == 0 ? "all read" : unread == 1 ? "1 unread" : unread + " unread");
        }

        // Messages, with day dividers and the "New" line.
        var list = SlackStore.History(selected);
        bool loading = SlackStore.IsLoading(selected);
        awaitingHistory = selected != null && live && (list == null || (loading && list.Count == 0));
        if (unreadAtOpen > 0 && list != null && !loading)
        {
            newMarkTs = FirstUnread(list, unreadAtOpen);
            unreadAtOpen = 0;
        }
        items.Clear();
        shownRows = shownDays = 0;
        bool newShown = false;
        if (list != null)
        {
            int start = Mathf.Max(0, list.Count - MaxRows);
            NotifyMessage prev = null;
            DateTime prevDay = DateTime.MinValue;
            for (int i = start; i < list.Count; i++)
            {
                var m = list[i];
                DateTime day = SlackText.Local(m.ts).Date;
                bool newDay = prev == null || day != prevDay;
                if (newDay)
                {
                    while (days.Count <= shownDays) days.Add(NewDay());
                    var d = days[shownDays];
                    d.label.text = SlackText.DayLabel(day);
                    SlackUi.Show(d.root, true);
                    items.Add(new Item { kind = ItemKind.Day, index = shownDays++ });
                }
                bool isNew = !newShown && newMarkTs != null && m.ts == newMarkTs;
                if (isNew)
                {
                    newShown = true;
                    items.Add(new Item { kind = ItemKind.New });
                }
                bool grouped = !newDay && !isNew && prev != null && prev.user == m.user && !string.IsNullOrEmpty(m.user)
                               && SlackText.Ts(m.ts) - SlackText.Ts(prev.ts) < GroupSeconds && !SlackText.IsThreadReply(m)
                               && !SlackText.IsThreadReply(prev);
                while (rows.Count <= shownRows) rows.Add(NewRow());
                var row = rows[shownRows];
                SlackUi.Show(row, true);
                row.Bind(m, Rendered(m), grouped);
                items.Add(new Item { kind = ItemKind.Message, index = shownRows++ });
                prev = m;
                prevDay = day;
            }
        }
        for (int i = shownRows; i < rows.Count; i++) SlackUi.Show(rows[i], false);
        for (int i = shownDays; i < days.Count; i++) SlackUi.Show(days[i].root, false);
        SlackUi.Show(newRoot, newShown);
        bool more = list != null && SlackStore.HasMore(selected);
        SlackUi.Show(loadEarlier, more);
        if (more)
        {
            loadEarlier.SetText(loading ? "Loading…" : "Load earlier messages");
            loadEarlier.Interactable = !loading;
        }
        bool empty = shownRows == 0;
        SlackUi.Show(rightEmpty, empty);
        if (empty)
        {
            rightEmpty.text = !live ? "Hub offline\n<size=85%><alpha=#AA>Start the hub (uvicorn main:app) and this connects by itself."
                : selected == null ? "Pick a conversation"
                : loading || list == null ? "Loading…"
                : dm ? "This conversation is just between you and " + SlackText.PlainName(channel) + ".\n<size=85%><alpha=#AA>Say hi below."
                : "This is the very beginning of " + SlackText.DisplayName(channel) + ".";
        }
    }

    // The message the "New" line goes above: walking back from the newest, the n-th one that isn't the user's own.
    static string FirstUnread(List<NotifyMessage> list, int n)
    {
        string ts = null;
        for (int i = list.Count - 1; i >= 0 && n > 0; i--)
        {
            if (list[i].isMe) continue;
            ts = list[i].ts;
            n--;
        }
        return ts;
    }

    static string GroupCount(SlackChannel c)
    {
        if (string.IsNullOrEmpty(c.name)) return "2";
        int people = 1;
        for (int i = 0; i < c.name.Length; i++) if (c.name[i] == ',') people++;
        return people.ToString();
    }

    string Rendered(NotifyMessage m)
    {
        if (rendered.TryGetValue(m, out string s)) return s;
        if (rendered.Count > 400) rendered.Clear();
        s = SlackText.RenderSlack(m.text, SlackStore.MeId, SlackStore.NameOf);
        rendered[m] = s;
        return s;
    }

    SideRow NewSideRow()
    {
        var r = new SideRow();
        r.button = SlackButton.Create(sideContent, "Conversation", SlackButton.SideRow, 6f, null);
        r.button.Button.onClick.AddListener(() => Open(r.id));
        r.glyph = SlackUi.Text(r.button.transform, "Hash", 14f, SlackUi.SkSideText, align: TextAlignmentOptions.Center);
        r.glyph.text = "#";
        r.lockIcon = SlackUi.Icon(r.button.transform, "Lock", "lock", SlackUi.SkSideText);
        r.avatar = SlackUi.Surface(r.button.transform, "Avatar", SlackUi.BubbleColors[0], 4f);
        r.avatarText = SlackUi.Text(r.avatar.transform, "Initials", 8f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(r.avatarText.rectTransform, 0f, 0f, 0f, 0f);
        r.name = SlackUi.Text(r.button.transform, "Name", 13f, SlackUi.SkSideText);
        r.name.overflowMode = TextOverflowModes.Ellipsis;
        r.pill = SlackUi.PillSurface(r.button.transform, "Unread", SlackUi.SkBadge);
        r.count = SlackUi.Text(r.pill.transform, "Count", 10.5f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(r.count.rectTransform, 0f, 0f, 0f, 0f);
        return r;
    }

    void FillSideRow(SideRow r, SlackChannel c)
    {
        r.id = c.id;
        SlackUi.Show(r.button, true);
        bool on = c.id == selected, unread = c.unread > 0, dm = SlackText.IsDmKind(c.kind), locked = c.kind == "private";
        r.button.On = on;
        r.name.text = SlackText.PlainName(c);
        SlackUi.SetBold(r.name, unread);
        Color ink = on || unread ? Color.white : SlackUi.SkSideText;
        r.name.color = ink;
        r.glyph.color = ink;
        r.lockIcon.color = ink;
        SlackUi.Show(r.glyph, !dm && !locked);
        SlackUi.Show(r.lockIcon, locked);
        SlackUi.Show(r.avatar, dm);
        if (dm)
        {
            r.avatarText.text = c.kind == "group-dm" ? GroupCount(c) : SlackText.Initials(c.name);
            r.avatar.color = SlackUi.BubbleColor(c.name ?? c.id);
        }
        // Like Slack: a count for DMs and for mentions; a channel that is only unread just goes bold.
        bool badge = unread && (dm || c.mentions > 0);
        SlackUi.Show(r.pill, badge);
        if (badge)
        {
            int n = dm ? c.unread : c.mentions;
            r.count.text = n > 99 ? "99+" : n.ToString();
        }
    }

    SlackRow NewRow()
    {
        var row = SlackRow.Create(msgContent);
        row.AskAlbie += OnAskAlbie;
        return row;
    }

    void OnAskAlbie(SlackRow row)
    {
        if (!FocusBit(row.Message)) composer.ShowStatus("Hub offline");
        else composer.ShowStatus("Albie is reading it…");
    }

    DayDivider NewDay()
    {
        var d = new DayDivider { root = SlackUi.Rect(msgContent, "Day") };
        d.line = SlackUi.Surface(d.root, "Line", SlackUi.SkLine, 0f);
        d.edge = SlackUi.PillSurface(d.root, "Edge", SlackUi.White(0.16f));
        d.fill = SlackUi.PillSurface(d.edge.transform, "Fill", SlackUi.SkPane);
        SlackUi.Stretch(d.fill.rectTransform, 1.2f, 1.2f, 1.2f, 1.2f);
        d.label = SlackUi.Text(d.edge.transform, "Label", 11f, SlackUi.SkName, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(d.label.rectTransform, 0f, 0f, 0f, 0f);
        return d;
    }

    // ---- Layout (on data or size change) ----

    void Layout()
    {
        layoutDirty = false;
        float w = content.rect.width, h = content.rect.height;
        if (w < 10f || h < 10f) return;
        float sideW = Mathf.Clamp(w * 0.25f, 160f, 230f);

        // Sidebar.
        float teamW = Mathf.Min(SlackUi.Width(team, team.text) + 2f, sideW - 2f * SidePad - 20f);
        SlackUi.Place(team.rectTransform, SidePad + 6f, 8f, teamW, TeamH - 16f);
        SlackUi.Place(teamCaret.rectTransform, SidePad + 6f + teamW + 4f, (TeamH - 12f) * 0.5f, 12f, 12f);
        SlackUi.Place((RectTransform)sideScroll.transform, 0f, TeamH, sideW, h - TeamH);
        float y = 4f;
        for (int i = 0; i < shownSide; i++)
        {
            if (i == 0 && channelSide > 0) y = Section(channelsHead, channelsCaret, y);
            if (i == channelSide) y = Section(dmsHead, dmsCaret, y + (i > 0 ? 8f : 0f));
            var r = sideRows[i];
            SlackUi.Band(r.button.Rect, y, SideRowH, SidePad, SidePad);
            float iconX = 10f, iconW = 16f;
            SlackUi.Place(r.glyph.rectTransform, iconX, 0f, iconW, SideRowH);
            SlackUi.Place(r.lockIcon.rectTransform, iconX + 2f, (SideRowH - 12f) * 0.5f, 12f, 12f);
            SlackUi.Place(r.avatar.rectTransform, iconX, (SideRowH - 17f) * 0.5f, 17f, 17f);
            float right = 8f;
            if (r.pill.gameObject.activeSelf)
            {
                float pw = Mathf.Max(20f, SlackUi.Width(r.count, r.count.text) + 12f);
                SlackUi.PlaceRight(r.pill.rectTransform, right, (SideRowH - 17f) * 0.5f, pw, 17f);
                right += pw + 6f;
            }
            SlackUi.Band(r.name.rectTransform, 0f, SideRowH, iconX + iconW + 8f, right);
            y += SideRowH + SideRowGap;
        }
        sideContent.sizeDelta = new Vector2(0f, Mathf.Max(h - TeamH, y + 8f));
        SlackUi.Place(leftEmpty.rectTransform, SidePad + 6f, TeamH + 8f, sideW - 2f * SidePad - 6f, 60f);

        // The conversation pane.
        float px = sideW, pw2 = w - sideW - PaneInset, py = PaneInset, ph = h - 2f * PaneInset;
        SlackUi.Place(pane.rectTransform, px, py, pw2, ph);
        float hx = px + 18f;
        bool avatarHead = headAvatar.gameObject.activeSelf;
        float iconW2 = avatarHead ? 24f : 16f;
        SlackUi.Place(headGlyph.rectTransform, hx - 2f, py, iconW2, HeaderH);
        SlackUi.Place(headLock.rectTransform, hx, py + (HeaderH - 15f) * 0.5f, 15f, 15f);
        SlackUi.Place(headAvatar.rectTransform, hx, py + (HeaderH - 24f) * 0.5f, 24f, 24f);
        float nameX = hx + iconW2 + 6f;
        float nameW = Mathf.Min(SlackUi.Width(headName, headName.text) + 2f, pw2 * 0.55f);
        SlackUi.Place(headName.rectTransform, nameX, py, nameW, HeaderH);
        float topicX = nameX + nameW + 14f;
        SlackUi.Place(headTopic.rectTransform, topicX, py + 2f, Mathf.Max(10f, px + pw2 - 18f - topicX), HeaderH);
        SlackUi.Place(headLine.rectTransform, px, py + HeaderH, pw2, 1.2f);

        float composerH = composer.Height;
        float composerY = py + ph - ComposerPad * 0.5f - composerH;
        float listY = py + HeaderH + 2f, listH = Mathf.Max(20f, composerY - 6f - listY);
        SlackUi.Place((RectTransform)msgScroll.transform, px, listY, pw2, listH);
        y = 6f;
        if (loadEarlier.gameObject.activeSelf)
        {
            float lw = loadEarlier.Rect.sizeDelta.x;
            SlackUi.Place(loadEarlier.Rect, (pw2 - lw) * 0.5f, y, lw, 26f);
            y += 34f;
        }
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            switch (it.kind)
            {
                case ItemKind.Day:
                {
                    var d = days[it.index];
                    SlackUi.Band(d.root, y, DayH);
                    SlackUi.Place(d.line.rectTransform, 0f, DayH * 0.5f, pw2, 1.2f);
                    float lw = SlackUi.Width(d.label, d.label.text) + 26f;
                    SlackUi.Place(d.edge.rectTransform, (pw2 - lw) * 0.5f, (DayH - 24f) * 0.5f, lw, 24f);
                    y += DayH;
                    break;
                }
                case ItemKind.New:
                    SlackUi.Band(newRoot, y, NewH);
                    SlackUi.Place(newLine.rectTransform, 0f, NewH * 0.5f, pw2 - 46f, 1.4f);
                    SlackUi.PlaceRight(newLabel.rectTransform, 12f, 0f, 30f, NewH);
                    y += NewH;
                    break;
                default:
                {
                    var row = rows[it.index];
                    float rh = row.Measure(pw2);
                    SlackUi.Band(row.Rect, y, rh);
                    row.Layout(pw2, rh);
                    y += rh;
                    break;
                }
            }
        }
        y += 8f;
        float contentH = Mathf.Max(listH, y);
        msgContent.sizeDelta = new Vector2(0f, contentH);
        SlackUi.Place(rightEmpty.rectTransform, px + 24f, listY + listH * 0.5f - 30f, pw2 - 48f, 60f);
        composer.Layout(px + ComposerPad, composerY, pw2 - 2f * ComposerPad);

        if (scrollToBottom && !awaitingHistory)
        {
            scrollToBottom = false;
            msgScroll.velocity = Vector2.zero;
            msgScroll.verticalNormalizedPosition = 0f;
        }
        else if (keepOffset)
        {
            // Older messages went in at the top: keep what was on screen where it was.
            keepOffset = false;
            float grown = contentH - lastContentH;
            if (grown > 0f) msgContent.anchoredPosition += new Vector2(0f, grown);
        }
    }

    // A sidebar section header ("▾ Channels") at y; returns the y below it.
    float Section(TextMeshProUGUI head, Image caret, float y)
    {
        SlackUi.Band(head.rectTransform, y, SectionH, SidePad + 26f, SidePad);
        SlackUi.Place(caret.rectTransform, SidePad + 10f, y + (SectionH - 11f) * 0.5f, 11f, 11f);
        return y + SectionH;
    }
}
