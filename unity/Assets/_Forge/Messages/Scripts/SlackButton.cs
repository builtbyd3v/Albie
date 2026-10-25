using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A button in the Messages panels: a ForgeSurface whose colour follows its state (rest, pointed at, on), an optional
/// label, 35% when disabled, and a uGUI Button for the click, so the mouse (DesktopRig) and Interaction SDK rays and
/// pokes all work. Hover arrives as uGUI pointer enter / exit from both input paths. Make one with <see cref="Create"/>.
/// </summary>
public class SlackButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public struct Look
    {
        public Color rest, hover, on, onHover;
        public Color ink, onInk;
        public bool bold, boldOn;
    }

    /// <summary>Horizon-style pill: dim white, near-white with dark text when on (Ask Bit, Cancel, Load earlier).</summary>
    public static readonly Look Pill = new Look
    {
        rest = ForgeTheme.PillOff, hover = ForgeTheme.PillOffHover, on = ForgeTheme.PillOn, onHover = Color.white,
        ink = ForgeTheme.PillOffText, onInk = ForgeTheme.PillOnText, boldOn = true,
    };
    /// <summary>The primary pill (Send): the accent with dark text.</summary>
    public static readonly Look Primary = new Look
    {
        rest = SlackUi.AccentA(0.88f), hover = SlackUi.AccentA(1f), on = SlackUi.AccentA(1f), onHover = SlackUi.AccentA(1f),
        ink = SlackUi.Ink, onInk = SlackUi.Ink, bold = true,
    };
    /// <summary>Conversation and mention rows: the module's card fill, lighter when pointed at, solid when selected.</summary>
    public static readonly Look Row = new Look
    {
        rest = SlackUi.CardFill, hover = SlackUi.CardFillHover, on = SlackUi.CardFillOn, onHover = SlackUi.CardFillOn,
        ink = SlackUi.TextColor, onInk = Color.white, boldOn = true,
    };

    // ---- The Slack panel's buttons (SlackUi.Sk* colours) ----

    /// <summary>A sidebar conversation: no fill until pointed at, aubergine-lit when open.</summary>
    public static readonly Look SideRow = new Look
    {
        rest = SlackUi.Clear, hover = SlackUi.SkSideHover, on = SlackUi.SkSideOn, onHover = SlackUi.SkSideOnHover,
        ink = SlackUi.SkSideText, onInk = Color.white,
    };
    /// <summary>A quiet text button in the conversation (Ask Albie, Cancel, Load earlier).</summary>
    public static readonly Look Ghost = new Look
    {
        rest = SlackUi.Clear, hover = SlackUi.White(0.09f), on = SlackUi.White(0.14f), onHover = SlackUi.White(0.18f),
        ink = SlackUi.SkMuted, onInk = Color.white,
    };
    /// <summary>The floating hover toolbar on a message.</summary>
    public static readonly Look Toolbar = new Look
    {
        rest = new Color(0.17f, 0.18f, 0.20f), hover = new Color(0.23f, 0.24f, 0.26f), on = new Color(0.23f, 0.24f, 0.26f),
        onHover = new Color(0.23f, 0.24f, 0.26f), ink = SlackUi.SkText, onInk = Color.white,
    };
    /// <summary>Send with something to send: Slack's green.</summary>
    public static readonly Look SendOn = new Look
    {
        rest = SlackUi.SkGreen, hover = SlackUi.SkGreenHover, on = SlackUi.SkGreenHover, onHover = SlackUi.SkGreenHover,
        ink = Color.white, onInk = Color.white,
    };
    /// <summary>Send with an empty field: no fill, a grey arrow.</summary>
    public static readonly Look SendOff = new Look
    {
        rest = SlackUi.Clear, hover = SlackUi.Clear, on = SlackUi.Clear, onHover = SlackUi.Clear,
        ink = SlackUi.SkMuted, onInk = SlackUi.SkMuted,
    };

    public Button Button { get; private set; }
    public ForgeSurface Surface { get; private set; }
    public TextMeshProUGUI Label { get; private set; }
    public RectTransform Rect => (RectTransform)transform;

    Look look;
    bool on, interactable = true, emphasis;
    int hovers;
    CanvasGroup group;

    public bool On
    {
        get => on;
        set { if (on != value) { on = value; Refresh(); } }
    }

    /// <summary>Bold label while set (an unread conversation).</summary>
    public bool Emphasis
    {
        get => emphasis;
        set { if (emphasis != value) { emphasis = value; Refresh(); } }
    }

    public bool Interactable
    {
        get => interactable;
        set
        {
            if (interactable == value) return;
            interactable = value;
            if (Button != null) Button.interactable = value;
            Refresh();
        }
    }

    public static SlackButton Create(Transform parent, string name, Look look, float corner, UnityAction onClick, bool pill = false)
    {
        var rt = SlackUi.Rect(parent, name);
        var b = rt.gameObject.AddComponent<SlackButton>();
        b.Surface = rt.gameObject.AddComponent<ForgeSurface>();
        b.Surface.Style = ForgeSurface.Kind.Fill;
        b.Surface.Corner = corner;
        b.Surface.Pill = pill;
        b.Surface.raycastTarget = true;
        b.Button = rt.gameObject.AddComponent<Button>();
        b.Button.targetGraphic = b.Surface;
        b.Button.transition = Selectable.Transition.None;
        b.Button.navigation = new Navigation { mode = Navigation.Mode.None };
        if (onClick != null) b.Button.onClick.AddListener(onClick);
        b.look = look;
        b.Refresh();
        return b;
    }

    /// <summary>A pill button sized to its text (like ForgePill.Create), <paramref name="height"/> mm tall.</summary>
    public static SlackButton CreatePill(Transform parent, string text, Look look, UnityAction onClick, float height = ForgeTheme.PillHeight, float textSize = ForgeTheme.PillText)
    {
        var b = Create(parent, text, look, 0f, onClick, pill: true);
        b.AddLabel(text, textSize);
        b.Label.characterSpacing = 1f;
        b.Rect.sizeDelta = new Vector2(SlackUi.Width(b.Label, text) + 2f * textSize, height);
        return b;
    }

    public TextMeshProUGUI AddLabel(string text, float size, TextAlignmentOptions align = TextAlignmentOptions.Center, float pad = 0f)
    {
        Label = SlackUi.Text(transform, "Label", size, look.ink, look.bold, align);
        SlackUi.Stretch(Label.rectTransform, pad, 0f, pad, 0f);
        Label.text = text;
        Refresh();
        return Label;
    }

    /// <summary>Switch looks (Send on / off as the field fills and empties).</summary>
    public void SetLook(Look l)
    {
        look = l;
        Refresh();
    }

    /// <summary>New label text; a pill re-sizes to it.</summary>
    public void SetText(string text)
    {
        if (Label == null || Label.text == text) return;
        Label.text = text;
        if (Surface != null && Surface.Pill) Rect.sizeDelta = new Vector2(SlackUi.Width(Label, text) + 2f * Label.fontSize, Rect.sizeDelta.y);
    }

    public void OnPointerEnter(PointerEventData e)
    {
        hovers++;
        Refresh();
    }

    public void OnPointerExit(PointerEventData e)
    {
        hovers = Mathf.Max(0, hovers - 1);
        Refresh();
    }

    void OnDisable()
    {
        hovers = 0;
        Refresh();
    }

    void Refresh()
    {
        if (Surface == null) return;
        bool lit = hovers > 0 && interactable;
        Surface.color = on ? (lit ? look.onHover : look.on) : lit ? look.hover : look.rest;
        if (Label != null)
        {
            Label.color = on ? look.onInk : look.ink;
            var style = look.bold || (on && look.boldOn) || emphasis ? FontStyles.Bold : FontStyles.Normal;
            if (Label.fontStyle != style) Label.fontStyle = style;
        }
        float alpha = interactable ? 1f : 0.35f;
        if (group == null && alpha < 1f) group = SlackUi.Group(gameObject);
        if (group != null && !Mathf.Approximately(group.alpha, alpha)) group.alpha = alpha;
    }
}
