using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The focus timers above the screens: one slim graphite pill per timer, stacked upward from the main panel's top edge
/// with the one that ends soonest at the bottom (a ringing one first). Each shows its name, the time left in big fixed-
/// width digits and a rim of light that drains as it runs (<see cref="ForgeRim"/>), with pause / resume and stop.
/// Ringing, the pill widens, pulses and offers +5 min and Stop (the chime is <see cref="DeskChime"/>). A small − to the
/// right minimizes the stack into a dot (the soonest timer). Counts down on its own from each timer's remaining_s
/// when the hub's list arrives. Built at runtime on a headerless ForgePanel (Desk/Prefabs/TimerStack.prefab, root
/// pivot at the bottom so it grows upward).
/// </summary>
public class TimerStack : MonoBehaviour
{
    const float PillWidth = 312f, RingWidth = 372f, PillHeight = 64f, Gap = 12f, Side = 44f;
    static readonly Color Graphite = new Color32(30, 33, 38, 242);

    class Pill
    {
        public RectTransform root;
        public ForgeSurface halo, body;
        public ForgeRim rim;
        public TMP_Text label, digits, up, upSub;
        public GameObject counting, ringing;
        public DeskIcon pauseIcon;
        public DeskTimer timer;
        public int shownSecond = -1;
        public float shownProgress = -1f;
        public string shownStatus;
    }

