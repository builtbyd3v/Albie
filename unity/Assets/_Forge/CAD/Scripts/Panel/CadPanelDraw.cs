using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Painters the design panel's sections share (into a <see cref="CadShape"/>): the verdict's half-circle dial, the
/// band bars of the strip and the Analysis gauges, and the flight curve of the telemetry card and the Analysis plot.
/// Pure functions of their data and the shape's size (the boards' dialSpec, barSpec and flightPlot).
/// </summary>
public static class CadPanelDraw
{
    // The dial: a 156 x 86 mm half circle, 7 mm band arcs about (78, 74), radius 56.
    public const float DialW = 156f, DialH = 86f, DialCx = 78f, DialCy = 74f, DialR = 56f;

    public static float DialAngle(CadMetric m, float v) =>
        Mathf.PI * (1f - Mathf.Clamp01((v - m.min.Value) / Mathf.Max(1e-6f, m.max.Value - m.min.Value)));

    /// <summary>Band arcs (the metric's band full, the others 50%) and a white needle across the arc.</summary>
    public static void Dial(CadShape s, CadMetric m)
    {
        if (m == null || !m.IsGauge) return;
        float max = m.max.Value, from = m.min.Value;
        if (m.bands == null || m.bands.Count == 0)
            s.Begin().Arc(DialCx, DialCy, DialR, Mathf.PI - 0.025f, 0.025f, start: true).Stroke(7f, CadUi.Status(m.status));
        else
            foreach (var b in m.bands)
            {
                float to = Mathf.Min(b.to, max);
                if (to > from + 1e-5f)
                {
                    float a0 = DialAngle(m, from) - 0.025f, a1 = DialAngle(m, to) + 0.025f;
                    s.Begin().Arc(DialCx, DialCy, DialR, a0, a1, start: true)
                        .Stroke(7f, CadUi.Alpha(CadUi.Status(b.status), b.status == m.status ? 1f : 0.5f));
                }
                from = b.to;
                if (from >= max) break;
            }
        float a = DialAngle(m, m.value);
        float c = Mathf.Cos(a), sn = Mathf.Sin(a);
        s.Line(DialCx + (DialR - 17f) * c, DialCy - (DialR - 17f) * sn, DialCx + (DialR + 6f) * c, DialCy - (DialR + 6f) * sn,
            2.6f, Color.white, roundCaps: true);
    }

    /// <summary>Where a dial's tick label goes (its centre), for value <paramref name="v"/>.</summary>
    public static Vector2 DialTick(CadMetric m, float v)
    {
        float a = DialAngle(m, v);
        return new Vector2(DialCx + (DialR + 14f) * Mathf.Cos(a), DialCy - (DialR + 14f) * Mathf.Sin(a) - 1f);
    }

    /// <summary>
    /// A horizontal band bar: the bands (2 mm apart, the metric's own at full colour, the others 42%) and a white needle
    /// ringed in graphite at the value. (x, y) is the bar's top left; the needle reaches <paramref name="needleTop"/>
    /// above it.
    /// </summary>
    public static void BandBar(CadShape s, CadMetric m, float x, float y, float w, float h, float needleW, float needleH, float needleTop, float radius)
    {
        if (m == null || !m.IsGauge) return;
        float min = m.min.Value, max = m.max.Value, span = Mathf.Max(1e-6f, max - min);
        int n = m.bands != null ? m.bands.Count : 0;
        if (n == 0) s.Begin().RoundRect(x, y, w, h, radius).Fill(CadUi.Status(m.status));
        else
        {
            float available = w - 2f * (n - 1), from = min, at = x;
            foreach (var b in m.bands)
            {
                float bw = Mathf.Max(0f, (Mathf.Min(b.to, max) - from) / span) * available;
                from = b.to;
                if (bw <= 0.05f) continue;
                s.Begin().RoundRect(at, y, bw, h, Mathf.Min(radius, bw * 0.5f))
                    .Fill(CadUi.Alpha(CadUi.Status(b.status), b.status == m.status ? 1f : 0.42f));
                at += bw + 2f;
            }
        }
        float nx = x + Mathf.Clamp01((m.value - min) / span) * w;
        s.Begin().RoundRect(nx - needleW * 0.5f - 2f, y - needleTop - 2f, needleW + 4f, needleH + 4f, needleW * 0.5f + 2f)
            .Fill(new Color(0.078f, 0.086f, 0.102f, 0.9f));
        s.Begin().RoundRect(nx - needleW * 0.5f, y - needleTop, needleW, needleH, needleW * 0.5f).Fill(Color.white);
    }

