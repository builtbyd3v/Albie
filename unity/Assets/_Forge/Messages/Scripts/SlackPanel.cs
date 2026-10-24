using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Slack panel (SlackPanel.prefab: a ForgePanel variant, panel id <c>messages</c>, slot Main of the MSG mode).
/// Builds its UI at runtime under Content, like the CAD design panel: a conversation list on the left (Channels /
/// Direct messages, unread pills, mention dots, the open one lit), and on the right the conversation's header, its
/// messages as <see cref="MessageCard"/>s (oldest at the top, "Load earlier" when the hub has more, auto-scroll to the
/// bottom unless you scrolled up) and the <see cref="SlackComposer"/>. Data comes from <see cref="SlackStore"/>
/// (notify.channels / notify.message / GET history); showing a conversation and new messages in it POST /seen.
/// Tapping a message asks Bit to read it (messages.focus); the composer's Ask Bit does the same for the latest one.
/// One open panel at a time is <see cref="Active"/>: the Mentions panel, DigestTrigger and DraftPresenter route through
/// it. Lists are rebuilt only when data or the panel's size changes; rows are pooled. Live hub only: offline it says so.
/// </summary>
public class SlackPanel : MonoBehaviour
{
    public const int MaxCards = 80;
    const string FocusType = "messages.focus";
    const string LeadPetId = "bit";
    const float Gap = 14f, RowH = 34f, RowGap = 4f, HeadH = 16f, CardGap = 6f, MeInset = 36f, HeaderH = 46f;
    static readonly WaitForSeconds ClockTick = new WaitForSeconds(30f);

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

    sealed class ConvRow
    {
        public SlackButton button;
        public TextMeshProUGUI name, count;
        public ForgeSurface pill, dot, bar;
        public string id;
    }

    RectTransform content;
    // Left.
    ScrollRect convScroll;
    RectTransform convContent;
    TextMeshProUGUI channelsHead, dmsHead, leftEmpty;
    readonly List<ConvRow> convRows = new List<ConvRow>(16);
    int shownRows, channelRows;
    // Right.
    TextMeshProUGUI headName, headTopic, rightEmpty;
    ScrollRect msgScroll;
    RectTransform msgContent;
    SlackButton loadEarlier;
    MessageCard template;
    readonly List<MessageCard> cards = new List<MessageCard>(MaxCards);
    int shownCards;
    SlackComposer composer;

    // Rendered rich text per message (the store's objects are stable), so a refresh re-renders only new ones.
    readonly Dictionary<NotifyMessage, string> rendered = new Dictionary<NotifyMessage, string>(128);

    string selected;
    bool built, dataDirty, layoutDirty, scrollToBottom = true, keepOffset;
    float lastContentH;
    Vector2 lastSize;
    Coroutine clock;

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
        clock = StartCoroutine(Clock());
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
        if (clock != null) { StopCoroutine(clock); clock = null; }
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
        // Left: the conversation list.
        convScroll = SlackUi.ScrollList(content, "Conversations", out convContent);
        channelsHead = SlackUi.OverlineText(convContent, "ChannelsHead");
        channelsHead.text = "Channels";
        dmsHead = SlackUi.OverlineText(convContent, "DmsHead");
        dmsHead.text = "Direct messages";
        leftEmpty = SlackUi.Text(content, "LeftEmpty", SlackUi.Small, SlackUi.Subtext, wrap: true);

        // Right: header, messages, composer.
        headName = SlackUi.Text(content, "Name", SlackUi.Title, SlackUi.TextColor, bold: true);
        headName.overflowMode = TextOverflowModes.Ellipsis;
        headTopic = SlackUi.Text(content, "Topic", SlackUi.Small, SlackUi.Subtext);
        headTopic.overflowMode = TextOverflowModes.Ellipsis;
        msgScroll = SlackUi.ScrollList(content, "Messages", out msgContent);
        loadEarlier = SlackButton.CreatePill(msgContent, "Load earlier", SlackButton.Pill, LoadEarlier, 26f, 12f);
        SlackUi.Show(loadEarlier, false);
        rightEmpty = SlackUi.Text(content, "RightEmpty", SlackUi.Body, SlackUi.Subtext, align: TextAlignmentOptions.Center, wrap: true);
        template = Resources.Load<MessageCard>("MessageCard");
        if (template == null) Debug.LogWarning("[SlackPanel] Resources/MessageCard.prefab not found; run Forge > Messages > Build Prefabs.");
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
        // Follow new messages only while already at the bottom (a reader who scrolled up keeps their place).
        if (msgScroll.verticalNormalizedPosition <= 0.05f) scrollToBottom = true;
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
        if (SlackStore.History(channelId) == null) SlackStore.LoadHistory(channelId);
        if (isActiveAndEnabled) SlackStore.MarkSeen(channelId);
        composer.SetChannel(SlackStore.Find(channelId));
        if (changed && msgScroll != null) { msgScroll.velocity = Vector2.zero; }
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
    /// messages.focus {pet_id, ts, channel}: Bit reads the message aloud, asks whether to answer, and seeds the reply
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

