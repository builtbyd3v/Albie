using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A label in the CAD viewer (CAD UX spec, section 3): a graphite chip with a title (Inter, semibold) and an optional
/// second line at 64%, facing the eyes and resized every frame to stay about 0.9 deg tall (12.5 mm at 0.8 m), whatever
/// the model's scale. The chip is drawn by its owner's <see cref="CadChips"/> batch (<see cref="Draw"/>), the text by
/// TextMeshPro. It fades through its material block (no text rebuilds) and eases toward where it's sent.
/// <see cref="MoveTo"/> puts one point of the chip on the target: its left edge (anchor 1: the chip reads rightward from
/// the point), its right edge (-1) or its centre (0), at mid height. Lives under the viewer's overlay root.
/// </summary>
public sealed class CadLabel
{
    /// <summary>Chip: explode labels. Read: live readouts. Tag: pills ("Slow motion ÷100"). Mark: markers. Dim: dimension
    /// values. Event / EventOn: flight events (EventOn once reached).</summary>
    public enum Style { Chip, Read, Tag, Mark, Dim, Event, EventOn }

    struct Metrics
    {
        public float padX, padY, radius, title, sub, opacity;
    }

    static Metrics MetricsOf(Style s)
    {
        switch (s)
        {
            case Style.Read: return new Metrics { padX = 11f, padY = 7f, radius = 10f, title = 14f, sub = 10.5f, opacity = 0.86f };
            case Style.Tag: return new Metrics { padX = 12f, padY = 6f, radius = 15f, title = 12f, sub = 11f, opacity = 0.84f };
            case Style.Mark: return new Metrics { padX = 8f, padY = 4f, radius = 7f, title = 11.5f, sub = 10f, opacity = 0.78f };
            case Style.Dim: return new Metrics { padX = 8f, padY = 4f, radius = 7f, title = 12f, sub = 10f, opacity = 0.84f };
            case Style.Event: return new Metrics { padX = 9f, padY = 5f, radius = 8f, title = 11f, sub = 10f, opacity = 0.62f };
            case Style.EventOn: return new Metrics { padX = 9f, padY = 5f, radius = 8f, title = 13f, sub = 10.5f, opacity = 0.84f };
            default: return new Metrics { padX = 10f, padY = 6f, radius = 9f, title = CadLook.TitleMm, sub = CadLook.SubMm, opacity = 0.82f };
        }
    }

    static readonly int FaceColorId = Shader.PropertyToID("_FaceColor");

    public readonly TextMeshPro text;
    readonly MeshRenderer renderer;
    readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    Metrics metrics;
    Style style;
    Color tint = Color.white;
    float alpha = -1f;
    int anchor = 2;
    bool placed;
    Vector3 point;                    // where its anchor point is (eased), world
    float metresPerMm = 0.001f;
    Vector2 textMin, textMax;         // the text's extents in its own mm, after layout
    string percent;                   // the second line's size, as a share of the title's

    public Transform Transform => text.transform;
    public float Alpha => Mathf.Max(0f, alpha);
    public Style CurrentStyle => style;
    /// <summary>The chip's height in the room (m).</summary>
    public float Height => (textMax.y - textMin.y + 2f * metrics.padY) * metresPerMm;
    /// <summary>The chip's width in the room (m).</summary>
    public float Width => (textMax.x - textMin.x + 2f * metrics.padX) * metresPerMm;

