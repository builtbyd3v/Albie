using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// The whole dataset as a barcode under the deck: one tick per image in its class color, removed images dim.
    /// One UI mesh, rebuilt only when <see cref="Bind"/> or SetVerticesDirty is called (samples changed).
    /// </summary>
    public sealed class MLScrubBar : MaskableGraphic
    {
        MLSession session;

        public void Bind(MLSession session) { this.session = session; SetVerticesDirty(); }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            Quad(vh, r.xMin, r.yMin, r.xMax, r.yMax, new Color(1, 1, 1, 0.05f));
            var samples = session != null && session.Data != null ? session.Data.samples : null;
            if (samples == null || samples.Length == 0) return;
            float slot = r.width / samples.Length, tick = slot >= 0.8f ? slot * 0.7f : slot;
            for (int i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                Color c = s.removed ? new Color(1, 1, 1, 0.08f) : MLUI.ClassColor(s.label_idx);
                if (!s.removed) c.a = 0.75f;
                float x = r.xMin + i * slot + (slot - tick) * 0.5f;
                Quad(vh, x, r.yMin, x + tick, r.yMax, c);
            }
        }

        static void Quad(VertexHelper vh, float x0, float y0, float x1, float y1, Color32 c)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(x0, y0), c, Vector2.zero); vh.AddVert(new Vector3(x0, y1), c, Vector2.zero);
            vh.AddVert(new Vector3(x1, y1), c, Vector2.zero); vh.AddVert(new Vector3(x1, y0), c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
