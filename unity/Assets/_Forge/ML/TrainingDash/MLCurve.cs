using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    // uGUI mesh chart: stays in the ForgePanel canvas and supports world-space UI. Faint gridlines, then per series
    // (train, val) a soft fill down to the axis, the line and a marker on the latest epoch.
    public class MLCurve : MaskableGraphic
    {
        public MLSession session;
        public bool accuracy;
        [Tooltip("Epoch index to mark with a dashed line (the best epoch), -1 for none.")] public int marker = -1;
        const float LineWidth = 1.6f, Marker = 3.2f;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            for (int g = 0; g <= 4; g++)
            {
                float y = r.yMin + r.height * g / 4f;
                var line = new Color(1, 1, 1, g == 0 ? 0.14f : 0.05f);
                Quad(vh, new Vector2(r.xMin, y - 0.3f), new Vector2(r.xMax, y + 0.3f), line, line);
            }
            if (session == null || session.EpochIndex < 1) return;
            float max = 1;
            // A metric the run didn't log is NaN: it doesn't scale the chart, and its segments are skipped.
            if (!accuracy) for (int i = 0; i <= session.EpochIndex; i++)
                for (int series = 0; series < 2; series++) { float v = Value(i, series); if (!float.IsNaN(v)) max = Mathf.Max(max, v); }
            if (marker >= 0 && marker <= session.EpochIndex)
            {
                float x = Point(marker, 1, r, max).x;
                var dash = new Color(1, 1, 1, 0.45f);
                for (float y = r.yMin; y < r.yMax; y += 6f) Quad(vh, new Vector2(x - 0.5f, y), new Vector2(x + 0.5f, Mathf.Min(y + 3f, r.yMax)), dash, dash);
            }
            for (int series = 0; series < 2; series++)
            {
                Color col = series == 0 ? MLUI.Train : MLUI.Val;
                Color top = new Color(col.r, col.g, col.b, 0.16f), bottom = new Color(col.r, col.g, col.b, 0f);
                for (int i = 1; i <= session.EpochIndex; i++)
                {
                    if (Missing(i, series)) continue;
                    Vector2 a = Point(i - 1, series, r, max), b = Point(i, series, r, max);
                    int start = vh.currentVertCount;
                    vh.AddVert(new Vector3(a.x, r.yMin), bottom, Vector2.zero); vh.AddVert(a, top, Vector2.zero);
                    vh.AddVert(b, top, Vector2.zero); vh.AddVert(new Vector3(b.x, r.yMin), bottom, Vector2.zero);
                    vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
                }
                for (int i = 1; i <= session.EpochIndex; i++)
                {
                    if (Missing(i, series)) continue;
                    Vector2 a = Point(i - 1, series, r, max), b = Point(i, series, r, max);
                    Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized * (LineWidth * 0.5f);
                    int start = vh.currentVertCount;
                    vh.AddVert(a - n, col, Vector2.zero); vh.AddVert(a + n, col, Vector2.zero); vh.AddVert(b + n, col, Vector2.zero); vh.AddVert(b - n, col, Vector2.zero);
                    vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
                }
                if (float.IsNaN(Value(session.EpochIndex, series))) continue;
                Vector2 last = Point(session.EpochIndex, series, r, max);
                Quad(vh, last - Vector2.one * Marker, last + Vector2.one * Marker, col, col);
            }
        }
        static void Quad(VertexHelper vh, Vector2 min, Vector2 max, Color bottom, Color top)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(min.x, min.y), bottom, Vector2.zero); vh.AddVert(new Vector3(min.x, max.y), top, Vector2.zero);
            vh.AddVert(new Vector3(max.x, max.y), top, Vector2.zero); vh.AddVert(new Vector3(max.x, min.y), bottom, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
        }
        bool Missing(int i, int series) => float.IsNaN(Value(i - 1, series)) || float.IsNaN(Value(i, series));
        float Value(int i, int series)
        {
            var m = session.Data.metrics[i];
            if (m == null) return float.NaN;  // an epoch whose tick never arrived
            return accuracy ? (series == 0 ? m.train_acc : m.val_acc) : (series == 0 ? m.train_loss : m.val_loss);
        }
        Vector2 Point(int i, int series, Rect r, float max)
        {
            float value = Value(i, series);
            return new Vector2(r.xMin + r.width * i / Mathf.Max(1, session.TotalEpochs - 1), r.yMin + r.height * Mathf.Clamp01(value / max));
        }
    }
}
