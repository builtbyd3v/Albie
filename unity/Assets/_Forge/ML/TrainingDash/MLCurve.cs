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
            if (!accuracy) for (int i = 0; i <= session.EpochIndex; i++) max = Mathf.Max(max, session.Data.metrics[i].train_loss, session.Data.metrics[i].val_loss);
            for (int series = 0; series < 2; series++)
            {
                Color col = series == 0 ? MLUI.Train : MLUI.Val;
                Color top = new Color(col.r, col.g, col.b, 0.16f), bottom = new Color(col.r, col.g, col.b, 0f);
                for (int i = 1; i <= session.EpochIndex; i++)
                {
                    Vector2 a = Point(i - 1, series, r, max), b = Point(i, series, r, max);
                    int start = vh.currentVertCount;
                    vh.AddVert(new Vector3(a.x, r.yMin), bottom, Vector2.zero); vh.AddVert(a, top, Vector2.zero);
                    vh.AddVert(b, top, Vector2.zero); vh.AddVert(new Vector3(b.x, r.yMin), bottom, Vector2.zero);
                    vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
                }
                for (int i = 1; i <= session.EpochIndex; i++)
                {
                    Vector2 a = Point(i - 1, series, r, max), b = Point(i, series, r, max);
                    Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized * (LineWidth * 0.5f);
                    int start = vh.currentVertCount;
                    vh.AddVert(a - n, col, Vector2.zero); vh.AddVert(a + n, col, Vector2.zero); vh.AddVert(b + n, col, Vector2.zero); vh.AddVert(b - n, col, Vector2.zero);
                    vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
                }
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
        Vector2 Point(int i, int series, Rect r, float max)
        {
            var m = session.Data.metrics[i]; float value = accuracy ? (series == 0 ? m.train_acc : m.val_acc) : (series == 0 ? m.train_loss : m.val_loss);
            return new Vector2(r.xMin + r.width * i / Mathf.Max(1, session.TotalEpochs - 1), r.yMin + r.height * Mathf.Clamp01(value / max));
        }
    }
}
