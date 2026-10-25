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
/// The same component with <see cref="iMessagePanel"/> on is the iMessage panel (IMessagePanel.prefab, added to MSG mode
/// by <see cref="MessagesLayout"/> when the hub has iMessage): only iMessage conversations, Messages' look (a "Messages"
/// list with round avatars and the last line, blue selection, the conversation as <see cref="MessageCard"/> bubbles,
/// yours blue on the right).
/// Data comes from <see cref="SlackStore"/> (notify.channels / notify.message / GET history); showing a conversation
/// and new messages in it POST /seen. A message's hover "Ask Albie" (and the composer's) sends messages.focus.
/// Each provider has one open panel (<see cref="Active"/> for Slack, <see cref="IMessageActive"/>; pick with
/// <see cref="ForChannel"/> / <see cref="ForSource"/>): the Mentions panel, DraftPresenter and SlackRemote (Albie's
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
    const float IMessageRowH = 50f, BubbleGap = 6f;
    static readonly Color IMessageWindow = new Color(0.11f, 0.11f, 0.12f), IMessagePane = new Color(0.04f, 0.04f, 0.05f);

    /// <summary>The open Slack panel (MSG mode open), or null.</summary>
    public static SlackPanel Active { get; private set; }
    /// <summary>The open iMessage panel (MSG mode open on a hub with iMessage), or null.</summary>
    public static SlackPanel IMessageActive { get; private set; }
    /// <summary>Enabled Slack panels in the scene.</summary>
    public static int ActiveCount { get; private set; }
    public static event Action ActiveChanged;
    /// <summary>A conversation to open as soon as a panel comes up (set by a Mentions row before opening MSG mode).</summary>
    public static string PendingChannel { get; set; }

    [Tooltip("When enabled, this panel only shows iMessage conversations. Leave disabled for Slack.")]
    public bool iMessagePanel;

    /// <summary>The ForgePanel this lives on; null in a bare test scene.</summary>
    public ForgePanel Panel { get; private set; }
    /// <summary>Id of the open conversation, or null.</summary>
    public string Selected => selected;
    public bool IsIMessagePanel => iMessagePanel;

    sealed class SideRow
    {
        public SlackButton button;
        public TextMeshProUGUI glyph, name, count, preview;
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

    enum ItemKind : byte { Message, Bubble, Day, New }

    struct Item
    {
        public ItemKind kind;
        public int index;  // into rows (Message), cards (Bubble) or days
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
    // iMessage bubbles (MessageCard.prefab, IMessage style).
    MessageCard template;
    readonly List<MessageCard> cards = new List<MessageCard>(MaxRows);
    int shownCards;
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
        IMessageActive = null;
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
        if (iMessagePanel) IMessageActive = this;
        else Active = this;
        SlackStore.Ensure();
        SlackStore.ChannelsChanged += OnChannels;
        SlackStore.HistoryChanged += OnHistory;
        SlackStore.MessageAdded += OnMessageAdded;
        ForgeEvents.OnNotifyDraft += OnDraft;
        ForgeEvents.OnNotifySent += OnSent;
        ForgeEvents.OnNotifyDraftCancelled += OnDraftCancelled;
        ForgeNet.StatusChanged += OnStatus;
        SlackStore.RefreshChannels();
        if (!string.IsNullOrEmpty(PendingChannel) && MatchesChannel(PendingChannel))
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
        if (IMessageActive == this) IMessageActive = null;
        ActiveChanged?.Invoke();
    }

    /// <summary>The open panel that shows <paramref name="channel"/> ("im:..." is iMessage), or null.</summary>
    public static SlackPanel ForChannel(string channel) => SlackStore.IsIMessageSource(null, channel) ? IMessageActive : Active;

    /// <summary>The open panel for a draft's or message's source ("imessage" or Slack), or null.</summary>
    public static SlackPanel ForSource(string source) => source == "imessage" ? IMessageActive : Active;

    bool MatchesChannel(string channel) => SlackStore.IsIMessageSource(null, channel) == iMessagePanel;

    // ---- Build (once) ----

    void Build()
    {
        if (built) return;
        built = true;
        // The window: aubergine everywhere (Messages: near-black), the conversation a dark rounded pane on it.
        window = SlackUi.Surface(content, "Window", iMessagePanel ? IMessageWindow : SlackUi.SkWindow, WindowCorner);
        SlackUi.Stretch(window.rectTransform, 0f, 0f, 0f, 0f);

        // Sidebar.
        team = SlackUi.Text(content, "Team", 15f, Color.white, bold: true);
        team.overflowMode = TextOverflowModes.Ellipsis;
        teamCaret = SlackUi.Icon(content, "TeamCaret", "caret", SlackUi.SkSideText);
        SlackUi.Show(teamCaret, !iMessagePanel);
        sideScroll = SlackUi.ScrollList(content, "Conversations", out sideContent);
        channelsHead = SlackUi.Text(sideContent, "ChannelsHead", 12.5f, SlackUi.SkSideText);
        channelsHead.text = "Channels";
        channelsCaret = SlackUi.Icon(sideContent, "ChannelsCaret", "caret", SlackUi.SkSideText);
        dmsHead = SlackUi.Text(sideContent, "DmsHead", 12.5f, SlackUi.SkSideText);
        dmsHead.text = "Direct messages";
        dmsCaret = SlackUi.Icon(sideContent, "DmsCaret", "caret", SlackUi.SkSideText);
        leftEmpty = SlackUi.Text(content, "LeftEmpty", SlackUi.Small, SlackUi.SkSideText, wrap: true);

        // Conversation.
        pane = SlackUi.Surface(content, "Pane", iMessagePanel ? IMessagePane : SlackUi.SkPane, PaneCorner);
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
        if (iMessagePanel)
        {
            template = Resources.Load<MessageCard>("MessageCard");
            if (template == null) Debug.LogWarning("[SlackPanel] Resources/MessageCard.prefab not found; run Forge > Messages > Build Prefabs.");
        }
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
        if (ForSource(d != null ? d.source : null) == this) composer.ReceiveDraft(d);
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
        if (string.IsNullOrEmpty(channelId) || !MatchesChannel(channelId)) return;
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
            var first = SlackStore.DefaultChannel(iMessagePanel);
            if (first != null) { Open(first.id); dataDirty = false; }
        }
        var channel = SlackStore.Find(selected);

        // Sidebar. Slack: the workspace, channels A-Z, then DMs in the hub's order (unread first, then most recent).
        // iMessage: "Messages", every chat in the hub's order, no sections.
        team.text = iMessagePanel ? "Messages" : string.IsNullOrEmpty(SlackStore.TeamName) ? "Slack" : SlackStore.TeamName;
        order.Clear();
        if (!iMessagePanel)
        {
            for (int i = 0; i < SlackStore.Channels.Count; i++)
            {
                var c = SlackStore.Channels[i];
                if (SlackStore.IsProvider(c, false) && !SlackText.IsDmKind(c.kind)) order.Add(c);
            }
            order.Sort(ByName);
        }
        channelSide = order.Count;
        for (int i = 0; i < SlackStore.Channels.Count; i++)
        {
            var c = SlackStore.Channels[i];
            if (SlackStore.IsProvider(c, iMessagePanel) && (iMessagePanel || SlackText.IsDmKind(c.kind))) order.Add(c);
        }
        shownSide = order.Count;
        for (int i = 0; i < shownSide; i++)
        {
            while (sideRows.Count <= i) sideRows.Add(NewSideRow());
            FillSideRow(sideRows[i], order[i]);
        }
        for (int i = shownSide; i < sideRows.Count; i++) SlackUi.Show(sideRows[i].button, false);
        SlackUi.Show(channelsHead, channelSide > 0);
        SlackUi.Show(channelsCaret, channelSide > 0);
        SlackUi.Show(dmsHead, !iMessagePanel && shownSide > channelSide);
        SlackUi.Show(dmsCaret, !iMessagePanel && shownSide > channelSide);
        bool noList = shownSide == 0;
        SlackUi.Show(leftEmpty, noList);
        if (noList) leftEmpty.text = !live ? "Hub offline" : SlackStore.ChannelsKnown ? "No conversations" : "Loading…";

        // Header + the frame's subtitle.
        bool dm = channel != null && SlackText.IsDmKind(channel.kind);
        bool locked = channel != null && channel.kind == "private";
        headName.text = channel != null ? SlackText.PlainName(channel) : "";
        headTopic.text = channel == null ? "" : iMessagePanel ? "iMessage" : !string.IsNullOrEmpty(channel.topic) ? channel.topic : "";
        SlackUi.Show(headGlyph, channel != null && !dm && !locked && !iMessagePanel);
        SlackUi.Show(headLock, locked);
        bool avatar = dm || (iMessagePanel && channel != null);
        SlackUi.Show(headAvatar, avatar);
        if (avatar)
        {
            headAvatar.Pill = iMessagePanel;  // Messages' contacts are round, Slack's squares
            headAvatarText.text = channel.kind == "group-dm" ? GroupCount(channel) : SlackText.Initials(channel.name);
            headAvatar.color = SlackUi.BubbleColor(channel.name ?? channel.id);
        }
        if (Panel != null)
        {
            int unread = 0;
            for (int i = 0; i < SlackStore.Channels.Count; i++)
                if (SlackStore.IsProvider(SlackStore.Channels[i], iMessagePanel)) unread += SlackStore.Channels[i].unread;
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
        shownRows = shownDays = shownCards = 0;
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
                if (iMessagePanel)
                {
                    if (template != null)
                    {
                        while (cards.Count <= shownCards) cards.Add(NewCard());
                        var card = cards[shownCards];
                        SlackUi.Show(card, true);
                        card.Bind(m, MessageCard.Style.IMessage, Rendered(m));
                        items.Add(new Item { kind = ItemKind.Bubble, index = shownCards++ });
                    }
                    prev = m;
                    prevDay = day;
                    continue;
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
        for (int i = shownCards; i < cards.Count; i++) SlackUi.Show(cards[i], false);
        for (int i = shownDays; i < days.Count; i++) SlackUi.Show(days[i].root, false);
        SlackUi.Show(newRoot, newShown);
        bool more = list != null && SlackStore.HasMore(selected);
        SlackUi.Show(loadEarlier, more);
        if (more)
        {
            loadEarlier.SetText(loading ? "Loading…" : "Load earlier messages");
            loadEarlier.Interactable = !loading;
        }
        bool empty = shownRows == 0 && shownCards == 0;
        SlackUi.Show(rightEmpty, empty);
        if (empty)
        {
            rightEmpty.text = !live ? "Hub offline\n<size=85%><alpha=#AA>Start the hub (uvicorn main:app) and this connects by itself."
                : selected == null ? "Pick a conversation"
                : loading || list == null ? "Loading…"
                : iMessagePanel ? "No messages here yet"
                : dm ?"This conversation is just between you and " + SlackText.PlainName(channel) + ".\n<size=85%><alpha=#AA>Say hi below."
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
        s = iMessagePanel ? SlackText.Render(m.text, SlackStore.MeId, SlackStore.NameOf, SlackUi.Accent)
            : SlackText.RenderSlack(m.text, SlackStore.MeId, SlackStore.NameOf);
        rendered[m] = s;
        return s;
    }

    SideRow NewSideRow()
    {
        var r = new SideRow();
        r.button = SlackButton.Create(sideContent, "Conversation", iMessagePanel ? SlackButton.IMessageRow : SlackButton.SideRow,
            iMessagePanel ? 10f : 6f, null);
        r.button.Button.onClick.AddListener(() => Open(r.id));
        r.glyph = SlackUi.Text(r.button.transform, "Hash", 14f, SlackUi.SkSideText, align: TextAlignmentOptions.Center);
        r.glyph.text = "#";
        r.lockIcon = SlackUi.Icon(r.button.transform, "Lock", "lock", SlackUi.SkSideText);
        r.avatar = SlackUi.Surface(r.button.transform, "Avatar", SlackUi.BubbleColors[0], 4f);
        r.avatar.Pill = iMessagePanel;
        r.avatarText = SlackUi.Text(r.avatar.transform, "Initials", iMessagePanel ? 12f : 8f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(r.avatarText.rectTransform, 0f, 0f, 0f, 0f);
        r.name = SlackUi.Text(r.button.transform, "Name", 13f, SlackUi.SkSideText);
        r.name.overflowMode = TextOverflowModes.Ellipsis;
        r.preview = SlackUi.Text(r.button.transform, "Preview", 11f, SlackUi.SkMuted);
        r.preview.overflowMode = TextOverflowModes.Ellipsis;
        SlackUi.Show(r.preview, iMessagePanel);
        r.pill = SlackUi.PillSurface(r.button.transform, "Unread", iMessagePanel ? SlackUi.IMessageBlue : SlackUi.SkBadge);
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
        SlackUi.Show(r.glyph, !dm && !locked && !iMessagePanel);
        SlackUi.Show(r.lockIcon, locked);
        bool avatar = dm || iMessagePanel;
        SlackUi.Show(r.avatar, avatar);
        if (avatar)
        {
            r.avatarText.text = c.kind == "group-dm" ? GroupCount(c) : SlackText.Initials(c.name);
            r.avatar.color = SlackUi.BubbleColor(c.name ?? c.id);
        }
        if (iMessagePanel)
        {
            // Messages' list: the last line under the name.
            var history = SlackStore.History(c.id);
            var latest = history != null && history.Count > 0 ? history[history.Count - 1] : null;
            string last = latest != null ? (latest.isMe ? "You: " : "") + latest.text : c.lastText;
            r.preview.text = string.IsNullOrEmpty(last) ? "iMessage" : last.Replace('\n', ' ');
            r.preview.color = on ? SlackUi.White(0.8f) : SlackUi.SkMuted;
        }
        // Like Slack: a count for DMs and for mentions; a channel that is only unread just goes bold.
        bool badge = unread && (dm || c.mentions > 0 || iMessagePanel);
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

    void OnAskAlbie(SlackRow row) => AskAlbie(row.Message);

    void AskAlbie(NotifyMessage m)
    {
        if (!FocusBit(m)) composer.ShowStatus("Hub offline");
        else composer.ShowStatus("Albie is reading it…");
    }

    // An iMessage bubble; tapping it asks Albie about it, like a Slack row's toolbar.
    MessageCard NewCard()
    {
        var card = Instantiate(template, msgContent);
        card.name = "Bubble";
        card.Clicked += c => AskAlbie(c.Message);
        return card;
    }

    DayDivider NewDay()
    {
        var d = new DayDivider { root = SlackUi.Rect(msgContent, "Day") };
        d.line = SlackUi.Surface(d.root, "Line", SlackUi.SkLine, 0f);
        d.edge = SlackUi.PillSurface(d.root, "Edge", SlackUi.White(0.16f));
        d.fill = SlackUi.PillSurface(d.edge.transform, "Fill", iMessagePanel ? IMessagePane : SlackUi.SkPane);
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
            float rowH = iMessagePanel ? IMessageRowH : SideRowH;
            SlackUi.Band(r.button.Rect, y, rowH, SidePad, SidePad);
            float iconX = 10f, iconW = iMessagePanel ? 34f : 16f, avatarS = iMessagePanel ? 34f : 17f;
            SlackUi.Place(r.glyph.rectTransform, iconX, 0f, iconW, rowH);
            SlackUi.Place(r.lockIcon.rectTransform, iconX + 2f, (rowH - 12f) * 0.5f, 12f, 12f);
            SlackUi.Place(r.avatar.rectTransform, iconX - (iMessagePanel ? 4f : 0f), (rowH - avatarS) * 0.5f, avatarS, avatarS);
            float right = 8f;
            if (r.pill.gameObject.activeSelf)
            {
                float pw = Mathf.Max(20f, SlackUi.Width(r.count, r.count.text) + 12f);
                SlackUi.PlaceRight(r.pill.rectTransform, right, (rowH - 17f) * 0.5f, pw, 17f);
                right += pw + 6f;
            }
            float textX = iconX + iconW + (iMessagePanel ? 4f : 8f);
            if (iMessagePanel)
            {
                SlackUi.Band(r.name.rectTransform, 7f, 19f, textX, right);
                SlackUi.Band(r.preview.rectTransform, 27f, 17f, textX, right);
            }
            else SlackUi.Band(r.name.rectTransform, 0f, rowH, textX, right);
            y += rowH + SideRowGap;
        }
        sideContent.sizeDelta = new Vector2(0f, Mathf.Max(h - TeamH, y + 8f));
        SlackUi.Place(leftEmpty.rectTransform, SidePad + 6f, TeamH + 8f, sideW - 2f * SidePad - 6f, 60f);

        // The conversation pane.
        float px = sideW, pw2 = w - sideW - PaneInset, py = PaneInset, ph = h - 2f * PaneInset;
        SlackUi.Place(pane.rectTransform, px, py, pw2, ph);
        float hx = px + 18f;
        bool avatarHead = headAvatar.gameObject.activeSelf;
        float headS = iMessagePanel ? 30f : 24f;
        float iconW2 = avatarHead ? headS : 16f;
        SlackUi.Place(headGlyph.rectTransform, hx - 2f, py, iconW2, HeaderH);
        SlackUi.Place(headLock.rectTransform, hx, py + (HeaderH - 15f) * 0.5f, 15f, 15f);
        SlackUi.Place(headAvatar.rectTransform, hx, py + (HeaderH - headS) * 0.5f, headS, headS);
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
                case ItemKind.Bubble:
                {
                    // Messages: as wide as the text needs, at most three quarters; yours on the right.
                    var card = cards[it.index];
                    float maxW = Mathf.Min(pw2 * 0.76f, 360f);
                    float bw = Mathf.Min(maxW, Mathf.Max(90f, card.PreferredBubbleWidth(maxW)));
                    float bh = card.Measure(bw);
                    float bx = card.Message != null && card.Message.isMe ? pw2 - bw - 14f : 14f;
                    SlackUi.Place((RectTransform)card.transform, bx, y, bw, bh);
                    card.Layout(bw, bh);
                    y += bh + BubbleGap;
                    break;
                }
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
