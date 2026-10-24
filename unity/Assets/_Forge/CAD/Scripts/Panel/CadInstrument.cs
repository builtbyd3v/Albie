using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The verdict card's instrument (CAD UX spec 2.2): the design at true proportions in 156 x 196 mm, from its
/// parameters, with what the verdict is about. Rocket: side view, CG (blue) and CP (red) from the tip, their gap
/// bracketed in the status colour. Quadcopter: top view, prop discs, the gap between neighbouring tips in green or red
/// lenses where they overlap. Gearbox: front view down the shafts, both gears (dashed pitch circles), the pinion's rim
/// filled in its status colour. A 100 mm (or 50 mm) scale bar. Ported from the boards' schematicSpec.
/// <see cref="Build"/> works the geometry out once per version into a list of drawing ops (replayed by
/// <see cref="Paint"/> when the mesh rebuilds) and a list of labels. A design it doesn't know gets none
/// (<see cref="Has"/> false): the card then drops its right column.
/// </summary>
public sealed class CadInstrument
{
    public const float W = 156f, H = 196f;

    public enum LabelStyle { Strong, Muted, Cg, Cp, Ok, Warn, Bad }
    public enum Side { Left, Center, Right }  // the text's right edge, centre or left edge at x

    public struct Label
    {
        public string text;
        public float x, y;
        public LabelStyle style;
        public Side side;
    }

    enum Kind { Fill, Stroke, Dash, Annulus }

    struct Op
    {
        public Kind kind;
        public int start, count;
        public bool closed;
        public float width, on, off;
        public Color color;
        public float cx, cy, r0, r1;  // Annulus
    }

    readonly List<Vector2> points = new List<Vector2>(2048);
    readonly List<Op> ops = new List<Op>(64);
    public readonly List<Label> labels = new List<Label>(8);

    public bool Has { get; private set; }

    /// <summary>Work the view out for this version; false if it isn't a design this knows.</summary>
    public bool Build(CadParams p, CadAnalysis a, string status)
    {
        points.Clear();
        ops.Clear();
        labels.Clear();
        Has = false;
        if (p == null) return false;
        if (Num(p, "body_length") > 0 && Num(p, "body_diameter") > 0 && p.Find("fin_span") != null) Has = Rocket(p, a, status);
        else if (Num(p, "arm_length") > 0 && Num(p, "prop_diameter") > 0) Has = Quad(p, a);
        else if (Num(p, "module") > 0 && Num(p, "pinion_teeth") > 0 && Num(p, "gear_teeth") > 0) Has = Gearbox(p, a);
        return Has;
    }

