using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// The Slack inbox (MessagesPanel.prefab, a ForgePanel variant, panel id <c>messages</c>; UI under Content).
/// Shows the newest <see cref="MaxCards"/> messages as <see cref="MessageCard"/>s and folds the rest into a
/// "+N more" footer. Fed by the hub's notify.* events (PROJECT_CONTEXT 7 / 8.8): notify.digest replaces the list,
/// notify.message pushes one card in at the top, notify.sent flashes the footer. When it opens live it reads
/// GET /messages/latest. Clicking a card asks Bit to reply (the hub's messages_send tool confirms before posting).
/// <see cref="floating"/> copies are spawned by <see cref="DigestTrigger"/> when the digest arrives with MSG mode
/// closed; they don't count as an open panel. Live hub only, no mocks.
/// </summary>
public class MessagesPanel : MonoBehaviour
{
    public const int MaxCards = 4;
    const string LatestPath = "/messages/latest?n=4";
    const float SentFlashSeconds = 2f;

    [SerializeField] RectTransform list;
    [SerializeField] MessageCard cardTemplate;
    [SerializeField] TMP_Text footer;
    [SerializeField] TMP_Text empty;
    [Tooltip("Spawned by DigestTrigger beside Bit while MSG mode is closed: shows the last digest and isn't counted as open.")]
    public bool floating;

    /// <summary>Enabled non-floating panels in the scene (MSG mode open).</summary>
    public static int ActiveCount { get; private set; }
    public static event Action ActiveChanged;

    readonly List<MessageCard> cards = new List<MessageCard>(MaxCards);
    readonly List<NotifyMessage> shown = new List<NotifyMessage>(MaxCards + 1);
    int more;
    Coroutine clock, flash;
    static readonly WaitForSeconds ClockTick = new WaitForSeconds(30f);
    static readonly WaitForSeconds FlashWait = new WaitForSeconds(SentFlashSeconds);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { ActiveCount = 0; ActiveChanged = null; }

    void Awake()
    {
        cardTemplate.gameObject.SetActive(false);
        for (int i = 0; i < MaxCards; i++)
        {
            var card = Instantiate(cardTemplate, list);
            card.name = "Card " + i;
            card.gameObject.SetActive(false);
            card.Clicked += ReplyWithBit;
            cards.Add(card);
        }
    }

    void OnEnable()
    {
        ForgeEvents.OnNotifyDigest += OnDigest;
        ForgeEvents.OnNotifyMessage += OnMessage;
        ForgeEvents.OnNotifySent += OnSent;
        if (!floating)
        {
            ActiveCount++;
            ActiveChanged?.Invoke();
        }

        if (!floating && ForgeNet.Status == ForgeNetStatus.Live) ForgeNet.Get<List<NotifyMessage>>(LatestPath, ShowLatest);
        else if (ForgeNet.TryGetLast(ForgeEvents.NotifyDigestType, out NotifyDigest digest)) OnDigest(digest);
        Render();
        clock = StartCoroutine(Clock());
    }

    void OnDisable()
    {
        ForgeEvents.OnNotifyDigest -= OnDigest;
        ForgeEvents.OnNotifyMessage -= OnMessage;
        ForgeEvents.OnNotifySent -= OnSent;
        if (clock != null) { StopCoroutine(clock); clock = null; }
        if (flash != null) { StopCoroutine(flash); flash = null; }
        if (!floating)
        {
            ActiveCount = Mathf.Max(0, ActiveCount - 1);
            ActiveChanged?.Invoke();
        }
    }

    // ---- hub -> panel ---------------------------------------------------------------------------------------

    void OnDigest(NotifyDigest d)
    {
        shown.Clear();
        if (d.messages != null) for (int i = 0; i < d.messages.Count && i < MaxCards; i++) shown.Add(d.messages[i]);
        more = Mathf.Max(0, d.count - shown.Count);
        Render();
    }

    void ShowLatest(List<NotifyMessage> latest)
    {
        shown.Clear();
        if (latest != null) for (int i = 0; i < latest.Count; i++) InsertByTime(latest[i]);
        while (shown.Count > MaxCards) shown.RemoveAt(shown.Count - 1);
        more = 0;
        Render();
    }

    void OnMessage(NotifyMessage m)
    {
        for (int i = 0; i < shown.Count; i++) if (shown[i].ts == m.ts) return;  // already here (digest + live both delivered it)
        shown.Insert(0, m);
        if (shown.Count > MaxCards)
        {
            shown.RemoveAt(shown.Count - 1);
            more++;
        }
        Render();
    }

    void OnSent(NotifySent s)
    {
        if (flash != null) StopCoroutine(flash);
        flash = StartCoroutine(FlashSent(s));
    }

    // Newest first; Slack ts is "<seconds>.<micros>" so a numeric compare orders it.
    void InsertByTime(NotifyMessage m)
    {
        double t = Ts(m);
        int at = shown.Count;
        for (int i = 0; i < shown.Count; i++)
        {
            if (Ts(shown[i]) < t) { at = i; break; }
        }
        shown.Insert(at, m);
    }

    static double Ts(NotifyMessage m) =>
        double.TryParse(m.ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;

    // ---- panel -> hub ---------------------------------------------------------------------------------------

    /// <summary>"Reply with Bit": one agent turn with the message as context; the hub's messages_send tool
    /// reads the reply back and sends it in-thread only after the user confirms.</summary>
    void ReplyWithBit(MessageCard card)
    {
        var m = card.Message;
        if (m == null) return;
        string where = MessageCard.IsDm(m) ? "by DM" : $"in #{m.channelName}";
        string prompt = $"Reply to {m.userName} {where} (Slack ts {m.ts}). They said: \"{m.text}\". Ask me what to answer, then send it in that thread.";

        var bridge = FindAnyObjectByType<Forge.Pet.GatorAgentBridge>();
        if (bridge != null) bridge.Ask(prompt, "qa");  // tracks the turn so the gator animates and cancels cleanly
        else ForgeNet.Send("agent.ask", new JObject { ["pet_id"] = "bit", ["project_id"] = "gatorhack", ["text"] = prompt, ["mode"] = "qa" });
    }

    // ---- view -----------------------------------------------------------------------------------------------

    void Render()
    {
        for (int i = 0; i < cards.Count; i++)
        {
            bool on = i < shown.Count;
            if (on) cards[i].Bind(shown[i]);
            if (cards[i].gameObject.activeSelf != on) cards[i].gameObject.SetActive(on);
        }
        if (empty != null) empty.gameObject.SetActive(shown.Count == 0);
        if (flash == null) footer.text = more > 0 ? $"+{more} more" : "";
    }

    IEnumerator FlashSent(NotifySent s)
    {
        footer.text = MessageCard.IsDmChannel(s.channelName) ? "Sent" : $"Sent to #{s.channelName}";
        yield return FlashWait;
        flash = null;
        Render();
    }

    IEnumerator Clock()
    {
        while (true)
        {
            yield return ClockTick;
            for (int i = 0; i < shown.Count; i++) cards[i].RefreshTime();
        }
    }
}