    readonly List<Pill> pills = new List<Pill>();
    readonly List<DeskTimer> timers = new List<DeskTimer>();
    ForgePanel panel;
    RectTransform content, minimize;
    float receivedAt;
    int count;

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        content = panel.Content;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        minimize = (RectTransform)DeskUi.RoundButton("Minimize", content, 30f, DeskIcon.Shape.Minus,
            () => DeskApi.SetWidget("timer", "minimized"), out _).transform;
        minimize.anchorMin = minimize.anchorMax = new Vector2(0.5f, 0f);
    }

    /// <summary>The hub's timers (soonest first), as of <paramref name="at"/> (realtimeSinceStartup).</summary>
    public void Show(List<DeskTimer> list, float at)
    {
        timers.Clear();
        if (list != null) timers.AddRange(list);
        receivedAt = at;
        count = timers.Count;
        while (pills.Count < count) pills.Add(Build(pills.Count));
        for (int i = 0; i < pills.Count; i++)
        {
            bool on = i < count;
            pills[i].root.gameObject.SetActive(on);
            if (on) Bind(pills[i], timers[i]);
        }
        Layout();
    }

    void Layout()
    {
        int n = Mathf.Max(1, count);
        float height = n * PillHeight + (n - 1) * Gap;
        panel.SizeMeters = new Vector2(RingWidth + 2f * Side, height) * ForgePanel.MetersPerUnit;
        for (int i = 0; i < count; i++)
        {
            var p = pills[i];
            float width = p.timer.status == "ringing" ? RingWidth : PillWidth;
            p.root.sizeDelta = new Vector2(width, PillHeight);
            p.root.anchoredPosition = new Vector2(0f, i * (PillHeight + Gap) + PillHeight * 0.5f);
        }
        // The − beside the bottom pill.
        float bottomWidth = count > 0 && pills[0].timer.status == "ringing" ? RingWidth : PillWidth;
        minimize.anchoredPosition = new Vector2(bottomWidth * 0.5f + 12f + 15f, PillHeight * 0.5f);
        minimize.gameObject.SetActive(count > 0);
    }

    void Bind(Pill p, DeskTimer t)
    {
        p.timer = t;
        p.shownSecond = -1;
        p.shownProgress = -1f;
        bool ringing = t.status == "ringing";
        p.counting.SetActive(!ringing);
        p.ringing.SetActive(ringing);
        p.halo.gameObject.SetActive(ringing);
        p.label.text = (t.status == "paused" ? t.label + " · paused" : t.label).ToUpperInvariant();
        p.pauseIcon.Kind = t.status == "paused" ? DeskIcon.Shape.Play : DeskIcon.Shape.Pause;
        p.up.text = "Time's up";
        p.upSub.text = string.Equals(t.label, "focus", System.StringComparison.OrdinalIgnoreCase)
            ? DeskUi.Human(t.durationS) + " of focus"
            : $"{t.label} · {DeskUi.Human(t.durationS)}";
        if (p.shownStatus != t.status)
        {
            p.shownStatus = t.status;
            float dim = t.status == "paused" ? 0.35f : 1f;
            p.rim.canvasRenderer.SetAlpha(dim);
            p.digits.alpha = t.status == "paused" ? 0.6f : 1f;
            p.root.localScale = Vector3.one;
        }
    }

    void Update()
    {
        float now = Time.realtimeSinceStartup;
        for (int i = 0; i < count; i++) Tick(pills[i], now);
    }

    void Tick(Pill p, float now)
    {
        DeskTimer t = p.timer;
        bool ringing = t.status == "ringing";
        float left = t.status == "running" ? Mathf.Max(0f, t.remainingS - (now - receivedAt)) : t.remainingS;
        int second = Mathf.CeilToInt(left - 0.001f);
        if (!ringing && second != p.shownSecond)
        {
            p.shownSecond = second;
            p.digits.text = DeskUi.Clock(left, roundUp: true);
        }
        float progress = ringing ? 1f : t.durationS > 0f ? Mathf.Clamp01(left / t.durationS) : 0f;
        // Redraw the rim only when it has visibly moved (a quarter mm round the pill).
        if (Mathf.Abs(progress - p.shownProgress) * (2f * PillWidth + 2f * PillHeight) > 0.25f)
        {
            p.shownProgress = progress;
            p.rim.Progress = progress;
        }
        if (ringing)
        {
            // White-hot and breathing, a beat every 1.2 s (alpha and scale only: no mesh rebuild).
            float k = 0.5f + 0.5f * Mathf.Sin(now * Mathf.PI * 2f / 1.2f);
            p.root.localScale = Vector3.one * (1f + 0.035f * k);
            p.halo.canvasRenderer.SetAlpha(0.55f + 0.45f * k);
            p.rim.canvasRenderer.SetAlpha(0.7f + 0.3f * k);
        }
    }

    Pill Build(int index)
    {
        var p = new Pill();
        p.root = DeskUi.Rect($"Timer {index + 1}", content, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(PillWidth, PillHeight));

        p.halo = DeskUi.Surface("Halo", p.root, new Color(1f, 1f, 1f, 0.22f), pill: true, glow: 1f);
        DeskUi.Stretch(p.halo.rectTransform, -18f, -18f, -18f, -18f);
        p.halo.gameObject.SetActive(false);

        var shadow = DeskUi.Surface("Shadow", p.root, new Color(0f, 0f, 0f, 0.28f), pill: true, glow: 1f);
        DeskUi.Stretch(shadow.rectTransform, 2f, -4f, 2f, 4f);
        p.body = DeskUi.Surface("Body", p.root, Graphite, pill: true);
        DeskUi.Stretch(p.body.rectTransform, 0f, 0f, 0f, 0f);
        p.body.raycastTarget = true;  // the pill takes the pointer, so it doesn't fall through to the screen behind

        // The rim of light: a crisp line with a soft glow outside it and a little inside.
        p.rim = Rim("Rim", p.root, 1.9f, 7f, 0.5f, 0.14f);
        p.rim.InnerGlow = 0.3f;

        // Counting: name and digits on the left, pause / resume and stop on the right.
        p.counting = DeskUi.Rect("Counting", p.root).gameObject;
        DeskUi.Stretch((RectTransform)p.counting.transform, 0f, 0f, 0f, 0f);
        p.label = DeskUi.Text("Label", p.counting.transform, 10f, FontStyles.Bold, DeskUi.Muted, TextAlignmentOptions.BottomLeft);
        p.label.characterSpacing = 12f;
        Band(p.label.rectTransform, 24f, 120f, 8f, 22f);
        p.digits = DeskUi.Text("Digits", p.counting.transform, 28f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
        Band(p.digits.rectTransform, 22f, 120f, -25f, 9f);
        var stopTimer = DeskUi.RoundButton("Stop", p.counting.transform, 40f, DeskIcon.Shape.Stop, () => DeskApi.TimerStop(p.timer.timerId), out var stopIcon);
        stopIcon.rectTransform.sizeDelta = Vector2.one * 15f;
        Right((RectTransform)stopTimer.transform, 12f);
        var pause = DeskUi.RoundButton("Pause", p.counting.transform, 40f, DeskIcon.Shape.Pause, () =>
        {
            if (p.timer.status == "paused") DeskApi.TimerResume(p.timer.timerId);
            else DeskApi.TimerPause(p.timer.timerId);
        }, out p.pauseIcon);
        p.pauseIcon.rectTransform.sizeDelta = Vector2.one * 17f;
        Right((RectTransform)pause.transform, 12f + 40f + 10f);

        // Ringing: Time's up and what it was, +5 min and Stop.
        p.ringing = DeskUi.Rect("Ringing", p.root).gameObject;
        DeskUi.Stretch((RectTransform)p.ringing.transform, 0f, 0f, 0f, 0f);
        p.up = DeskUi.Text("TimesUp", p.ringing.transform, 19f, FontStyles.Bold, Color.white, TextAlignmentOptions.BottomLeft);
        Band(p.up.rectTransform, 26f, 170f, 1f, 26f);
        p.upSub = DeskUi.Text("What", p.ringing.transform, 11f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.TopLeft);
        Band(p.upSub.rectTransform, 26f, 170f, -19f, -1f);
        var stop = DeskUi.PillButton("Stop", p.ringing.transform, "Stop", 40f, 14f, primary: true, () => DeskApi.TimerStop(p.timer.timerId), out _);
        Right((RectTransform)stop.transform, 12f);
        var snooze = DeskUi.PillButton("Snooze", p.ringing.transform, "+5 min", 40f, 13f, primary: false, () => DeskApi.TimerAdd(p.timer.timerId, 5f), out _);
        Right((RectTransform)snooze.transform, 12f + ((RectTransform)stop.transform).sizeDelta.x + 8f);
        p.ringing.SetActive(false);
        return p;
    }

    static ForgeRim Rim(string name, RectTransform parent, float stroke, float glow, float glowAlpha, float track)
    {
        var rim = DeskUi.Rect(name, parent).gameObject.AddComponent<ForgeRim>();
        DeskUi.Stretch(rim.rectTransform, 0f, 0f, 0f, 0f);
        rim.raycastTarget = false;
        rim.color = Color.white;
        rim.Stroke = stroke;
        rim.Glow = glow;
        rim.GlowAlpha = glowAlpha;
        rim.TrackAlpha = track;
        return rim;
    }

    // A text box from `left` past the pill's left edge to `right` short of its right one, from y0 to y1 (mm from the
    // pill's middle, up positive).
    static void Band(RectTransform rt, float left, float right, float y0, float y1)
    {
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(0f, 0f);
        rt.offsetMin = new Vector2(left, y0);
        rt.offsetMax = new Vector2(-right, y1);
    }

    // Anchored to the pill's right end, `fromRight` in, centred vertically.
    static void Right(RectTransform rt, float fromRight)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-fromRight, 0f);
    }
}
