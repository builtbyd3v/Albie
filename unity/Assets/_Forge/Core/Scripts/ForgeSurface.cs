using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A rounded UI surface drawn by the Forge/PanelSurface shader (no sprites, crisp at any size): the graphite panel
/// window (gradient, rim light, header hairline, soft shadow), or a flat fill in this graphic's color with an optional
/// glow (pills, handle bars). Sizes are canvas units (mm on Forge canvases).
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class ForgeSurface : MaskableGraphic
{
    public enum Kind { Panel, Fill }

    [SerializeField] Kind kind = Kind.Fill;
    [Tooltip("Corner radius (mm). Pill rounds the ends fully instead.")]
    [SerializeField] float corner = 8f;
    [SerializeField] bool pill;
    [Tooltip("Fill: glow around the shape in its own color, 0..1. Panel: how lit the frame is (0.5 pointed at, 1 held).")]
    [SerializeField, Range(0f, 1f)] float glow;
    [Tooltip("Fill: a 1 mm outline in the same color at this alpha, 0 = none.")]
    [SerializeField, Range(0f, 1f)] float outline;
    [Tooltip("Panel: height of the header, where the hairline goes (mm). 0 = none.")]
    [SerializeField] float header;
    [Tooltip("Panel: drop shadow strength.")]
    [SerializeField, Range(0f, 1f)] float shadow = 1f;

    public Kind Style { get => kind; set { kind = value; SetVerticesDirty(); } }
    public float Corner { get => corner; set { corner = value; SetVerticesDirty(); } }
    public bool Pill { get => pill; set { pill = value; SetVerticesDirty(); } }
    public float Glow { get => glow; set { glow = value; SetVerticesDirty(); } }
    public float Outline { get => outline; set { outline = value; SetVerticesDirty(); } }
    public float Header { get => header; set { header = value; SetVerticesDirty(); } }
    public float Shadow { get => shadow; set { shadow = value; SetVerticesDirty(); } }

    public override Material defaultMaterial => ForgeTheme.PanelSurface != null ? ForgeTheme.PanelSurface : base.defaultMaterial;

    protected override void OnEnable()
    {
        base.OnEnable();
        // The shape travels in uv1 / uv2, which a canvas only passes on when asked.
        if (canvas != null)
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        Vector2 half = r.size * 0.5f;
        Vector2 center = r.center;
        float radius = pill ? Mathf.Min(half.x, half.y) : Mathf.Min(corner, Mathf.Min(half.x, half.y));
        // Room outside the shape for the shadow / glow.
        float pad = kind == Kind.Panel ? 56f * shadow + 1f : glow > 0f ? 14f : 1f;
        // A pill's ends are truly round (the shader reads a negative corner that way); other corners are squircles.
        var shape = new Vector4(half.x, half.y, pill ? -radius : radius, kind == Kind.Panel ? header : 0f);
        var style = new Vector4(kind == Kind.Panel ? 0f : 1f, glow, shadow, outline);
        Color32 c = color;
        float x0 = -half.x - pad, x1 = half.x + pad;
        float y0 = -half.y - pad - (kind == Kind.Panel ? 16f * shadow : 0f), y1 = half.y + pad;
        vh.AddVert(Vertex(center, new Vector2(x0, y0), c, shape, style));
        vh.AddVert(Vertex(center, new Vector2(x0, y1), c, shape, style));
        vh.AddVert(Vertex(center, new Vector2(x1, y1), c, shape, style));
        vh.AddVert(Vertex(center, new Vector2(x1, y0), c, shape, style));
        vh.AddTriangle(0, 1, 2);
        vh.AddTriangle(2, 3, 0);
    }

    static UIVertex Vertex(Vector2 center, Vector2 p, Color32 c, Vector4 shape, Vector4 style)
    {
        var v = UIVertex.simpleVert;
        v.position = new Vector3(center.x + p.x, center.y + p.y, 0f);
        v.color = c;
        // z: the color's own alpha, so the shader can tell how far a CanvasGroup has faded this graphic.
        v.uv0 = new Vector4(p.x, p.y, c.a / 255f, 0f);
        v.uv1 = shape;
        v.uv2 = style;
        return v;
    }
}
