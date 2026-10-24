using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Vector drawing on a Forge canvas, for the design panel's dial, instruments, plots, sparkline and icons:
/// anti-aliased strokes (polylines with mitred joins, round caps, arcs, circles, dashes) and fills (polygons, discs,
/// rings, the area under a curve, gradients), in this rect's mm with the origin at its top left and y down, like the
/// boards' SVG. Drawn with ForgeSurface's material (Forge/PanelSurface in its flat-fill style with an endless shape),
/// so it batches with the panel's surfaces, composites over passthrough and clips in scroll views.
/// <see cref="paint"/> runs whenever the mesh rebuilds (a resize, <see cref="Redraw"/>): a painter reads its data and
/// this rect's size there. Edges are softened by a 0.7 mm alpha ramp. No allocations once the shared buffers have grown.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class CadShape : MaskableGraphic
{
    /// <summary>Draws the shape: called whenever the mesh rebuilds.</summary>
    public System.Action<CadShape> paint;

    /// <summary>Edge softening (mm), about a pixel at reading distance.</summary>
    public const float Feather = 0.7f;
    const int MaxVertices = 60000;

    static readonly List<Vector2> path = new List<Vector2>(512);
    static readonly List<int> starts = new List<int>(16);
    static readonly List<bool> closes = new List<bool>(16);
    static readonly List<Vector2> dash = new List<Vector2>(128);
    static readonly List<float> rows = new List<float>(32);
    static UIVertex template;
    static bool templated;

    VertexHelper vh;
    float ox, oy;          // the rect's top left, local
    float tx, ty, ts = 1f; // where path points go (an icon's 24-unit box drawn at a size)

    public float Width => rectTransform.rect.width;
    public float Height => rectTransform.rect.height;

    public override Material defaultMaterial => ForgeTheme.PanelSurface != null ? ForgeTheme.PanelSurface : base.defaultMaterial;

    protected override void OnEnable()
    {
        base.OnEnable();
        // The shape travels in uv1 / uv2, which a canvas only passes on when asked (as for ForgeSurface).
        if (canvas != null)
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;
    }

    public void Redraw() => SetVerticesDirty();

    protected override void OnPopulateMesh(VertexHelper helper)
    {
        helper.Clear();
        if (paint == null) return;
        if (!templated)
        {
            template = UIVertex.simpleVert;
            template.uv0 = Vector4.zero;
            template.uv1 = new Vector4(1e5f, 1e5f, 0f, 0f);  // an endless shape: everything is inside it
            template.uv2 = new Vector4(1f, 0f, 0f, 0f);      // PanelSurface's flat fill, no glow, no outline
            templated = true;
        }
        Rect r = GetPixelAdjustedRect();
        ox = r.xMin;
        oy = r.yMax;
        tx = ty = 0f;
        ts = 1f;
        vh = helper;
        try { paint(this); }
        finally { vh = null; }
    }

    // ---- Where path points go ----

    /// <summary>Path points that follow land at (x + px * scale, y + py * scale): an icon's own units drawn at a size.</summary>
    public CadShape At(float x, float y, float scale = 1f)
    {
        tx = x;
        ty = y;
        ts = scale;
        return this;
    }

    Vector2 P(float x, float y) => new Vector2(tx + x * ts, ty + y * ts);

    // ---- Paths: build, then Stroke / Fill / Dashed ----

    public CadShape Begin()
    {
        path.Clear();
        starts.Clear();
        closes.Clear();
        return this;
    }

    public CadShape M(float x, float y)
    {
        starts.Add(path.Count);
        closes.Add(false);
        path.Add(P(x, y));
        return this;
    }

    public CadShape L(float x, float y)
    {
        if (starts.Count == 0) return M(x, y);
        path.Add(P(x, y));
        return this;
    }

    /// <summary>A cubic Bezier from the last point (SVG C).</summary>
    public CadShape C(float x1, float y1, float x2, float y2, float x, float y)
    {
        if (path.Count == 0 || starts.Count == 0) return M(x, y);
        Vector2 p0 = path[path.Count - 1], p1 = P(x1, y1), p2 = P(x2, y2), p3 = P(x, y);
        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps, u = 1f - t;
            path.Add(u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
        }
        return this;
    }

    /// <summary>
    /// Points along an arc about (cx, cy), from angle <paramref name="a0"/> to <paramref name="a1"/> (radians, maths
    /// style: 0 right, pi/2 up); joins the current subpath, or starts one if <paramref name="start"/>.
    /// </summary>
    public CadShape Arc(float cx, float cy, float r, float a0, float a1, bool start = false)
    {
        int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(a1 - a0) * r * ts / 1.1f), 4, 128);
        for (int i = 0; i <= n; i++)
        {
            float a = Mathf.Lerp(a0, a1, i / (float)n);
            float x = cx + r * Mathf.Cos(a), y = cy - r * Mathf.Sin(a);
            if (i == 0 && (start || starts.Count == 0)) M(x, y);
            else L(x, y);
        }
        return this;
    }

    public CadShape Z()
    {
        if (closes.Count > 0) closes[closes.Count - 1] = true;
        return this;
    }

    public CadShape Circle(float cx, float cy, float r)
    {
        int n = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * r * ts / 1.1f), 10, 128);
        for (int i = 0; i < n; i++)
        {
            float a = 2f * Mathf.PI * i / n;
            float x = cx + r * Mathf.Cos(a), y = cy - r * Mathf.Sin(a);
            if (i == 0) M(x, y);
            else L(x, y);
        }
        return Z();
    }

    public CadShape RoundRect(float x, float y, float w, float h, float r)
    {
        r = Mathf.Min(r, Mathf.Min(w, h) * 0.5f);
        const float q = Mathf.PI * 0.5f;
        Arc(x + w - r, y + r, r, 0f, q, start: true);
        Arc(x + r, y + r, r, q, 2f * q);
        Arc(x + r, y + h - r, r, 2f * q, 3f * q);
        Arc(x + w - r, y + h - r, r, 3f * q, 4f * q);
        return Z();
    }

    public void Stroke(float width, Color color, bool roundCaps = true)
    {
        for (int s = 0; s < starts.Count; s++)
        {
            Run(s, out int start, out int count);
            StrokeRun(path, start, count, closes[s], width, color, roundCaps);
        }
    }

    public void Fill(Color color)
    {
        for (int s = 0; s < starts.Count; s++)
        {
            Run(s, out int start, out int count);
            FillRun(path, start, count, color);
        }
    }

    /// <summary>Dashes along each subpath: <paramref name="on"/> mm drawn, <paramref name="off"/> mm gap.</summary>
    public void Dashed(float width, Color color, float on, float off)
    {
        for (int s = 0; s < starts.Count; s++)
        {
            Run(s, out int start, out int count);
            DashRun(start, count, closes[s], width, color, on, off);
        }
    }

    /// <summary>The first subpath as a curve, filled down (or up) to <paramref name="baseY"/>: the area under a plot.</summary>
    public void FillUnder(float baseY, Color color)
    {
        if (starts.Count == 0 || vh == null || Full) return;
        Run(0, out int start, out int count);
        if (count < 2) return;
        Color32 c = color;
        int first = vh.currentVertCount;
        for (int i = 0; i < count; i++)
        {
            Vector2 p = path[start + i];
            V(p, c);
            V(new Vector2(p.x, baseY), c);
        }
        for (int i = 0; i < count - 1; i++)
        {
            int a = first + 2 * i;
            Quad(a, a + 2, a + 3, a + 1);
        }
    }

    void Run(int s, out int start, out int count)
    {
        start = starts[s];
        count = (s + 1 < starts.Count ? starts[s + 1] : path.Count) - start;
        // A closed run whose last point repeats its first: drop the repeat.
        if (closes[s] && count > 2 && (path[start + count - 1] - path[start]).sqrMagnitude < 1e-8f) count--;
    }

    // ---- Shapes that don't need a path ----

    public void Line(float x0, float y0, float x1, float y1, float width, Color color, bool roundCaps = false)
    {
        Begin().M(x0, y0).L(x1, y1).Stroke(width, color, roundCaps);
    }

    public void Disc(float cx, float cy, float r, Color color) => Begin().Circle(cx, cy, r).Fill(color);

    public void Ring(float cx, float cy, float r, float width, Color color) => Begin().Circle(cx, cy, r).Stroke(width, color);

    /// <summary>A filled annulus between radii <paramref name="r0"/> and <paramref name="r1"/> (the pinion's rim).</summary>
    public void Annulus(float cx, float cy, float r0, float r1, Color color)
    {
        if (vh == null || Full || r1 <= r0) return;
        Vector2 c = P(cx, cy);
        float f = Feather * 0.5f;
        r0 *= ts;
        r1 *= ts;
        Color32 core = color, edge = Clear(color);
        int n = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * r1 / 1.1f), 12, 128);
        int first = vh.currentVertCount;
        for (int i = 0; i <= n; i++)
        {
            float a = 2f * Mathf.PI * i / n;
            var u = new Vector2(Mathf.Cos(a), -Mathf.Sin(a));
            V(c + u * Mathf.Max(0f, r0 - f), edge);
            V(c + u * (r0 + f), core);
            V(c + u * (r1 - f), core);
            V(c + u * (r1 + f), edge);
        }
        for (int i = 0; i < n; i++)
        {
            int a = first + 4 * i, b = a + 4;
            Quad(a, a + 1, b + 1, b);
            Quad(a + 1, a + 2, b + 2, b + 1);
            Quad(a + 2, a + 3, b + 3, b + 2);
        }
    }

    /// <summary>A plain rectangle (no rounding, no softening): bands behind a plot.</summary>
    public void FillRect(float x, float y, float w, float h, Color color)
    {
        if (vh == null || Full) return;
        Color32 c = color;
        Vector2 p = P(x, y);
        w *= ts;
        h *= ts;
        int a = V(p, c);
        V(new Vector2(p.x + w, p.y), c);
        V(new Vector2(p.x + w, p.y + h), c);
        V(new Vector2(p.x, p.y + h), c);
        Quad(a, a + 1, a + 2, a + 3);
    }

    /// <summary>A rectangle shading from <paramref name="left"/> to <paramref name="right"/>.</summary>
    public void GradientH(float x, float y, float w, float h, Color left, Color right)
    {
        if (vh == null || Full) return;
        Vector2 p = P(x, y);
        w *= ts;
        h *= ts;
        int a = V(p, left);
        V(new Vector2(p.x + w, p.y), right);
        V(new Vector2(p.x + w, p.y + h), right);
        V(new Vector2(p.x, p.y + h), left);
        Quad(a, a + 1, a + 2, a + 3);
    }

    /// <summary>A rectangle shading from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    public void GradientV(float x, float y, float w, float h, Color top, Color bottom)
    {
        if (vh == null || Full) return;
        Vector2 p = P(x, y);
        w *= ts;
        h *= ts;
        int a = V(p, top);
        V(new Vector2(p.x + w, p.y), top);
        V(new Vector2(p.x + w, p.y + h), bottom);
        V(new Vector2(p.x, p.y + h), bottom);
        Quad(a, a + 1, a + 2, a + 3);
    }

    /// <summary>
    /// A radial wash inside a rounded rectangle (0, 0, w, h, corner): <paramref name="color"/> at
    /// <paramref name="alpha"/> at (cx, cy), fading linearly to nothing at the ellipse (rx, ry): the boards'
    /// "radial-gradient(ellipse at 12% 0%, status 16%, transparent 62%)" behind the verdict. A grid that follows the
    /// corners, so the wash never shows outside the card.
    /// </summary>
    public void Wash(float w, float h, float corner, float cx, float cy, float rx, float ry, Color color, float alpha)
    {
        if (vh == null || Full || w <= 0f || h <= 0f) return;
        corner = Mathf.Min(corner, Mathf.Min(w, h) * 0.5f);
        rows.Clear();
        const int cornerRows = 5;
        for (int k = 0; k <= cornerRows; k++) rows.Add(corner * (1f - Mathf.Cos(k / (float)cornerRows * Mathf.PI * 0.5f)));
        float mid = h - 2f * corner;
        int midRows = Mathf.Max(1, Mathf.CeilToInt(mid / 28f));
        for (int k = 1; k < midRows; k++) rows.Add(corner + mid * k / midRows);
        for (int k = 0; k <= cornerRows; k++) rows.Add(h - corner + corner * Mathf.Sin(k / (float)cornerRows * Mathf.PI * 0.5f));
        const int columns = 14;
        int first = vh.currentVertCount;
        for (int i = 0; i < rows.Count; i++)
        {
            float y = rows[i];
            float inset = 0f;
            if (y < corner) inset = corner - Mathf.Sqrt(Mathf.Max(0f, corner * corner - (corner - y) * (corner - y)));
            else if (y > h - corner) inset = corner - Mathf.Sqrt(Mathf.Max(0f, corner * corner - (y - h + corner) * (y - h + corner)));
            for (int j = 0; j <= columns; j++)
            {
                float x = Mathf.Lerp(inset, w - inset, j / (float)columns);
                float dx = (x - cx) / Mathf.Max(rx, 1f), dy = (y - cy) / Mathf.Max(ry, 1f);
                float a = alpha * Mathf.Max(0f, 1f - Mathf.Sqrt(dx * dx + dy * dy));
                V(P(x, y), new Color(color.r, color.g, color.b, a));
            }
        }
        for (int i = 0; i < rows.Count - 1; i++)
            for (int j = 0; j < columns; j++)
            {
                int a = first + i * (columns + 1) + j, b = a + columns + 1;
                Quad(a, a + 1, b + 1, b);
            }
    }

    /// <summary>
    /// The building state's line of light (2 mm, a third of the card wide): clear, the CAD hue, warm white, clear,
    /// faded out toward <paramref name="clipLeft"/> and <paramref name="clipRight"/> so it never pokes past the card's
    /// rounded corners.
    /// </summary>
    public void Scan(float x, float length, float y, float height, float clipLeft, float clipRight, float alpha)
    {
        if (vh == null || Full || length <= 0f) return;
        const int columns = 24;
        Color hue = ForgeTheme.CAD, warm = new Color(1f, 0.953f, 0.902f, 1f);
        int first = vh.currentVertCount;
        for (int j = 0; j <= columns; j++)
        {
            float u = j / (float)columns, px = x + u * length;
            Color c = u < 1f / 3f ? Color.Lerp(Clear(hue), hue, u * 3f)
                : u < 2f / 3f ? Color.Lerp(hue, warm, u * 3f - 1f)
                : Color.Lerp(warm, Clear(hue), u * 3f - 2f);
            float fade = Mathf.Clamp01((px - clipLeft) / 16f) * Mathf.Clamp01((clipRight - px) / 16f);
            c.a *= alpha * fade;
            V(P(px, y), c);
            V(P(px, y + height), c);
        }
        for (int j = 0; j < columns; j++)
        {
            int a = first + 2 * j;
            Quad(a, a + 2, a + 3, a + 1);
        }
    }

    // ---- Mesh ----

    bool Full => vh.currentVertCount > MaxVertices;

    static Color Clear(Color c) => new Color(c.r, c.g, c.b, 0f);

    int V(Vector2 p, Color32 c)
    {
        var v = template;
        v.position = new Vector3(ox + p.x, oy - p.y, 0f);
        v.color = c;
        int i = vh.currentVertCount;
        vh.AddVert(v);
        return i;
    }

    void Tri(int a, int b, int c) => vh.AddTriangle(a, b, c);

    void Quad(int a, int b, int c, int d)
    {
        vh.AddTriangle(a, b, c);
        vh.AddTriangle(c, d, a);
    }

    static Vector2 Perp(Vector2 d) => new Vector2(-d.y, d.x);

    // A polyline as one strip: per point an outer edge, a core and the far edge, joined mitred (limited to 4x).
    void StrokeRun(List<Vector2> pts, int start, int n, bool closed, float width, Color color, bool round)
    {
        if (vh == null || Full || n < 2) return;
        float half = width * 0.5f, f = Feather * 0.5f, alpha = color.a;
        if (width < Feather)
        {
            alpha *= width / Feather;  // a hairline reads fainter rather than thinner
            half = Feather * 0.5f;
        }
        float inner = Mathf.Max(0f, half - f), outer = half + f;
        Color32 core = new Color(color.r, color.g, color.b, alpha), edge = Clear(color);
        int first = vh.currentVertCount;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = pts[start + i];
            Vector2 prev = i > 0 ? pts[start + i - 1] : closed ? pts[start + n - 1] : p;
            Vector2 next = i < n - 1 ? pts[start + i + 1] : closed ? pts[start] : p;
            Vector2 d0 = p - prev, d1 = next - p;
            Vector2 n0 = d0.sqrMagnitude > 1e-10f ? Perp(d0.normalized) : Vector2.zero;
            Vector2 n1 = d1.sqrMagnitude > 1e-10f ? Perp(d1.normalized) : Vector2.zero;
            Vector2 side = n0 + n1;
            if (side.sqrMagnitude < 1e-8f) side = n0.sqrMagnitude > 0f ? n0 : n1;
            side = side.sqrMagnitude > 0f ? side.normalized : Vector2.up;
            Vector2 reference = n0.sqrMagnitude > 0f ? n0 : n1;
            float cos = reference.sqrMagnitude > 0f ? Vector2.Dot(side, reference) : 1f;
            float k = cos > 0.25f ? 1f / cos : 4f;
            Vector2 o = side * (outer * k), m = side * (inner * k);
            V(p + o, edge);
            V(p + m, core);
            V(p - m, core);
            V(p - o, edge);
        }
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            int a = first + i * 4, b = first + ((i + 1) % n) * 4;
            Quad(a, a + 1, b + 1, b);
            Quad(a + 1, a + 2, b + 2, b + 1);
            Quad(a + 2, a + 3, b + 3, b + 2);
        }
        if (!closed && round && inner > 0f)
        {
            Cap(pts[start], pts[start] - pts[start + 1], inner, outer, core, edge);
            Cap(pts[start + n - 1], pts[start + n - 1] - pts[start + n - 2], inner, outer, core, edge);
        }
    }

    // A half disc beyond a stroke's end, sharing its end edge (no overlap, so faint strokes stay even).
    void Cap(Vector2 p, Vector2 outward, float inner, float outer, Color32 core, Color32 edge)
    {
        if (outward.sqrMagnitude < 1e-10f) return;
        Vector2 d = outward.normalized, side = Perp(d);
        const int steps = 8;
        int centre = V(p, core), lastIn = -1, lastOut = -1;
        for (int i = 0; i <= steps; i++)
        {
            float t = Mathf.PI * i / steps;
            Vector2 u = side * Mathf.Cos(t) + d * Mathf.Sin(t);
            int vi = V(p + u * inner, core), vo = V(p + u * outer, edge);
            if (i > 0)
            {
                Tri(centre, lastIn, vi);
                Quad(lastIn, lastOut, vo, vi);
            }
            lastIn = vi;
            lastOut = vo;
        }
    }

    // A polygon fanned from its vertices' centroid (convex, or star-shaped about it: a gear), with a soft rim.
    void FillRun(List<Vector2> pts, int start, int n, Color color)
    {
        if (vh == null || Full || n < 3) return;
        Vector2 c = Vector2.zero;
        float area = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = pts[start + i], b = pts[start + (i + 1) % n];
            c += a;
            area += a.x * b.y - b.x * a.y;
        }
        c /= n;
        float sign = area >= 0f ? 1f : -1f, f = Feather * 0.5f;
        Color32 core = color, edge = Clear(color);
        int centre = V(c, core);
        int first = vh.currentVertCount;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = pts[start + i], prev = pts[start + (i + n - 1) % n], next = pts[start + (i + 1) % n];
            Vector2 e0 = Outward(prev, p, sign), e1 = Outward(p, next, sign);
            Vector2 side = e0 + e1;
            side = side.sqrMagnitude > 1e-8f ? side.normalized : e0;
            float cos = Vector2.Dot(side, e0);
            float k = cos > 0.3f ? 1f / cos : 3.3f;
            V(p - side * (f * k), core);
            V(p + side * (f * k), edge);
        }
        for (int i = 0; i < n; i++)
        {
            int a = first + 2 * i, b = first + 2 * ((i + 1) % n);
            Tri(centre, a, b);
            Quad(a, a + 1, b + 1, b);
        }
    }

    // An edge's outward normal for a polygon of this winding (positive area: (dy, -dx), whatever way y points).
    static Vector2 Outward(Vector2 a, Vector2 b, float sign)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 1e-12f) return Vector2.zero;
        d.Normalize();
        return new Vector2(d.y, -d.x) * sign;
    }

    void DashRun(int start, int n, bool closed, float width, Color color, float on, float off)
    {
        if (vh == null || n < 2 || on <= 0f) return;
        off = Mathf.Max(0f, off);
        bool drawing = true;
        float left = on;  // what's left of the current dash (or gap)
        dash.Clear();
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            Vector2 a = path[start + i], b = path[start + (i + 1) % n];
            float length = Vector2.Distance(a, b), done = 0f;
            if (length < 1e-6f) continue;
            int guard = 0;
            while (done < length - 1e-6f && guard++ < 4096)
            {
                float step = Mathf.Min(left, length - done);
                if (drawing)
                {
                    if (dash.Count == 0) dash.Add(Vector2.Lerp(a, b, done / length));
                    dash.Add(Vector2.Lerp(a, b, (done + step) / length));
                }
                done += step;
                left -= step;
                if (left <= 1e-5f)
                {
                    if (drawing) FlushDash(width, color);
                    drawing = !drawing || off <= 0f;
                    left = drawing ? on : off;
                }
            }
        }
        FlushDash(width, color);
    }

    void FlushDash(float width, Color color)
    {
        if (dash.Count >= 2) StrokeRun(dash, 0, dash.Count, false, width, color, false);
        dash.Clear();
    }
}
