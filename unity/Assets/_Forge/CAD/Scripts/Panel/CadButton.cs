using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A button on the design panel: a ForgeSurface whose colour follows its state (rest, pointed at, on, active), an
/// optional label and icon (a <see cref="CadShape"/> drawn in the label's colour), 35% when disabled, a press that dips
/// it to 96% (CAD UX spec section 7: 0.12 s), and a uGUI Button for the click, so the mouse (DesktopRig) and Interaction
/// SDK rays and pokes all work. Make one with <see cref="Create"/>; the looks below are the boards' CSS.
/// </summary>
public class CadButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    /// <summary>Colours per state (background and ink: the label and icon), the outline, and when it's semibold.</summary>
    public struct Look
    {
        public Color rest, hover, on, onHover, active, activeHover;
        public Color ink, onInk, activeInk;
        public float outline;  // a 1 mm outline in the background's colour at this alpha
        public bool bold;      // always semibold (the primary button)
        public bool boldOn;    // semibold when on (tabs, segments)
    }

    static Look Make(float rest, float hover, float ink, float on = 0.92f) => new Look
    {
        rest = CadUi.White(rest), hover = CadUi.White(hover), on = CadUi.White(on), onHover = CadUi.White(Mathf.Min(1f, on + 0.04f)),
        active = CadUi.HueA(0.24f), activeHover = CadUi.HueA(0.3f),
        ink = CadUi.White(ink), onInk = CadUi.Dark, activeInk = CadUi.ActiveText,
    };

    /// <summary>Tabs and segments: clear, 6% pointed at; on: white 92% with dark semibold text.</summary>
    public static readonly Look Segment = WithBoldOn(Make(0f, 0.06f, 0.72f));
    /// <summary>Presets, "On the model" toggles, the banner's mini pills.</summary>
    public static readonly Look Pill = Make(0.08f, 0.14f, 0.88f);
    /// <summary>The footer: 9%, 15% pointed at; active (Assemble, Back to desk) in the CAD hue.</summary>
    public static readonly Look Footer = Make(0.09f, 0.15f, 0.92f);
    /// <summary>The footer's demo button: white 92% with dark semibold text.</summary>
    public static readonly Look Primary = Primaryish();
    /// <summary>Steppers: 8%, 16% pointed at.</summary>
    public static readonly Look Stepper = Make(0.08f, 0.16f, 0.85f);
    /// <summary>Text buttons, the header's title: clear, 7% pointed at.</summary>
    public static readonly Look Ghost = Make(0f, 0.07f, 0.7f);
    /// <summary>History rows and Designs menu items: clear, 5% pointed at; on (current): 7%.</summary>
    public static readonly Look Row = Rowish();
    /// <summary>The verdict strip: a card that opens Overview.</summary>
    public static readonly Look Card = Cardish();

    static Look WithBoldOn(Look l)
    {
        l.boldOn = true;
        return l;
    }

    static Look Primaryish()
    {
        var l = Make(0.92f, 1f, 1f);
        l.ink = l.onInk = CadUi.Dark;
        l.bold = true;
        return l;
    }

    static Look Rowish()
    {
        var l = Make(0f, 0.05f, 0.92f, 0.07f);
        l.onHover = CadUi.White(0.09f);
        l.onInk = CadUi.White(0.92f);
        return l;
    }

    static Look Cardish()
    {
        var l = Make(0.045f, 0.07f, 0.92f);
        l.outline = 0.07f;
        return l;
    }

    public Button Button { get; private set; }
    public ForgeSurface Surface { get; private set; }
    public TextMeshProUGUI Label { get; private set; }
    public CadShape Icon { get; private set; }
    public RectTransform Rect => (RectTransform)transform;
    /// <summary>The label's colour now: icons draw in it.</summary>
    public Color Ink { get; private set; } = Color.white;

    static readonly List<CadButton> moving = new List<CadButton>();
    static int tickedFrame = -1;

    Look look;
    bool on, active, interactable = true, pressed, animating;
    int hovers;
    float scale = 1f, iconSize, iconGap;
    CanvasGroup group;
    System.Action<CadShape, Color> drawIcon;

    public bool On
    {
        get => on;
        set
        {
            if (on == value) return;
            on = value;
            Refresh();
        }
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            Refresh();
        }
    }

    public bool Interactable
    {
        get => interactable;
        set
        {
            if (interactable == value) return;
            interactable = value;
            if (Button != null) Button.interactable = value;
            if (!value)
            {
                pressed = false;
                Animate();
            }
            Refresh();
        }
    }

    public bool Hovered => hovers > 0 && interactable;

    public static CadButton Create(Transform parent, string name, Look look, float corner, UnityAction onClick, bool pill = false)
    {
        var rt = CadUi.Rect(parent, name);
        var b = rt.gameObject.AddComponent<CadButton>();
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

    public void SetLook(Look l)
    {
        look = l;
        Refresh();
    }

    /// <summary>A centred label (or <paramref name="align"/>ed, inset by <paramref name="pad"/> each side).</summary>
    public TextMeshProUGUI AddLabel(string text, float size, TextAlignmentOptions align = TextAlignmentOptions.Center, float pad = 0f)
    {
        Label = CadUi.Text(transform, "Label", size, look.ink, look.bold, align);
        CadUi.Stretch(Label.rectTransform, pad, 0f, pad, 0f);
        Label.text = text;
        Refresh();
        return Label;
    }

    /// <summary>
    /// An icon <paramref name="size"/> mm square drawn by <paramref name="draw"/> (in <see cref="Ink"/>), set before the
    /// label with <paramref name="gap"/> between them, the pair centred (the footer's buttons).
    /// </summary>
    public CadShape AddIcon(System.Action<CadShape, Color> draw, float size, float gap)
    {
        drawIcon = draw;
        iconSize = size;
        iconGap = gap;
        if (Icon == null) Icon = CadUi.Shape(transform, "Icon", s => drawIcon?.Invoke(s, Ink));
        else Icon.Redraw();
        Center();
        return Icon;
    }

    /// <summary>New label text (and the icon re-centred beside it).</summary>
    public void SetText(string text)
    {
        if (Label == null || Label.text == text) return;
        Label.text = text;
        Center();
    }

    /// <summary>Another icon in the same place (Explode / Assemble, Fly / Stop).</summary>
    public void SetIcon(System.Action<CadShape, Color> draw)
    {
        if (drawIcon == draw) return;
        drawIcon = draw;
        Icon?.Redraw();
    }

    void Center()
    {
        if (Icon == null) return;
        var irt = Icon.rectTransform;
        irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
        irt.pivot = new Vector2(0.5f, 0.5f);
        irt.sizeDelta = new Vector2(iconSize, iconSize);
        if (Label == null || string.IsNullOrEmpty(Label.text))
        {
            irt.anchoredPosition = Vector2.zero;
            return;
        }
        float w = CadUi.Width(Label, Label.text);
        float total = iconSize + iconGap + w;
        irt.anchoredPosition = new Vector2(-total * 0.5f + iconSize * 0.5f, 0f);
        var lrt = Label.rectTransform;
        lrt.anchorMin = new Vector2(0.5f, 0f);
        lrt.anchorMax = new Vector2(0.5f, 1f);
        lrt.pivot = new Vector2(0.5f, 0.5f);
        lrt.sizeDelta = new Vector2(w + 2f, 0f);
        lrt.anchoredPosition = new Vector2(-total * 0.5f + iconSize + iconGap + w * 0.5f, 0f);
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

    public void OnPointerDown(PointerEventData e)
    {
        if (!interactable) return;
        pressed = true;
        Animate();
    }

    public void OnPointerUp(PointerEventData e)
    {
        pressed = false;
        Animate();
    }

    void OnDisable()
    {
        hovers = 0;
        pressed = false;
        scale = 1f;
        transform.localScale = Vector3.one;
        Refresh();
    }

    void Refresh()
    {
        if (Surface == null) return;
        bool lit = hovers > 0 && interactable;
        Color bg = active ? (lit ? look.activeHover : look.active)
            : on ? (lit ? look.onHover : look.on)
            : lit ? look.hover : look.rest;
        Surface.color = bg;
        if (!Mathf.Approximately(Surface.Outline, look.outline)) Surface.Outline = look.outline;
        Color ink = active ? look.activeInk : on ? look.onInk : look.ink;
        if (Label != null)
        {
            Label.color = ink;
            var style = look.bold || (on && look.boldOn) ? FontStyles.Bold : FontStyles.Normal;
            if (Label.fontStyle != style) Label.fontStyle = style;
        }
        if (ink != Ink)
        {
            Ink = ink;
            if (Icon != null) Icon.Redraw();
        }
        float alpha = interactable ? 1f : 0.35f;
        if (group == null && alpha < 1f) group = CadUi.Group(gameObject);
        if (group != null && !Mathf.Approximately(group.alpha, alpha)) group.alpha = alpha;
    }

    // Press: dip to 96% and back, eased (spec section 7). Ticked by the panel only while moving.

    void Animate()
    {
        if (animating || !isActiveAndEnabled) return;
        animating = true;
        moving.Add(this);
    }

    /// <summary>CadParamsPanel.Update: move the buttons that are dipping or coming back up.</summary>
    public static void TickAll(float dt)
    {
        if (tickedFrame == Time.frameCount) return;
        tickedFrame = Time.frameCount;
        for (int i = moving.Count - 1; i >= 0; i--)
        {
            var b = moving[i];
            if (b == null || !b.Step(dt)) moving.RemoveAt(i);
        }
    }

    bool Step(float dt)
    {
        float target = pressed ? 0.96f : 1f;
        scale = Mathf.Lerp(scale, target, 1f - Mathf.Exp(-dt * 38f));
        bool settled = Mathf.Abs(scale - target) < 0.0008f;
        if (settled) scale = target;
        transform.localScale = new Vector3(scale, scale, 1f);
        if (settled && !pressed) animating = false;
        return !settled || pressed;
    }
}
