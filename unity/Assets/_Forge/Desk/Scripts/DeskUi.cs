using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Small vector icons for the desk panels' buttons, drawn as a mesh by the default UI shader (no sprites): play, pause,
/// stop, close, check, minus, plus, speaker. The icon fills the shorter side of its rect.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class DeskIcon : MaskableGraphic
{
    public enum Shape { Play, Pause, Stop, Close, Check, Minus, Plus, Speaker }

    [SerializeField] Shape shape;
    [Tooltip("Line width of the stroked icons (mm).")]
    [SerializeField] float weight = 2.4f;

    public Shape Kind
    {
        get => shape;
        set
        {
            if (shape == value) return;
            shape = value;
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        float s = Mathf.Min(r.width, r.height);
        Vector2 c = r.center;
        Color32 col = color;
        Vector2 P(float x, float y) => c + new Vector2(x, y) * s;
        switch (shape)
        {
            case Shape.Play:  // nudged right so it looks centred
                Triangle(vh, P(-0.28f, 0.38f), P(0.42f, 0f), P(-0.28f, -0.38f), col);
                break;
            case Shape.Pause:
                Quad(vh, P(-0.3f, -0.38f), P(-0.3f, 0.38f), P(-0.08f, 0.38f), P(-0.08f, -0.38f), col);
                Quad(vh, P(0.08f, -0.38f), P(0.08f, 0.38f), P(0.3f, 0.38f), P(0.3f, -0.38f), col);
                break;
            case Shape.Stop:
                Quad(vh, P(-0.32f, -0.32f), P(-0.32f, 0.32f), P(0.32f, 0.32f), P(0.32f, -0.32f), col);
                break;
            case Shape.Close:
                Line(vh, P(-0.32f, 0.32f), P(0.32f, -0.32f), weight, col);
                Line(vh, P(-0.32f, -0.32f), P(0.32f, 0.32f), weight, col);
                break;
            case Shape.Check:
                Line(vh, P(-0.36f, 0.02f), P(-0.1f, -0.26f), weight, col);
                Line(vh, P(-0.14f, -0.26f), P(0.38f, 0.3f), weight, col);
                break;
            case Shape.Minus:
                Line(vh, P(-0.34f, 0f), P(0.34f, 0f), weight, col);
                break;
            case Shape.Plus:
                Line(vh, P(-0.34f, 0f), P(0.34f, 0f), weight, col);
                Line(vh, P(0f, -0.34f), P(0f, 0.34f), weight, col);
                break;
            case Shape.Speaker:
                Quad(vh, P(-0.42f, -0.13f), P(-0.42f, 0.13f), P(-0.2f, 0.13f), P(-0.2f, -0.13f), col);
                Quad(vh, P(-0.2f, -0.13f), P(-0.2f, 0.13f), P(0.04f, 0.38f), P(0.04f, -0.38f), col);
                Line(vh, P(0.18f, -0.14f), P(0.18f, 0.14f), weight, col);
                Line(vh, P(0.33f, -0.27f), P(0.33f, 0.27f), weight, col);
                break;
        }
    }

    static void Triangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color32 col)
    {
        int i = vh.currentVertCount;
        vh.AddVert(a, col, Vector4.zero);
        vh.AddVert(b, col, Vector4.zero);
        vh.AddVert(c, col, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2);
    }

    // Corners in order round the quad.
    static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 col)
    {
        int i = vh.currentVertCount;
        vh.AddVert(a, col, Vector4.zero);
        vh.AddVert(b, col, Vector4.zero);
        vh.AddVert(c, col, Vector4.zero);
        vh.AddVert(d, col, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i + 2, i + 3, i);
    }

    // A thick line with square ends that reach half the width past each point.
    static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color32 col)
    {
        Vector2 d = (b - a).normalized * (width * 0.5f);
        var n = new Vector2(-d.y, d.x);
        a -= d;
        b += d;
        Quad(vh, a - n, a + n, b + n, b - n, col);
    }
}

/// <summary>
/// Swaps a graphic's color while any pointer is on this object (the mouse, rays and pokes all send enter / exit).
/// For round buttons and list rows.
/// </summary>
public class DeskHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Graphic target;
    public Color rest = new Color(1f, 1f, 1f, 0.1f);
    public Color lit = new Color(1f, 1f, 1f, 0.22f);
    int hovers;

    public bool Hovered => hovers > 0;

    /// <summary>New resting color (the row of a done note, a button that's on).</summary>
    public void SetRest(Color color)
    {
        rest = color;
        Refresh();
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
        if (target != null) target.color = Hovered ? lit : rest;
    }
}

/// <summary>
/// A track you point along to pick a value from 0 to 1: the movie's scrubber and its volume. Press or drag on it with
/// the mouse, a ray or a finger; <see cref="Changed"/> while dragging, <see cref="Committed"/> on release.
/// </summary>
public class DeskTrack : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public RectTransform fill, knob;
    public event Action<float> Changed;
    public event Action<float> Committed;

    float value;

    public bool Dragging { get; private set; }

    public float Value
    {
        get => value;
        set
        {
            this.value = Mathf.Clamp01(value);
            Layout();
        }
    }

    public void OnPointerDown(PointerEventData e)
    {
        Dragging = true;
        Pick(e);
    }

    public void OnDrag(PointerEventData e) => Pick(e);

    public void OnPointerUp(PointerEventData e)
    {
        if (!Dragging) return;
        Dragging = false;
        Committed?.Invoke(value);
    }

    void OnDisable() => Dragging = false;

    // Where the pointer is along the track: its hit point, or for a ray that missed, where it crosses the track's plane.
    void Pick(PointerEventData e)
    {
        var rect = (RectTransform)transform;
        Vector3 world = e.pointerCurrentRaycast.worldPosition;
        if (world == Vector3.zero && Camera.main != null && ForgePointer.TryFind(e, Camera.main.transform, out var pointer))
        {
            var plane = new Plane(rect.forward, rect.position);
            Ray r = pointer.Ray;
            if (plane.Raycast(r, out float enter)) world = r.GetPoint(enter);
        }
        if (world == Vector3.zero) return;
        Vector3 local = rect.InverseTransformPoint(world);
        Value = Mathf.InverseLerp(rect.rect.xMin, rect.rect.xMax, local.x);
        Changed?.Invoke(value);
    }

    void Layout()
    {
        if (fill != null) fill.anchorMax = new Vector2(value, 1f);
        if (knob != null) knob.anchorMin = knob.anchorMax = new Vector2(value, 0.5f);
    }
}

