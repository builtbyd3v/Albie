using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One Slack message in <see cref="MessagesPanel"/>: sender initial bubble, name + channel, first line of text,
/// relative time, and an accent strip when the message mentions the user. Slightly translucent at rest.
/// Hover (mouse crosshair via DesktopRig, Interaction SDK ray via PointableCanvasModule, both arrive as uGUI pointer
/// events) lifts the card 1 cm toward the viewer, makes it opaque and expands it to the full text. Click = reply with Bit.
/// Layout: the parent VerticalLayoutGroup reads <see cref="LayoutElement.preferredHeight"/>; 1 canvas unit = 1 mm.
/// </summary>
public class MessageCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    [SerializeField] Image frame;          // card background; alpha RestAlpha at rest, 1 on hover
    [SerializeField] Image accent;         // thin strip on the left, on for mentions
    [SerializeField] Image bubble;         // sender initial disc
    [SerializeField] TMP_Text initial;
    [SerializeField] TMP_Text header;      // "Hudson  #forge-team"
    [SerializeField] TMP_Text time;        // "2m", "1h"
    [SerializeField] TMP_Text body;        // first line at rest, full text on hover
    [SerializeField] LayoutElement layout;

    public const float CollapsedHeightMm = 86f;
    public const float RestAlpha = 0.8f;
    public const float LiftMm = 10f;       // 1 cm toward the viewer (canvas -Z)
    const float HoverSeconds = 0.18f;
    const float BodyTopMm = 42f, BodyBottomMm = 10f;

    static readonly Color MentionAccent = new Color(1f, 0.6f, 0.2f);
    static readonly Color[] BubbleColors =
    {
        new Color(0.25f, 0.55f, 1f), new Color(0.85f, 0.4f, 0.9f), new Color(0.3f, 0.8f, 0.45f),
        new Color(1f, 0.55f, 0.2f), new Color(0.95f, 0.35f, 0.4f), new Color(0.25f, 0.8f, 0.85f),
    };
    static readonly char[] LineBreaks = { '\n', '\r' };

    public NotifyMessage Message { get; private set; }
    /// <summary>The user clicked or poked the card.</summary>
    public event Action<MessageCard> Clicked;

    RectTransform rect;
    Coroutine anim;
    string firstLine = "";

    void Awake() => rect = (RectTransform)transform;

    public void Bind(NotifyMessage m)
    {
        Message = m;
        string name = string.IsNullOrEmpty(m.userName) ? m.user : m.userName;
        if (string.IsNullOrEmpty(name)) name = "?";
        initial.text = name.Substring(0, 1).ToUpperInvariant();
        bubble.color = BubbleColors[Mathf.Abs(StableHash(name)) % BubbleColors.Length];
        header.text = IsDm(m) ? $"{name}  <alpha=#99><size=85%>DM" : $"{name}  <alpha=#99><size=85%>#{m.channelName}";
        firstLine = FirstLine(m.text);
        accent.enabled = m.mentionsMe;
        RefreshTime();
        SetHover(false, immediate: true);
    }

    public void RefreshTime()
    {
        if (Message != null) time.text = RelativeTime(Message.ts);
    }

    /// <summary>The hub sets is_dm; the channel name is only consulted when the hub sent none.</summary>
    public static bool IsDm(NotifyMessage m) => m.isDm || (string.IsNullOrEmpty(m.channelName) ? true : m.channelName == "dm");

    /// <summary>For payloads without is_dm (notify.sent): the hub names a DM channel "dm".</summary>
    public static bool IsDmChannel(string channelName) =>
        string.IsNullOrEmpty(channelName) || channelName == "dm" || channelName == "group-dm";

    /// <summary>Slack ts ("1729800000.123456") to "now" / "5m" / "2h" / "3d".</summary>
    public static string RelativeTime(string ts)
    {
        if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return "";
        double age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
        if (age < 60) return "now";
        if (age < 3600) return $"{(int)(age / 60)}m";
        if (age < 86400) return $"{(int)(age / 3600)}h";
        return $"{(int)(age / 86400)}d";
    }

    static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int cut = text.IndexOfAny(LineBreaks);
        return cut < 0 ? text : text.Substring(0, cut);
    }

    // string.GetHashCode is randomized per process; keep bubble colors stable across runs.
    static int StableHash(string s)
    {
        int h = 17;
        for (int i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
        return h;
    }

    public void OnPointerEnter(PointerEventData eventData) => SetHover(true);
    public void OnPointerExit(PointerEventData eventData) => SetHover(false);
    public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke(this);

    void SetHover(bool on, bool immediate = false)
    {
        if (Message == null) return;
        body.text = on ? Message.text : firstLine;
        body.textWrappingMode = on ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        body.overflowMode = on ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;

        float h1 = on ? ExpandedHeight() : CollapsedHeightMm;
        float a1 = on ? 1f : RestAlpha;
        float z1 = on ? -LiftMm : 0f;
        if (anim != null) { StopCoroutine(anim); anim = null; }
        if (immediate || !isActiveAndEnabled)
        {
            Apply(h1, a1, z1);
            return;
        }
        float h0 = layout.preferredHeight, a0 = frame.color.a, z0 = rect.localPosition.z;
        anim = StartCoroutine(ForgeTween.Run(HoverSeconds, 0f, k =>
        {
            float e = ForgeTween.EaseOutCubic(k);
            Apply(Mathf.Lerp(h0, h1, e), Mathf.Lerp(a0, a1, e), Mathf.Lerp(z0, z1, e));
        }, () => anim = null));
    }

    void Apply(float heightMm, float alpha, float zMm)
    {
        layout.preferredHeight = heightMm;
        Color c = frame.color;
        c.a = alpha;
        frame.color = c;
        Vector3 p = rect.localPosition;  // the layout group owns x/y; z is ours
        p.z = zMm;
        rect.localPosition = p;
    }

    float ExpandedHeight()
    {
        float width = body.rectTransform.rect.width;
        if (width <= 1f) width = 300f;  // not laid out yet
        float text = body.GetPreferredValues(Message.text, width, 0f).y;
        return Mathf.Max(CollapsedHeightMm, BodyTopMm + text + BodyBottomMm);
    }
}