    // ---- The flight curve: altitude against time, launch to landing ----

    /// <summary>
    /// A flight's curve, sampled once per version: times and altitudes (sim seconds, m), its events, and how the
    /// telemetry's time axis squeezes the descent (×8 after <see cref="switchAt"/>, as the Fly demo plays it).
    /// </summary>
    public sealed class Flight
    {
        public readonly List<Vector2> points = new List<Vector2>(512);  // (t, alt)
        public float land, apogeeT, apogeeAlt, burnout, deploy, switchAt, top;
        public bool deploys;

        public void Set(CadFlight f)
        {
            points.Clear();
            if (f == null) return;
            f.Times(out land, out apogeeT, out burnout, out deploy, out switchAt);
            deploys = f.deployTimeS.HasValue && f.deployTimeS.Value < land;
            apogeeAlt = f.apogeeM;
            if (f.samples != null && f.samples.Count > 1)
                foreach (var q in f.samples) points.Add(new Vector2(q.t, Mathf.Max(0f, q.altM)));
            else if (f.trajectory != null)
            {
                // A flight stored before round 4: the climb only (east, up, north each 1/48 of the way to apogee).
                for (int i = 0; i < f.trajectory.Count; i++)
                    if (f.trajectory[i] != null && f.trajectory[i].Length >= 2)
                        points.Add(new Vector2(apogeeT * i / Mathf.Max(1, f.trajectory.Count - 1), f.trajectory[i][1]));
                land = apogeeT;
            }
            top = 1f;
            foreach (var q in points) top = Mathf.Max(top, q.y);
            apogeeAlt = Mathf.Max(apogeeAlt, top);
        }

        public bool Empty => points.Count < 2;

        /// <summary>The telemetry's time axis: real time to the parachute, then ×8.</summary>
        public float Squeeze(float t, float speedup) => t > switchAt ? switchAt + (t - switchAt) / speedup : t;

        public float AltitudeAt(float t)
        {
            int n = points.Count;
            if (n == 0) return 0f;
            if (t <= points[0].x) return points[0].y;
            if (t >= points[n - 1].x) return points[n - 1].y;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (points[mid].x <= t) lo = mid;
                else hi = mid;
            }
            float k = Mathf.InverseLerp(points[lo].x, points[hi].x, t);
            return Mathf.Lerp(points[lo].y, points[hi].y, k);
        }
    }

    /// <summary>The plot's x for time t (6 mm in from each side), squeezed after the parachute when <paramref name="speedup"/> > 1.</summary>
    public static float PlotX(Flight f, float t, float w, float speedup)
    {
        float end = f.Squeeze(f.land, speedup);
        return 6f + f.Squeeze(t, speedup) / Mathf.Max(end, 1e-3f) * (w - 12f);
    }

    /// <summary>The plot's y for altitude a: the apogee at 108%.</summary>
    public static float PlotY(Flight f, float a, float h) => h - 6f - a / Mathf.Max(f.apogeeAlt * 1.08f, 1f) * (h - 16f);

    /// <summary>The whole curve as a path in the shape (call Stroke / Dashed / FillUnder after).</summary>
    public static void CurvePath(CadShape s, Flight f, float w, float h, float speedup)
    {
        s.Begin();
        for (int i = 0; i < f.points.Count; i++)
        {
            var q = f.points[i];
            float x = PlotX(f, q.x, w, speedup), y = PlotY(f, q.y, h);
            if (i == 0) s.M(x, y);
            else s.L(x, y);
        }
    }
}
