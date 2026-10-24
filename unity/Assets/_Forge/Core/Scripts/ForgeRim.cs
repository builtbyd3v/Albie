using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// An outline of light round a pill or a circle, lit for <see cref="Progress"/> of the way clockwise from the top: a
/// timer's rim draining as its time runs out, or (with <see cref="Tail"/>, turned) a comet running round a ring. A thin
/// bright stroke with a soft glow either side; the unlit part is a faint track. Drawn as a mesh by the default UI
/// shader (no texture, crisp at any size), so it layers with the rest of the canvas. The rect's height is the pill's
/// (keep it no taller than wide); a square rect is a circle. Sizes are canvas units (mm on Forge canvases).
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class ForgeRim : MaskableGraphic
{
    const int ArcSegments = 28;

    [SerializeField, Range(0f, 1f)] float progress = 1f;
    [Tooltip("Width of the bright line (mm).")]
    [SerializeField] float stroke = 1.7f;
    [Tooltip("Soft glow either side of the line (mm).")]
    [SerializeField] float glow = 6f;
    [SerializeField, Range(0f, 1f)] float glowAlpha = 0.5f;
    [Tooltip("The unlit part, as a share of the color's alpha.")]
    [SerializeField, Range(0f, 1f)] float trackAlpha = 0.14f;
    [Tooltip("The line's centre from the rect's edge (mm); negative draws outside it.")]
    [SerializeField] float inset = 0.85f;
    [Tooltip("The glow inside the line, as a share of the outer glow (a rim on a dark pill wants little).")]
    [SerializeField, Range(0f, 1f)] float innerGlow = 1f;
    [Tooltip("The lit part fades in from its start: a comet's tail.")]
    [SerializeField] bool tail;

    // The outline as a polyline, rebuilt only when the mesh is (shared: one canvas rebuilds at a time).
    static readonly List<Vector2> points = new List<Vector2>(80);
    static readonly List<Vector2> normals = new List<Vector2>(80);
    static readonly List<float> lengths = new List<float>(80);

    public float Progress
    {
        get => progress;
        set
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Abs(value - progress) < 1e-4f) return;
            progress = value;
            SetVerticesDirty();
        }
    }

    public float Stroke { get => stroke; set { stroke = value; SetVerticesDirty(); } }
    public float Glow { get => glow; set { glow = value; SetVerticesDirty(); } }
    public float GlowAlpha { get => glowAlpha; set { glowAlpha = value; SetVerticesDirty(); } }
    public float TrackAlpha { get => trackAlpha; set { trackAlpha = value; SetVerticesDirty(); } }
    public float Inset { get => inset; set { inset = value; SetVerticesDirty(); } }
    public float InnerGlow { get => innerGlow; set { innerGlow = value; SetVerticesDirty(); } }
    public bool Tail { get => tail; set { tail = value; SetVerticesDirty(); } }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        float radius = Mathf.Min(r.width, r.height) * 0.5f - inset;
        if (radius <= 0f) return;
        float half = Mathf.Max(0f, r.width * 0.5f - inset - radius);  // half the straight part
        BuildPath(r.center, radius, half);

        float lit = progress * lengths[lengths.Count - 1];
        for (int i = 0; i + 1 < points.Count; i++)
        {
            float s0 = lengths[i], s1 = lengths[i + 1];
            if (s1 - s0 < 1e-4f) continue;
            if (lit > s0 && lit < s1)
            {
                float t = (lit - s0) / (s1 - s0);
                Vector2 p = Vector2.Lerp(points[i], points[i + 1], t);
                Vector2 n = Vector2.Lerp(normals[i], normals[i + 1], t).normalized;
                Segment(vh, points[i], normals[i], p, n, Lit(s0, lit), Lit(lit, lit), true);
                Segment(vh, p, n, points[i + 1], normals[i + 1], trackAlpha, trackAlpha, false);
            }
            else if (s1 <= lit) Segment(vh, points[i], normals[i], points[i + 1], normals[i + 1], Lit(s0, lit), Lit(s1, lit), true);
            else Segment(vh, points[i], normals[i], points[i + 1], normals[i + 1], trackAlpha, trackAlpha, false);
        }
    }

    // How bright the lit part is this far along it: full, or with a tail, rising from nothing at its start.
    float Lit(float s, float lit) => tail ? (lit > 0f ? s / lit : 0f) : 1f;

    // Clockwise from the top middle: the top's right half, the right end, the bottom, the left end, the top's left half.
    static void BuildPath(Vector2 c, float radius, float half)
    {
        points.Clear();
        normals.Clear();
        lengths.Clear();
        Add(new Vector2(c.x, c.y + radius), Vector2.up);
        if (half > 0f) Add(new Vector2(c.x + half, c.y + radius), Vector2.up);
        Arc(new Vector2(c.x + half, c.y), radius, 90f, -90f);
        if (half > 0f) Add(new Vector2(c.x - half, c.y - radius), Vector2.down);
        Arc(new Vector2(c.x - half, c.y), radius, -90f, -270f);
        if (half > 0f) Add(new Vector2(c.x, c.y + radius), Vector2.up);
    }

    static void Arc(Vector2 center, float radius, float fromDeg, float toDeg)
    {
        for (int k = 1; k <= ArcSegments; k++)
        {
            float a = Mathf.Lerp(fromDeg, toDeg, k / (float)ArcSegments) * Mathf.Deg2Rad;
            var n = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Add(center + n * radius, n);
        }
    }

    static void Add(Vector2 p, Vector2 n)
    {
        lengths.Add(points.Count == 0 ? 0f : lengths[lengths.Count - 1] + Vector2.Distance(points[points.Count - 1], p));
        points.Add(p);
        normals.Add(n);
    }

    // One piece of the outline, `ka` / `kb` of the color's alpha at its ends. Only the lit part glows.
    void Segment(VertexHelper vh, Vector2 a, Vector2 na, Vector2 b, Vector2 nb, float ka, float kb, bool lit)
    {
        float h = stroke * 0.5f;
        Color32 ca = Tint(ka), cb = Tint(kb);
        Band(vh, a, na, b, nb, -h, h, ca, ca, cb, cb);
        if (!lit || glow <= 0f || glowAlpha <= 0f) return;
        Color32 ga = Tint(ka * glowAlpha), gb = Tint(kb * glowAlpha), clear = Tint(0f);
        Band(vh, a, na, b, nb, h, h + glow, ga, clear, gb, clear);
        if (innerGlow <= 0f) return;
        Color32 ia = Tint(ka * glowAlpha * innerGlow), ib = Tint(kb * glowAlpha * innerGlow);
        Band(vh, a, na, b, nb, -h - glow * innerGlow, -h, clear, ia, clear, ib);
    }

    Color32 Tint(float k) => new Color(color.r, color.g, color.b, color.a * Mathf.Clamp01(k));

    // A strip between offsets d0 and d1 along the normals; colors at a (d0, d1) and at b (d0, d1).
    static void Band(VertexHelper vh, Vector2 a, Vector2 na, Vector2 b, Vector2 nb, float d0, float d1,
        Color32 a0, Color32 a1, Color32 b0, Color32 b1)
    {
        int i = vh.currentVertCount;
        vh.AddVert(a + na * d0, a0, Vector4.zero);
        vh.AddVert(a + na * d1, a1, Vector4.zero);
        vh.AddVert(b + nb * d1, b1, Vector4.zero);
        vh.AddVert(b + nb * d0, b0, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i + 2, i + 3, i);
    }
}
