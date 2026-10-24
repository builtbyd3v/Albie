using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// "New for you" (MentionsPanel.prefab: a ForgePanel variant, panel id <c>messages-mentions</c>, slot Left of the MSG
/// mode): unread DMs and mentions across conversations as <see cref="MessageCard"/>s, newest first, capped at
/// <see cref="MaxRows"/> with a "+N more" line. Rows come from <see cref="SlackStore"/>: the conversations with unread
/// counts (notify.channels) and the recent messages known for them (history, notify.message, the digest); a conversation
/// with unread but no known messages shows its last line. Tapping a row opens that conversation in the Slack panel
/// (<see cref="SlackPanel.Active"/>.Open, or MSG mode with it pending) and the row clears once the hub marks it seen.
/// <see cref="floating"/> copies (DigestTrigger, beside Bit while MSG mode is closed) show the digest's messages instead
/// and open MSG mode when tapped. Rebuilt only when data or size changes; rows are pooled. This replaces the old
/// MessagesPanel.
/// </summary>
public class MentionsPanel : MonoBehaviour
{
    public const int MaxRows = 8;
    public const int FloatingRows = 4;
    const float RowGap = 6f, HeadH = 16f, FooterH = 18f;
    static readonly WaitForSeconds ClockTick = new WaitForSeconds(30f);

    [Tooltip("Spawned by DigestTrigger beside Bit while MSG mode is closed: shows the last digest; tapping opens MSG mode.")]
    public bool floating;

    public ForgePanel Panel { get; private set; }

    RectTransform content, list;
    TextMeshProUGUI heading, footer, empty;
    MessageCard template;
    readonly List<MessageCard> cards = new List<MessageCard>(MaxRows);
    readonly List<NotifyMessage> items = new List<NotifyMessage>(MaxRows + 1);
    readonly Dictionary<string, NotifyMessage> stubs = new Dictionary<string, NotifyMessage>();  // per channel: "last line" rows
    NotifyDigest digest;
    int shown, more;
    bool dataDirty, layoutDirty;
    Vector2 lastSize;
    Coroutine clock;

    void Awake()
    {
        Panel = GetComponent<ForgePanel>();
        content = Panel != null ? Panel.Content : (RectTransform)transform;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        heading = SlackUi.OverlineText(content, "Heading");
        heading.text = "New for you";
        list = SlackUi.Rect(content, "List");
        footer = SlackUi.Text(content, "Footer", SlackUi.Small, SlackUi.Subtext, align: TextAlignmentOptions.Center);
        empty = SlackUi.Text(content, "Empty", SlackUi.Body, SlackUi.Subtext, align: TextAlignmentOptions.Center, wrap: true);
        template = Resources.Load<MessageCard>("MessageCard");
        if (template == null) Debug.LogWarning("[MentionsPanel] Resources/MessageCard.prefab not found; run Forge > Messages > Build Prefabs.");
    }

    void OnEnable()
    {
        SlackStore.Ensure();
        SlackStore.ChannelsChanged += MarkDirty;
        SlackStore.HistoryChanged += MarkDirty;
        SlackStore.MessageAdded += MarkDirty;
        ForgeEvents.OnNotifyDigest += OnDigest;
        ForgeNet.StatusChanged += MarkDirty;
        if (floating && digest == null && ForgeNet.TryGetLast(ForgeEvents.NotifyDigestType, out NotifyDigest last)) digest = last;
        dataDirty = layoutDirty = true;
        clock = StartCoroutine(Clock());
    }

    void OnDisable()
    {
        SlackStore.ChannelsChanged -= MarkDirty;
        SlackStore.HistoryChanged -= MarkDirty;
        SlackStore.MessageAdded -= MarkDirty;
        ForgeEvents.OnNotifyDigest -= OnDigest;
        ForgeNet.StatusChanged -= MarkDirty;
        if (clock != null) { StopCoroutine(clock); clock = null; }
    }

    void MarkDirty() => dataDirty = true;
    void MarkDirty(string _) => dataDirty = true;
    void MarkDirty(string _, NotifyMessage __) => dataDirty = true;
    void MarkDirty(ForgeNetStatus _) => dataDirty = true;

    void OnDigest(NotifyDigest d)
    {
        if (floating) digest = d;
        dataDirty = true;
    }

