using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Messages module's look and small UI factories, in the style of CAD's CadUi: Hudson's graphite ForgePanel frame
/// outside, and inside it the module's own card fill (the slightly lighter translucent slate the digest cards always had:
/// 0.12 / 0.15 / 0.21 at 0.8 alpha, opaque Inter text, soft shadow, rounded) for message rows, conversation rows, the
/// composer and the Mentions rows. Text colours from <see cref="ForgeTheme"/>, accent = ForgeTheme.Messages.
/// 1 canvas unit = 1 mm; rects are placed from their parent's top left with y down.
/// </summary>
public static class SlackUi
{
    // ---- Colours ----

    public static Color Accent => ForgeTheme.Messages;                        // #A77BFF
    public static readonly Color CardFill = new Color(0.12f, 0.15f, 0.21f, 0.8f);
    public static readonly Color CardFillHover = new Color(0.14f, 0.17f, 0.24f, 0.92f);
    public static readonly Color CardFillOn = new Color(0.17f, 0.20f, 0.28f, 1f);
    public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.28f);
    public static Color TextColor => ForgeTheme.PanelText;                          // white 95%
    public static Color Subtext => ForgeTheme.PanelSubtext;                    // white 50%
    public static readonly Color Muted = new Color(1f, 1f, 1f, 0.68f);
    public static readonly Color Ink = ForgeTheme.PillOnText;                   // dark text on a light / accent pill
    public static Color White(float a) => new Color(1f, 1f, 1f, a);
    public static Color AccentA(float a) => new Color(Accent.r, Accent.g, Accent.b, a);
    /// <summary>The user's own rows: the card fill nudged toward the accent.</summary>
    public static Color MeFill => new Color(Mathf.Lerp(CardFill.r, Accent.r, 0.14f), Mathf.Lerp(CardFill.g, Accent.g, 0.14f), Mathf.Lerp(CardFill.b, Accent.b, 0.14f), CardFill.a);

    // Initials bubbles, by a stable hash of the sender.
    public static readonly Color[] BubbleColors =
    {
        new Color(0.25f, 0.55f, 1f), new Color(0.85f, 0.4f, 0.9f), new Color(0.3f, 0.8f, 0.45f),
        new Color(1f, 0.55f, 0.2f), new Color(0.95f, 0.35f, 0.4f), new Color(0.25f, 0.8f, 0.85f),
    };
    public static Color BubbleColor(string key) => BubbleColors[SlackText.StableHash(key) % BubbleColors.Length];

    // ---- Type (mm) ----

    public const float Title = 17f, Name = 13f, Body = 12.5f, Small = 11.5f, Overline = 11f, Time = 10.5f, Pill = 12f;
    public const float CardCorner = 14f, RowCorner = 10f;

    // ---- Rects ----

    public static RectTransform Rect(Transform parent, string name)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>x, y from the parent's top left (y down), w x h.</summary>
    public static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x + w * 0.5f, -(y + h * 0.5f));
    }

    /// <summary>Hung from the parent's top right: <paramref name="right"/> from its right edge to the rect's.</summary>
    public static void PlaceRight(RectTransform rt, float right, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(-(right + w * 0.5f), -(y + h * 0.5f));
    }

    /// <summary>The parent's width less <paramref name="left"/> and <paramref name="right"/>, <paramref name="y"/> from its top.</summary>
    public static void Band(RectTransform rt, float y, float h, float left = 0f, float right = 0f)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, -(y + h));
        rt.offsetMax = new Vector2(-right, -y);
    }

    /// <summary>Fills the parent less insets.</summary>
    public static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>A top-anchored strip whose height the owner sets (scroll contents, columns).</summary>
    public static void TopStrip(RectTransform rt, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(0f, -height);
        rt.offsetMax = Vector2.zero;
    }

    // ---- Text ----

    public static TextMeshProUGUI Text(Transform parent, string name, float size, Color color, bool bold = false,
        TextAlignmentOptions align = TextAlignmentOptions.Left, bool wrap = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        ApplyFont(t);
        t.fontSize = size;
        t.color = color;
        t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        t.alignment = align;
        t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        // Overflow, not Ellipsis, by default: TMP hides a line outright when its box is a hair shorter than the font's
        // line height. Rows that must cut off set Ellipsis themselves.
        t.overflowMode = TextOverflowModes.Overflow;
        t.richText = true;
        t.raycastTarget = false;
        t.text = "";
        return t;
    }

    /// <summary>Inter (Core's ForgeLook asset) when it exists, else TMP's default font.</summary>
    public static void ApplyFont(TMP_Text t)
    {
        if (t == null) return;
        var font = ForgeTheme.Font;
        if (font != null)
        {
            t.font = font;
            t.fontSharedMaterial = font.material;
        }
    }

    /// <summary>Overline: 11 mm caps, wide tracking, 50% ("CHANNELS", "NEW FOR YOU").</summary>
    public static TextMeshProUGUI OverlineText(Transform parent, string name)
    {
        var t = Text(parent, name, Overline, Subtext, bold: true);
        t.characterSpacing = 10f;
        t.fontStyle |= FontStyles.UpperCase;
        return t;
    }

    public static float Width(TMP_Text t, string s) => string.IsNullOrEmpty(s) ? 0f : t.GetPreferredValues(s).x;

    public static float Height(TMP_Text t, string s, float width) =>
        string.IsNullOrEmpty(s) ? 0f : t.GetPreferredValues(s, Mathf.Max(1f, width), 0f).y;

    // ---- Surfaces ----

    public static ForgeSurface Surface(Transform parent, string name, Color color, float corner, float outline = 0f)
    {
        var rt = Rect(parent, name);
        var s = rt.gameObject.AddComponent<ForgeSurface>();
        s.Style = ForgeSurface.Kind.Fill;
        s.Corner = corner;
        s.Outline = outline;
        s.color = color;
        s.raycastTarget = false;
        return s;
    }

    public static ForgeSurface PillSurface(Transform parent, string name, Color color, float glow = 0f)
    {
        var s = Surface(parent, name, color, 0f);
        s.Pill = true;
        s.Glow = glow;
        return s;
    }

    /// <summary>
    /// The module's card: a soft shadow (a dark fill with a glow, set a little low) under the translucent slate fill.
    /// Returns the fill; the shadow is its sibling just behind it, stretched to the same rect. Both clip under a
    /// RectMask2D. <paramref name="raycast"/> makes the fill take the pointer (a clickable row).
    /// </summary>
    public static ForgeSurface Card(RectTransform rect, float corner, bool raycast)
    {
        var shadow = Surface(rect, "Shadow", Shadow, corner);
        shadow.Glow = 0.7f;
        Stretch(shadow.rectTransform, 2f, 3f, 2f, -1f);
        var fill = Surface(rect, "Fill", CardFill, corner);
        Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);
        fill.raycastTarget = raycast;
        return fill;
    }

    public static SlackHitArea Hit(Transform parent, string name)
    {
        var rt = Rect(parent, name);
        var hit = rt.gameObject.AddComponent<SlackHitArea>();
        hit.raycastTarget = true;
        return hit;
    }

    public static CanvasGroup Group(GameObject go)
    {
        var g = go.GetComponent<CanvasGroup>();
        return g != null ? g : go.AddComponent<CanvasGroup>();
    }

    public static void Show(Component c, bool on)
    {
        if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
    }

    /// <summary>
    /// A vertical scroll list: an invisible viewport that takes the pointer (so dragging empty space scrolls), a
    /// RectMask2D and a top-anchored content strip whose height the owner sets after laying its rows out. Mouse drags
    /// (DesktopRig) and Interaction SDK ray / poke drags both scroll it; a press that moves is a drag, not a click
    /// (both input paths cancel the click past their slop), so dragging the list by a row never presses the row.
    /// </summary>
    public static ScrollRect ScrollList(Transform parent, string name, out RectTransform content)
    {
        var viewport = Hit(parent, name).rectTransform;
        viewport.gameObject.AddComponent<RectMask2D>();
        content = Rect(viewport, "Content");
        TopStrip(content, 100f);
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Elastic;
        scroll.elasticity = 0.08f;
        scroll.inertia = true;
        scroll.decelerationRate = 0.12f;
        scroll.scrollSensitivity = 18f;
        return scroll;
    }
}