    public CadLabel(Transform parent, string name, Style labelStyle = Style.Chip)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * 0.001f;
        text = go.AddComponent<TextMeshPro>();
        if (ForgeTheme.Font != null)
        {
            text.font = ForgeTheme.Font;
            text.fontSharedMaterial = CadLook.TextMaterial;
        }
        text.fontStyle = FontStyles.Normal;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.richText = true;
        text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        text.rectTransform.sizeDelta = new Vector2(10f, 10f);
        text.text = "";
        renderer = go.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        style = labelStyle;
        metrics = MetricsOf(labelStyle);
        Restyle();
        SetAnchor(0);
        SetAlpha(0f);
    }

    /// <summary>A chip label with its title <paramref name="sizeMm"/> tall (older callers).</summary>
    public CadLabel(Transform parent, string name, float sizeMm, bool bold = false) : this(parent, name, Style.Chip)
    {
        metrics.title = sizeMm;
        Restyle();
    }

    public void SetStyle(Style labelStyle)
    {
        if (labelStyle == style) return;
        style = labelStyle;
        metrics = MetricsOf(labelStyle);
        Restyle();
        Measure();
    }

    void Restyle()
    {
        if (text == null) return;
        text.fontSize = metrics.title * 10f;  // TextMeshPro (3D) is 0.1 unit per point, and 1 unit = 1 mm here
        percent = Mathf.RoundToInt(100f * metrics.sub / Mathf.Max(metrics.title, 1f)).ToString();
    }

    /// <summary>A title (semibold) and an optional second line (smaller, 64%).</summary>
    public void SetText(string title, string sub = null)
    {
        if (text == null) return;
        SetRaw(string.IsNullOrEmpty(sub)
            ? "<b>" + title + "</b>"
            : "<b>" + title + "</b>\n<size=" + percent + "%><alpha=#A3>" + sub);
    }

    /// <summary>Rich text as is.</summary>
    public void SetRaw(string value)
    {
        if (text == null || text.text == value) return;
        text.text = value;
        Measure();
    }

    /// <summary>Live numbers without allocating: TextMeshPro's SetText format ("{0:0} m", "{1:1} s").</summary>
    public void SetFormat(string format, float a, float b, float c)
    {
        if (text == null) return;
        text.SetText(format, a, b, c);
        Measure();
    }

    /// <summary>Title colour (white by default).</summary>
    public void SetTint(Color color)
    {
        if (color == tint) return;
        tint = color;
        float a = alpha;
        alpha = -1f;
        SetAlpha(a);
    }

    /// <summary>Which point of the chip <see cref="MoveTo"/> places: its left edge (1), right edge (-1) or centre (0).</summary>
    public void SetAnchor(int side)
    {
        if (side == anchor || text == null) return;
        bool realign = anchor == 2 || (side == 0) != (anchor == 0);
        anchor = side;
        if (!realign) return;
        text.alignment = side == 0 ? TextAlignmentOptions.Center : TextAlignmentOptions.Left;
        Measure();
    }

    public void SetAlpha(float a)
    {
        if (renderer == null) return;  // torn down (leaving Play)
        a = Mathf.Clamp01(a);
        if (Mathf.Abs(a - alpha) < 0.004f && (a > 0f) == (alpha > 0f)) return;
        alpha = a;
        bool show = a > 0.003f;
        if (renderer.enabled != show) renderer.enabled = show;
        if (!show) return;
        block.Clear();
        block.SetColor(FaceColorId, new Color(tint.r, tint.g, tint.b, a));
        renderer.SetPropertyBlock(block);
    }

    public void Hide()
    {
        SetAlpha(0f);
        placed = false;
    }

    /// <summary>
    /// Ease the anchor point toward <paramref name="world"/> (snap the first time, or with <paramref name="snap"/>),
    /// upright and facing <paramref name="eye"/>, sized for its distance.
    /// </summary>
    public void MoveTo(Vector3 world, Vector3 eye, float dt, bool snap = false)
    {
        if (text == null) return;
        point = !placed || snap ? world : Vector3.Lerp(point, world, 1f - Mathf.Exp(-dt * 14f));
        placed = true;
        var t = text.transform;
        Vector3 toPoint = point - eye;
        float distance = toPoint.magnitude;
        Quaternion rotation = distance > 1e-4f ? Quaternion.LookRotation(toPoint / distance, Vector3.up) : t.rotation;
        // About 0.9 deg tall at any distance, in 2% steps so the text isn't re-meshed every frame.
        float s = Mathf.Round(Mathf.Clamp(distance / CadLook.ReferenceDistance, 0.4f, 3f) * 50f) / 50f;
        metresPerMm = 0.001f * s;
        var parent = t.parent;
        float local = metresPerMm / Mathf.Max(parent != null ? parent.lossyScale.x : 1f, 1e-6f);
        if (Mathf.Abs(t.localScale.x - local) > local * 0.002f) t.localScale = new Vector3(local, local, local);
        float x = anchor > 0 ? textMin.x - metrics.padX : anchor < 0 ? textMax.x + metrics.padX : (textMin.x + textMax.x) * 0.5f;
        float y = (textMin.y + textMax.y) * 0.5f;
        Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
        t.SetPositionAndRotation(point - right * (x * metresPerMm) - up * (y * metresPerMm), rotation);
    }

    /// <summary>The middle of the chip's left (-1) or right (1) edge, or its centre (0), in the room.</summary>
    public Vector3 Edge(int side)
    {
        if (text == null) return point;
        var t = text.transform;
        float x = side > 0 ? textMax.x + metrics.padX : side < 0 ? textMin.x - metrics.padX : (textMin.x + textMax.x) * 0.5f;
        float y = (textMin.y + textMax.y) * 0.5f;
        return t.position + t.right * (x * metresPerMm) + t.up * (y * metresPerMm);
    }

    /// <summary>Add this label's chip to the batch (nothing while it's hidden).</summary>
    public void Draw(CadChips chips)
    {
        if (chips == null || text == null || alpha <= 0.003f) return;
        var t = text.transform;
        var half = new Vector2((textMax.x - textMin.x) * 0.5f + metrics.padX, (textMax.y - textMin.y) * 0.5f + metrics.padY);
        Vector3 right = t.right, up = t.up;
        Vector3 centre = t.position + right * ((textMin.x + textMax.x) * 0.5f * metresPerMm) + up * ((textMin.y + textMax.y) * 0.5f * metresPerMm);
        chips.Chip(centre, right, up, half, metrics.radius, metresPerMm, CadLook.Alpha(CadLook.Chip, metrics.opacity * alpha), 0.22f * alpha);
    }

    public void Destroy()
    {
        if (text != null) Object.Destroy(text.gameObject);
    }

    // The text's real extents (mm, its own space) once laid out: the chip wraps them.
    void Measure()
    {
        if (text == null) return;
        if (string.IsNullOrEmpty(text.text))
        {
            textMin = textMax = Vector2.zero;
            return;
        }
        text.ForceMeshUpdate();
        var b = text.textBounds;
        textMin = new Vector2(b.min.x, b.min.y);
        textMax = new Vector2(b.max.x, b.max.y);
    }
}