    /// <summary>The floating copy: show this digest (DigestTrigger sets it before the panel enables).</summary>
    public void ShowDigest(NotifyDigest d)
    {
        digest = d;
        dataDirty = true;
    }

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
            for (int i = 0; i < shown; i++) cards[i].RefreshTime();
        }
    }

    // ---- Data -> rows ----

    void Refresh()
    {
        dataDirty = false;
        layoutDirty = true;
        items.Clear();
        more = 0;
        int cap = floating ? FloatingRows : MaxRows;

        if (floating)
        {
            if (digest?.messages != null)
                for (int i = 0; i < digest.messages.Count && items.Count < cap; i++)
                    if (digest.messages[i] != null) items.Add(digest.messages[i]);
            more = digest != null ? Mathf.Max(0, digest.count - items.Count) : 0;
        }
        else
        {
            Collect();
            more = Mathf.Max(0, items.Count - cap);
            while (items.Count > cap) items.RemoveAt(items.Count - 1);
        }

        shown = items.Count;
        if (template != null)
        {
            for (int i = 0; i < shown; i++)
            {
                while (cards.Count <= i) cards.Add(NewCard());
                var m = items[i];
                SlackUi.Show(cards[i], true);
                cards[i].Bind(m, MessageCard.Style.Inbox, SlackText.Render(m.text, SlackStore.MeId, SlackStore.NameOf, SlackUi.Accent),
                    ChannelLabel(m));
            }
        }
        else shown = 0;
        for (int i = shown; i < cards.Count; i++) SlackUi.Show(cards[i], false);

        SlackUi.Show(footer, more > 0);
        if (more > 0) footer.text = "+" + more + " more";
        bool none = shown == 0;
        SlackUi.Show(empty, none);
        if (none)
        {
            bool live = ForgeNet.Status == ForgeNetStatus.Live;
            empty.text = !live ? "Hub offline" : floating || SlackStore.ChannelsKnown ? "You're all caught up" : "Loading…";
        }
        if (Panel != null && !floating)
        {
            int unread = SlackStore.TotalUnread;
            Panel.Subtitle = unread == 0 ? "" : unread == 1 ? "1 unread" : unread + " unread";
        }
    }

    // Unread DMs and mentions, newest first: for each conversation with unread, its newest known messages (as many as
    // are unread) that are DMs or mention the user; none known -> one row with the conversation's last line.
    void Collect()
    {
        var channels = SlackStore.Channels;
        for (int c = 0; c < channels.Count; c++)
        {
            var ch = channels[c];
            if (ch.unread <= 0) continue;
            bool dm = SlackText.IsDmKind(ch.kind);
            if (!dm && ch.mentions <= 0) continue;
            int found = 0;
            var recent = SlackStore.Recent;
            for (int i = 0; i < recent.Count && found < ch.unread; i++)
            {
                var m = recent[i];
                if (m.channel != ch.id || m.isMe) continue;
                found++;
                if (dm || m.mentionsMe) InsertNewestFirst(m);
            }
            if (found == 0 && !string.IsNullOrEmpty(ch.lastTs)) InsertNewestFirst(Stub(ch));
        }
    }

    void InsertNewestFirst(NotifyMessage m)
    {
        double t = SlackText.Ts(m.ts);
        int at = items.Count;
        for (int i = 0; i < items.Count; i++)
            if (SlackText.Ts(items[i].ts) < t) { at = i; break; }
        items.Insert(at, m);
    }

    // A row standing in for a conversation whose messages aren't loaded: its name as the sender, its last line as the text.
    NotifyMessage Stub(SlackChannel ch)
    {
        if (!stubs.TryGetValue(ch.id, out var m)) stubs[ch.id] = m = new NotifyMessage();
        m.channel = ch.id;
        m.channelName = ch.name;
        m.isDm = SlackText.IsDmKind(ch.kind);
        m.user = ch.id;
        m.userName = SlackText.IsDmKind(ch.kind) ? ch.name : "#" + ch.name;
        m.text = ch.lastText ?? "";
        m.ts = ch.lastTs;
        m.mentionsMe = ch.mentions > 0;
        return m;
    }

    static string ChannelLabel(NotifyMessage m)
    {
        var ch = SlackStore.Find(m.channel);
        return ch != null ? (SlackText.IsDmKind(ch.kind) ? "DM" : SlackText.DisplayName(ch)) : SlackText.ChannelLabel(m);
    }

    MessageCard NewCard()
    {
        var card = Instantiate(template, list);
        card.name = "Row";
        card.Clicked += OnRow;
        return card;
    }

    // Open the conversation in the Slack panel; from the floating copy, open MSG mode with it pending.
    void OnRow(MessageCard card)
    {
        var m = card.Message;
        if (m == null || string.IsNullOrEmpty(m.channel)) return;
        if (SlackPanel.Active != null) SlackPanel.Active.Open(m.channel);
        else
        {
            SlackPanel.PendingChannel = m.channel;
            var ws = Workspace.Current;
            if (ws != null && ws.ActiveMode != "messages") ws.Open("messages");
            else Debug.LogWarning("[MentionsPanel] No workspace to open MSG mode in.");
        }
    }

    // ---- Layout ----

    void Layout()
    {
        layoutDirty = false;
        float w = content.rect.width, h = content.rect.height;
        if (w < 10f || h < 10f) return;
        SlackUi.Band(heading.rectTransform, 0f, HeadH, 6f);
        float y = HeadH + 6f;
        SlackUi.Band(list, y, Mathf.Max(10f, h - y));
        float ly = 0f;
        for (int i = 0; i < shown; i++)
        {
            var card = cards[i];
            float ch = card.Measure(w);
            SlackUi.Band((RectTransform)card.transform, ly, ch);
            card.Layout(w, ch);
            ly += ch + RowGap;
        }
        SlackUi.Band(footer.rectTransform, y + ly, FooterH);
        SlackUi.Band(empty.rectTransform, y + 20f, 40f, 8f, 8f);
    }
}