    /// <summary>Replay the ops into the instrument's shape (its rect is 156 x 196).</summary>
    public void Paint(CadShape s)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.kind == Kind.Annulus)
            {
                s.Annulus(op.cx, op.cy, op.r0, op.r1, op.color);
                continue;
            }
            s.Begin();
            for (int k = 0; k < op.count; k++)
            {
                var q = points[op.start + k];
                if (k == 0) s.M(q.x, q.y);
                else s.L(q.x, q.y);
            }
            if (op.closed) s.Z();
            switch (op.kind)
            {
                case Kind.Fill: s.Fill(op.color); break;
                case Kind.Stroke: s.Stroke(op.width, op.color); break;
                default: s.Dashed(op.width, op.color, op.on, op.off); break;
            }
        }
    }

    // ---- Recording ----

    int mark;

    void Begin() => mark = points.Count;
    void Pt(float x, float y) => points.Add(new Vector2(x, y));

    void End(Kind kind, bool closed, float width, Color color, float on = 0f, float off = 0f)
    {
        int count = points.Count - mark;
        if (count < 2) return;
        ops.Add(new Op { kind = kind, start = mark, count = count, closed = closed, width = width, color = color, on = on, off = off });
    }

    // The same points used twice (a fill and its outline).
    void Again(Kind kind, bool closed, float width, Color color, float on = 0f, float off = 0f)
    {
        var last = ops[ops.Count - 1];
        ops.Add(new Op { kind = kind, start = last.start, count = last.count, closed = closed, width = width, color = color, on = on, off = off });
    }

    void Circle(float cx, float cy, float r, int n = 0)
    {
        if (n <= 0) n = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * r / 1.2f), 12, 128);
        Begin();
        for (int i = 0; i < n; i++)
        {
            float t = 2f * Mathf.PI * i / n;
            Pt(cx + r * Mathf.Cos(t), cy - r * Mathf.Sin(t));
        }
    }

    void Disc(float cx, float cy, float r, Color c)
    {
        Circle(cx, cy, r);
        End(Kind.Fill, true, 0f, c);
    }

    void Ring(float cx, float cy, float r, float width, Color c)
    {
        Circle(cx, cy, r);
        End(Kind.Stroke, true, width, c);
    }

    void Line(float x0, float y0, float x1, float y1, float width, Color c)
    {
        Begin();
        Pt(x0, y0);
        Pt(x1, y1);
        End(Kind.Stroke, false, width, c);
    }

    void Text(float x, float y, string text, LabelStyle style, Side side) =>
        labels.Add(new Label { text = text, x = x, y = y, style = style, side = side });

    void ScaleBar(float s, float mm)
    {
        float w = mm * s, x0 = 4f, y = H - 7f;
        Begin();
        Pt(x0, y - 3f);
        Pt(x0, y);
        Pt(x0 + w, y);
        Pt(x0 + w, y - 3f);
        End(Kind.Stroke, false, 1f, CadUi.White(0.5f));
        Text(x0 + w + 5f, y - 8f, mm.ToString("0") + " mm", LabelStyle.Muted, Side.Right);
    }

    static float Num(CadParams p, string name)
    {
        var q = p.Find(name);
        if (q == null || q.IsOptions || q.value == null) return 0f;
        try { return (float)q.Number; }
        catch (System.Exception) { return 0f; }
    }

    static LabelStyle StyleOf(string status) => status == "bad" ? LabelStyle.Bad : status == "warn" ? LabelStyle.Warn : LabelStyle.Ok;

    // ---- Rocket: side view, nose up ----

    bool Rocket(CadParams p, CadAnalysis a, string status)
    {
        float r = Num(p, "body_diameter") * 0.5f, body = Num(p, "body_length"), nose = Mathf.Max(1f, Num(p, "nose_length"));
        float L = body + nose, span = Num(p, "fin_span"), root = Num(p, "fin_root"), tip = Num(p, "fin_tip"), sweep = Num(p, "fin_sweep");
        bool cone = p.Find("nose_shape")?.value as string == "cone";
        float s = Mathf.Min((H - 24f) / L, 70f / (2f * (r + span)));
        float ax = 108f, y0 = H - 18f;
        float X(float x) => ax + x * s;
        float Y(float z) => y0 - z * s;

        // Outline: tail, up the left side, round the nose, down the right.
        var profile = new List<Vector2>();
        if (cone)
        {
            profile.Add(new Vector2(r, body));
            profile.Add(new Vector2(0f, L));
        }
        else
        {
            float rho = (r * r + nose * nose) / (2f * r);
            for (int i = 0; i <= 12; i++)
            {
                float z = nose * i / 12f;
                profile.Add(new Vector2(Mathf.Max(0f, Mathf.Sqrt(Mathf.Max(rho * rho - z * z, 0f)) + r - rho), body + z));
            }
        }
        Begin();
        Pt(X(-r), Y(0f));
        Pt(X(-r), Y(body));
        for (int i = 0; i < profile.Count; i++) Pt(X(-profile[i].x), Y(profile[i].y));
        for (int i = profile.Count - 1; i >= 0; i--) Pt(X(profile[i].x), Y(profile[i].y));
        Pt(X(r), Y(body));
        Pt(X(r), Y(0f));
        End(Kind.Fill, true, 0f, CadUi.White(0.07f));
        Again(Kind.Stroke, true, 1.2f, CadUi.White(0.7f));
        Line(X(-r), Y(body), X(r), Y(body), 1f, CadUi.White(0.35f));

        // Fins (one each side), flush with the tail.
        for (int sg = 1; sg >= -1; sg -= 2)
        {
            Begin();
            Pt(X(sg * r), Y(root));
            Pt(X(sg * r), Y(0f));
            Pt(X(sg * (r + span)), Y(root - sweep - tip));
            Pt(X(sg * (r + span)), Y(root - sweep));
            End(Kind.Fill, true, 0f, new Color(0.353f, 0.549f, 1f, 0.18f));
            Again(Kind.Stroke, true, 1.2f, CadUi.White(0.7f));
        }

        // CG and CP from the tip (the analysis's, else its metrics').
        float cg = a?.cgMm ?? Metric(a, "cg"), cp = a?.cpMm ?? Metric(a, "cp");
        if (cg <= 0f || cp <= 0f)
        {
            ScaleBar(s, 100f);
            return true;
        }
        float ycg = Y(L - cg), ycp = Y(L - cp);
        Disc(ax, ycg, 6f, CadUi.Hole);
        Wedge(ax, ycg, 6f, 0f, CadLook.CgBlue);
        Wedge(ax, ycg, 6f, Mathf.PI, CadLook.CgBlue);
        Ring(ax, ycg, 6f, 1.2f, new Color(0.81f, 0.886f, 1f, 1f));
        Disc(ax, ycp, 4.6f, CadLook.Bad);
        float lx = X(-(r + span)) - 6f, end = Mathf.Min(lx, ax - 22f);
        Line(ax - 9f, ycg, end, ycg, 1f, CadUi.Alpha(CadLook.CgBlue, 0.8f));
        Line(ax - 8f, ycp, end, ycp, 1f, CadUi.Alpha(CadLook.Bad, 0.8f));
        float bx = X(r) + 7f;
        Begin();
        Pt(bx - 3f, ycg);
        Pt(bx, ycg);
        Pt(bx, ycp);
        Pt(bx - 3f, ycp);
        End(Kind.Stroke, false, 1.4f, CadUi.Alpha(CadUi.Status(status), 0.95f));
        float tx = end - 4f;
        Text(tx, ycg - 27f, "CG", LabelStyle.Cg, Side.Left);
        Text(tx, ycg - 14f, Mathf.RoundToInt(cg) + " mm", LabelStyle.Muted, Side.Left);
        Text(tx, ycp + 2f, "CP", LabelStyle.Cp, Side.Left);
        Text(tx, ycp + 15f, Mathf.RoundToInt(cp) + " mm", LabelStyle.Muted, Side.Left);
        Text(bx + 5f, (ycg + ycp) * 0.5f - 8f, Mathf.RoundToInt(cp - cg) + " mm", LabelStyle.Muted, Side.Right);
        ScaleBar(s, 100f);
        return true;
    }

    // The CG symbol's quarter: a filled wedge from angle a0 to a0 + 90 deg (maths angles, y up).
    void Wedge(float cx, float cy, float r, float a0, Color c)
    {
        Begin();
        Pt(cx, cy);
        for (int i = 0; i <= 8; i++)
        {
            float t = a0 + Mathf.PI * 0.5f * i / 8f;
            Pt(cx + r * Mathf.Cos(t), cy - r * Mathf.Sin(t));
        }
        End(Kind.Fill, true, 0f, c);
    }

    static float Metric(CadAnalysis a, string id)
    {
        var m = a?.metrics?.Find(x => x.id == id);
        return m != null ? m.value : 0f;
    }

    // ---- Quadcopter: top view ----

    bool Quad(CadParams p, CadAnalysis a)
    {
        int n = Mathf.Max(3, Mathf.RoundToInt(Num(p, "arm_count") > 0 ? Num(p, "arm_count") : 4f));
        float A = Num(p, "arm_length"), pr = Num(p, "prop_diameter") * 25.4f * 0.5f, md = Mathf.Max(4f, Num(p, "motor_size"));
        float plate = Mathf.Max(10f, Num(p, "plate_size"));
        float ext = A + pr;
        float s = Mathf.Min((W - 10f) / (2f * ext), (H - 28f) / (2f * ext));
        float cx = W * 0.5f, cy = (H - 16f) * 0.5f;
        float X(float x) => cx + x * s;
        float Y(float y) => cy - y * s;
        var centres = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float t = (360f * i / n + 180f / n) * Mathf.Deg2Rad;
            centres[i] = new Vector2(A * Mathf.Cos(t), A * Mathf.Sin(t));
            Line(X(0f), Y(0f), X(centres[i].x), Y(centres[i].y), Mathf.Max(2f, md * 0.55f * s), CadUi.White(0.22f));
        }
        // The plate: a rounded square.
        float h = plate * 0.5f - plate * 0.18f, rr = plate * 0.18f;
        Begin();
        for (int c = 0; c < 4; c++)
        {
            float qx = c == 0 || c == 3 ? h : -h, qy = c < 2 ? h : -h;
            for (int i = 0; i <= 4; i++)
            {
                float t = (90f * c + 90f * i / 4f) * Mathf.Deg2Rad;
                Pt(X(qx + rr * Mathf.Cos(t)), Y(qy + rr * Mathf.Sin(t)));
            }
        }
        End(Kind.Fill, true, 0f, CadUi.White(0.1f));
        Again(Kind.Stroke, true, 1.1f, CadUi.White(0.6f));
        foreach (var c in centres)
        {
            Disc(X(c.x), Y(c.y), pr * s, CadUi.White(0.05f));
            Ring(X(c.x), Y(c.y), pr * s, 1.1f, CadUi.White(0.55f));
            Disc(X(c.x), Y(c.y), md * 0.5f * s, CadUi.White(0.5f));
        }
        float clearance = Metric(a, "clearance");
        bool hasClearance = a?.metrics?.Find(m => m.id == "clearance") != null;
        if (!hasClearance) clearance = 2f * A * Mathf.Sin(Mathf.PI / n) - 2f * pr;
        for (int i = 0; i < n; i++)
        {
            Vector2 c1 = centres[i], c2 = centres[(i + 1) % n];
            if (clearance < 0f) Lens(c1, c2, pr, X, Y);
            else
            {
                Vector2 d = (c2 - c1).normalized;
                Line(X(c1.x + d.x * pr), Y(c1.y + d.y * pr), X(c2.x - d.x * pr), Y(c2.y - d.y * pr), 2.2f, CadLook.Ok);
            }
        }
        Vector2 top = (centres[0] + centres[1 % n]) * 0.5f;
        Text(X(top.x), Y(top.y) - 18f, clearance < 0f ? Mathf.RoundToInt(-clearance) + " mm overlap" : Mathf.RoundToInt(clearance) + " mm",
            clearance < 0f ? LabelStyle.Bad : LabelStyle.Ok, Side.Center);
        Vector2 low = centres[2 % n];
        Text(X(low.x), Y(low.y) + pr * s + 2f, Num(p, "prop_diameter").ToString("0.0") + " in props", LabelStyle.Muted, Side.Center);
        ScaleBar(s, 100f);
        return true;
    }

    // Where two prop discs overlap: the lens between their circles, red.
    void Lens(Vector2 c1, Vector2 c2, float r, System.Func<float, float> X, System.Func<float, float> Y)
    {
        float d = Vector2.Distance(c1, c2);
        if (d < 1e-4f || d >= 2f * r) return;
        Vector2 u = (c2 - c1) / d, v = new Vector2(-u.y, u.x);
        float half = d * 0.5f, hh = Mathf.Sqrt(Mathf.Max(0f, r * r - half * half)), th = Mathf.Atan2(hh, half);
        Begin();
        for (int i = 0; i <= 12; i++)
        {
            float t = -th + 2f * th * i / 12f;
            Vector2 q = c1 + u * (r * Mathf.Cos(t)) + v * (r * Mathf.Sin(t));
            Pt(X(q.x), Y(q.y));
        }
        for (int i = 0; i <= 12; i++)
        {
            float t = Mathf.PI - th + 2f * th * i / 12f;
            Vector2 q = c2 + u * (r * Mathf.Cos(t)) + v * (r * Mathf.Sin(t));
            Pt(X(q.x), Y(q.y));
        }
        End(Kind.Fill, true, 0f, CadUi.Alpha(CadLook.Bad, 0.5f));
        Again(Kind.Stroke, true, 1.2f, CadLook.Bad);
    }

    // ---- Gearbox: front view down the shafts (the boards' gearboxDims and gearOutline) ----

    bool Gearbox(CadParams p, CadAnalysis a)
    {
        float m = Num(p, "module"), d = Mathf.Max(1f, Num(p, "shaft_diameter"));
        int z1 = Mathf.RoundToInt(Num(p, "pinion_teeth")), z2 = Mathf.RoundToInt(Num(p, "gear_teeth"));
        if (z1 < 4 || z2 < 4) return false;
        float r1 = m * z1 * 0.5f, r2 = m * z2 * 0.5f, centre = r1 + r2, x1 = -centre * 0.5f, x2 = centre * 0.5f;
        float bw = Mathf.Max(4f, 0.875f * d), gap = 3f + m, wall = bw + 1f;
        float xmin = x1 - r1 - m - gap, xmax = x2 + r2 + m + gap;
        float zc = wall + Mathf.Max(r1, r2) + m + gap;
        float X0 = xmin - wall, X1 = xmax + wall;
        float s = Mathf.Min((W - 12f) / (X1 - X0), (H - 30f) / (2f * zc));
        float cx = W * 0.5f - (X0 + X1) * 0.5f * s, y0 = (H - 22f) * 0.5f + zc * s;
        float X(float x) => cx + x * s;
        float Y(float z) => y0 - z * s;

        Begin();
        Pt(X(X0), Y(0f));
        Pt(X(X1), Y(0f));
        Pt(X(X1), Y(2f * zc));
        Pt(X(X0), Y(2f * zc));
        End(Kind.Dash, true, 1f, CadUi.White(0.3f), 3f, 4f);
        Begin();
        Pt(X(X0), Y(zc));
        Pt(X(X1), Y(zc));
        End(Kind.Dash, false, 1f, CadUi.White(0.15f), 3f, 4f);

        var rim = a?.metrics?.Find(x => x.id == "rim");
        string rimStatus = rim?.status ?? "ok";
        for (int g = 0; g < 2; g++)
        {
            float gx = g == 0 ? x1 : x2;
            int z = g == 0 ? z1 : z2;
            float phase = g == 0 ? 0f : Mathf.PI + Mathf.PI / z2;
            Color tint = g == 0 ? new Color(1f, 0.851f, 0.627f, 0.14f) : new Color(0.902f, 0.925f, 0.961f, 0.14f);
            Begin();
            GearOutline(m, z, phase, 3, gx, zc, X, Y);
            End(Kind.Fill, true, 0f, tint);
            Again(Kind.Stroke, true, 1f, CadUi.White(0.75f));
            Circle(X(gx), Y(zc), m * z * 0.5f * s);
            End(Kind.Dash, true, 0.8f, CadUi.White(0.35f), 2f, 3f);
            float bore = Mathf.Min(d, 2f * (m * z * 0.5f - 1.25f * m) - 1.6f) * 0.5f;
            if (g == 0)
            {
                // The pinion's rim: the metal between the bore and the tooth roots, in its status colour.
                float rf = m * z * 0.5f - 1.25f * m;
                Annulus(X(gx), Y(zc), Mathf.Max(0f, bore * s), Mathf.Max(0f, rf * s), CadUi.Alpha(CadUi.Status(rimStatus), 0.55f));
            }
            Disc(X(gx), Y(zc), Mathf.Max(0.5f, bore * s), CadUi.Hole);
            Ring(X(gx), Y(zc), Mathf.Max(0.5f, bore * s), 1f, CadUi.White(0.6f));
        }
        Text(X(x1), Y(zc) + r1 * s + 5f, z1 + "T", LabelStyle.Muted, Side.Center);
        Text(X(x2), Y(zc) + r2 * s + 5f, z2 + "T", LabelStyle.Muted, Side.Center);
        if (rim != null)
        {
            Text(X(x1), Y(zc) - r1 * s - 30f, "rim " + CadUi.Clean(rim.display), StyleOf(rimStatus), Side.Center);
            Line(X(x1), Y(zc) - r1 * s - 13f, X(x1), Y(zc) - (r1 - 1.25f * m) * s - 1f, 1f, CadUi.Alpha(CadUi.Status(rimStatus), 0.9f));
        }
        ScaleBar(s, X1 - X0 > 160f ? 100f : 50f);
        return true;
    }

    void Annulus(float cx, float cy, float r0, float r1, Color c)
    {
        if (r1 <= r0 + 0.05f) return;
        ops.Add(new Op { kind = Kind.Annulus, cx = cx, cy = cy, r0 = r0, r1 = r1, color = c });
    }

    // Spur gear outline (a port of gearbox.gear_outline): involute flanks, tip and root; tooth 0 at `phase`.
    void GearOutline(float m, int z, float phase, int flank, float gx, float gz, System.Func<float, float> X, System.Func<float, float> Y)
    {
        float pa = 20f * Mathf.Deg2Rad, r = m * z * 0.5f, rb = r * Mathf.Cos(pa), ra = r + m, rf = r - 1.25f * m;
        float Inv(float t) => Mathf.Tan(t) - t;
        float halfBase = Mathf.PI / (2f * z) + Inv(pa);
        float Half(float rho) => halfBase - Inv(Mathf.Acos(Mathf.Min(1f, rb / rho)));
        float start = Mathf.Max(rb, rf);
        for (int k = 0; k < z; k++)
        {
            float c = phase + 2f * Mathf.PI * k / z;
            if (rf < rb) Polar(rf, c - Half(start), gx, gz, X, Y);
            for (int i = 0; i < flank; i++)
            {
                float rho = start + (ra - start) * i / (flank - 1);
                Polar(rho, c - Half(rho), gx, gz, X, Y);
            }
            for (int i = flank - 1; i >= 0; i--)
            {
                float rho = start + (ra - start) * i / (flank - 1);
                Polar(rho, c + Half(rho), gx, gz, X, Y);
            }
            if (rf < rb) Polar(rf, c + Half(start), gx, gz, X, Y);
            Polar(rf, c + Mathf.PI / z, gx, gz, X, Y);
        }
    }

    void Polar(float rho, float t, float gx, float gz, System.Func<float, float> X, System.Func<float, float> Y) =>
        Pt(X(gx + rho * Mathf.Cos(t)), Y(gz + rho * Mathf.Sin(t)));
}