/// <summary>Building blocks for the desk panels' UI, made in code (sizes in mm, the Forge look).</summary>
public static class DeskUi
{
    public static readonly Color ButtonRest = new Color(1f, 1f, 1f, 0.1f);
    public static readonly Color ButtonLit = new Color(1f, 1f, 1f, 0.22f);
    public static readonly Color Muted = new Color(1f, 1f, 1f, 0.62f);

    public static RectTransform Rect(string name, Transform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var rt = Rect(name, parent);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        return rt;
    }

    public static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    public static TMP_Text Text(string name, Transform parent, float size, FontStyles style, Color color, TextAlignmentOptions align)
    {
        var text = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = align;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    public static ForgeSurface Surface(string name, Transform parent, Color color, bool pill, float corner = 8f, float glow = 0f)
    {
        var surface = Rect(name, parent).gameObject.AddComponent<ForgeSurface>();
        surface.Pill = pill;
        surface.Corner = corner;
        surface.Glow = glow;
        surface.color = color;
        surface.raycastTarget = false;
        return surface;
    }

    /// <summary>A round button with an icon: dim white, brighter while pointed at.</summary>
    public static Button RoundButton(string name, Transform parent, float diameter, DeskIcon.Shape shape, UnityAction onClick, out DeskIcon icon)
    {
        var surface = Surface(name, parent, ButtonRest, pill: true);
        surface.raycastTarget = true;
        surface.rectTransform.sizeDelta = Vector2.one * diameter;
        var button = Clickable(surface, onClick);
        icon = Rect("Icon", surface.transform).gameObject.AddComponent<DeskIcon>();
        icon.Kind = shape;
        icon.color = Color.white;
        icon.raycastTarget = false;
        icon.rectTransform.sizeDelta = Vector2.one * diameter * 0.42f;
        return button;
    }

    /// <summary>A pill button with a label: dim white (or near-white with dark text when <paramref name="primary"/>).</summary>
    public static Button PillButton(string name, Transform parent, string label, float height, float textSize, bool primary, UnityAction onClick, out TMP_Text text)
    {
        var surface = Surface(name, parent, primary ? ForgeTheme.PillOn : ButtonRest, pill: true, glow: primary ? 0.35f : 0f);
        surface.raycastTarget = true;
        var button = Clickable(surface, onClick);
        if (primary)
        {
            var hover = surface.GetComponent<DeskHover>();
            hover.rest = ForgeTheme.PillOn;
            hover.lit = Color.white;
        }
        text = Text("Label", surface.transform, textSize, primary ? FontStyles.Bold : FontStyles.Normal,
                    primary ? ForgeTheme.PillOnText : ForgeTheme.PillOffText, TextAlignmentOptions.Center);
        Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        text.text = label;
        surface.rectTransform.sizeDelta = new Vector2(text.GetPreferredValues(label).x + 2f * textSize + 6f, height);
        return button;
    }

    static Button Clickable(ForgeSurface surface, UnityAction onClick)
    {
        var button = surface.gameObject.AddComponent<Button>();
        button.targetGraphic = surface;
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        if (onClick != null) button.onClick.AddListener(onClick);
        var hover = surface.gameObject.AddComponent<DeskHover>();
        hover.target = surface;
        hover.rest = surface.color;
        hover.lit = ButtonLit;
        return button;
    }

    /// <summary>"42:18", "1:02:03": the digits fixed-width so a countdown doesn't jitter (TMP mspace).</summary>
    public static string Clock(float seconds, bool roundUp)
    {
        int t = Mathf.Max(0, roundUp ? Mathf.CeilToInt(seconds - 0.001f) : Mathf.FloorToInt(seconds));
        int h = t / 3600, m = t / 60 % 60, s = t % 60;
        return h > 0
            ? $"<mspace=0.6em>{h}</mspace>:<mspace=0.6em>{m:00}</mspace>:<mspace=0.6em>{s:00}</mspace>"
            : $"<mspace=0.6em>{m:00}</mspace>:<mspace=0.6em>{s:00}</mspace>";
    }

    /// <summary>"1 hour", "1 hour 30 minutes", "25 minutes", "45 seconds" (as the hub says it).</summary>
    public static string Human(float seconds)
    {
        int s = Mathf.Max(0, Mathf.RoundToInt(seconds));
        if (s < 60) return s == 1 ? "1 second" : $"{s} seconds";
        int total = Mathf.RoundToInt(s / 60f), h = total / 60, m = total % 60;
        string hours = h == 0 ? "" : h == 1 ? "1 hour" : $"{h} hours";
        string minutes = m == 0 ? "" : m == 1 ? "1 minute" : $"{m} minutes";
        return hours.Length > 0 && minutes.Length > 0 ? hours + " " + minutes : hours + minutes;
    }
}
