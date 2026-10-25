using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    // uGUI mesh chart: stays in the ForgePanel canvas and supports world-space UI.
    public class MLCurve : MaskableGraphic
    {
        public MLSession session;
        public bool accuracy;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (session == null || session.EpochIndex < 1) return;
            var r = rectTransform.rect; float max = 1;
            // A metric the run didn't log is NaN: it doesn't scale the chart, and its segments are skipped.
            if (!accuracy) for (int i = 0; i <= session.EpochIndex; i++)
                foreach (float v in new[] { Value(i, 0), Value(i, 1) }) if (!float.IsNaN(v)) max = Mathf.Max(max, v);
            for (int series = 0; series < 2; series++) for (int i = 1; i <= session.EpochIndex; i++)
            {
                if (float.IsNaN(Value(i - 1, series)) || float.IsNaN(Value(i, series))) continue;
                Vector2 a = Point(i - 1, series, r, max), b = Point(i, series, r, max);
                Vector2 n = new Vector2(-(b-a).y, (b-a).x).normalized * 1.2f;
                int start = vh.currentVertCount; Color col = series == 0 ? new Color(.3f, .8f, 1) : new Color(1, .65f, .25f);
                vh.AddVert(a-n, col, Vector2.zero); vh.AddVert(a+n, col, Vector2.zero); vh.AddVert(b+n, col, Vector2.zero); vh.AddVert(b-n, col, Vector2.zero);
                vh.AddTriangle(start, start+1, start+2); vh.AddTriangle(start, start+2, start+3);
            }
        }
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