    IEnumerator Clock()
    {
        while (true)
        {
            yield return ClockTick;
            for (int i = 0; i < shownCards; i++) cards[i].RefreshTime();
        }
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

        // Conversation rows: channels, then DMs.
        shownRows = 0;
        channelRows = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < SlackStore.Channels.Count; i++)
            {
                var c = SlackStore.Channels[i];
                bool dm = SlackText.IsDmKind(c.kind);
                if (dm != (pass == 1)) continue;
                while (convRows.Count <= shownRows) convRows.Add(NewConvRow());
                FillConvRow(convRows[shownRows++], c);
            }
            if (pass == 0) channelRows = shownRows;
        }
        for (int i = shownRows; i < convRows.Count; i++) SlackUi.Show(convRows[i].button, false);
        SlackUi.Show(channelsHead, channelRows > 0);
        SlackUi.Show(dmsHead, shownRows > channelRows);
        bool noList = shownRows == 0;
        SlackUi.Show(leftEmpty, noList);
        if (noList) leftEmpty.text = !live ? "Hub offline" : SlackStore.ChannelsKnown ? "No conversations" : "Loading…";

        // Header + subtitle.
        headName.text = channel != null ? SlackText.DisplayName(channel) : selected ?? "";
        headTopic.text = channel == null ? "" : !string.IsNullOrEmpty(channel.topic) ? channel.topic : SlackText.IsDmKind(channel.kind) ? "Direct message" : "";
        if (Panel != null)
        {
            int unread = SlackStore.TotalUnread;
            Panel.Subtitle = channel == null ? (live ? "" : "Hub offline")
                : SlackText.DisplayName(channel) + " · " + (unread == 0 ? "all read" : unread == 1 ? "1 unread" : unread + " unread");
        }

        // Messages.
        var list = SlackStore.History(selected);
        shownCards = 0;
        if (list != null && template != null)
        {
            int start = Mathf.Max(0, list.Count - MaxCards);
            for (int i = start; i < list.Count; i++)
            {
                var m = list[i];
                while (cards.Count <= shownCards) cards.Add(NewCard());
                var card = cards[shownCards++];
                SlackUi.Show(card, true);
                card.Bind(m, MessageCard.Style.Thread, Rendered(m));
            }
        }
        for (int i = shownCards; i < cards.Count; i++) SlackUi.Show(cards[i], false);
        bool more = list != null && SlackStore.HasMore(selected);
        SlackUi.Show(loadEarlier, more);
        if (more)
        {
            loadEarlier.SetText(SlackStore.IsLoading(selected) ? "Loading…" : "Load earlier");
            loadEarlier.Interactable = !SlackStore.IsLoading(selected);
        }
        bool empty = shownCards == 0;
        SlackUi.Show(rightEmpty, empty);
        if (empty)
        {
            rightEmpty.text = !live ? "Hub offline\n<size=85%><alpha=#AA>Start the hub (uvicorn main:app) and this connects by itself."
                : selected == null ? "Pick a conversation"
                : SlackStore.IsLoading(selected) || list == null ? "Loading…"
                : "No messages here yet";
        }
    }

    string Rendered(NotifyMessage m)
    {
        if (rendered.TryGetValue(m, out string s)) return s;
        if (rendered.Count > 400) rendered.Clear();
        s = SlackText.Render(m.text, SlackStore.MeId, SlackStore.NameOf, SlackUi.Accent);
        rendered[m] = s;
        return s;
    }

    ConvRow NewConvRow()
    {
        var r = new ConvRow();
        r.button = SlackButton.Create(convContent, "Conversation", SlackButton.Row, SlackUi.RowCorner, null);
        r.button.Button.onClick.AddListener(() => Open(r.id));
        r.bar = SlackUi.PillSurface(r.button.transform, "Bar", SlackUi.Accent);
        r.name = SlackUi.Text(r.button.transform, "Name", SlackUi.Name, SlackUi.TextColor);
        r.name.overflowMode = TextOverflowModes.Ellipsis;
        r.pill = SlackUi.PillSurface(r.button.transform, "Unread", SlackUi.AccentA(0.92f));
        r.count = SlackUi.Text(r.pill.transform, "Count", SlackUi.Time, SlackUi.Ink, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(r.count.rectTransform, 0f, 0f, 0f, 0f);
        r.dot = SlackUi.PillSurface(r.button.transform, "Mention", SlackUi.Accent, 0.4f);
        return r;
    }

    void FillConvRow(ConvRow r, SlackChannel c)
    {
        r.id = c.id;
        SlackUi.Show(r.button, true);
        bool on = c.id == selected, unread = c.unread > 0;
        r.button.On = on;
        r.button.Emphasis = unread;
        r.name.text = SlackText.DisplayName(c);
        r.name.fontStyle = unread || on ? FontStyles.Bold : FontStyles.Normal;
        r.name.color = unread || on ? Color.white : SlackUi.Muted;
        SlackUi.Show(r.bar, on);
        SlackUi.Show(r.pill, unread);
        if (unread) r.count.text = c.unread > 99 ? "99+" : c.unread.ToString();
        SlackUi.Show(r.dot, c.mentions > 0);
    }

    MessageCard NewCard()
    {
        var card = Instantiate(template, msgContent);
        card.name = "Message";
        card.Clicked += c => FocusBit(c.Message);
        return card;
    }

    // ---- Layout (on data or size change) ----

    void Layout()
    {
        layoutDirty = false;
        float w = content.rect.width, h = content.rect.height;
        if (w < 10f || h < 10f) return;
        float leftW = Mathf.Clamp(w * 0.28f, 150f, 240f);
        float x0 = leftW + Gap, rw = w - x0;

        // Left column.
        SlackUi.Place((RectTransform)convScroll.transform, 0f, 0f, leftW, h);
        float y = 0f;
        for (int i = 0; i < shownRows; i++)
        {
            if (i == 0 && channelRows > 0) { SlackUi.Band(channelsHead.rectTransform, y, HeadH, 8f); y += HeadH + 4f; }
            if (i == channelRows) { if (i > 0) y += 8f; SlackUi.Band(dmsHead.rectTransform, y, HeadH, 8f); y += HeadH + 4f; }
            var r = convRows[i];
            SlackUi.Band(r.button.Rect, y, RowH);
            SlackUi.Place(r.bar.rectTransform, 0f, 7f, 3f, RowH - 14f);
            float right = 8f;
            if (r.pill.gameObject.activeSelf)
            {
                float pw = Mathf.Max(20f, SlackUi.Width(r.count, r.count.text) + 12f);
                SlackUi.PlaceRight(r.pill.rectTransform, right, (RowH - 18f) * 0.5f, pw, 18f);
                right += pw + 6f;
            }
            if (r.dot.gameObject.activeSelf)
            {
                SlackUi.PlaceRight(r.dot.rectTransform, right, (RowH - 7f) * 0.5f, 7f, 7f);
                right += 7f + 6f;
            }
            SlackUi.Band(r.name.rectTransform, (RowH - 18f) * 0.5f, 18f, 12f, right + 4f);
            y += RowH + RowGap;
        }
        convContent.sizeDelta = new Vector2(0f, Mathf.Max(h, y));
        SlackUi.Place(leftEmpty.rectTransform, 6f, 8f, leftW - 12f, 60f);

        // Right column: header, messages, composer.
        SlackUi.Place(headName.rectTransform, x0 + 4f, 0f, rw - 8f, 24f);
        SlackUi.Place(headTopic.rectTransform, x0 + 4f, 24f, rw - 8f, 16f);
        float composerY = h - SlackComposer.Height;
        float listY = HeaderH, listH = Mathf.Max(20f, composerY - 10f - listY);
        SlackUi.Place((RectTransform)msgScroll.transform, x0, listY, rw, listH);
        y = 0f;
        if (loadEarlier.gameObject.activeSelf)
        {
            float pw = loadEarlier.Rect.sizeDelta.x;
            SlackUi.Place(loadEarlier.Rect, (rw - pw) * 0.5f, 2f, pw, 26f);
            y += 34f;
        }
        for (int i = 0; i < shownCards; i++)
        {
            var card = cards[i];
            float inset = card.Message != null && card.Message.isMe ? MeInset : 0f;
            float cw = rw - inset - 4f;
            float ch = card.Measure(cw);
            SlackUi.Band((RectTransform)card.transform, y, ch, inset, 4f);
            card.Layout(cw, ch);
            y += ch + CardGap;
        }
        y += 4f;
        float contentH = Mathf.Max(listH, y);
        msgContent.sizeDelta = new Vector2(0f, contentH);
        SlackUi.Place(rightEmpty.rectTransform, x0, listY + listH * 0.5f - 30f, rw, 60f);
        composer.Layout(x0, composerY, rw);

        if (scrollToBottom)
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
}
