using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>A half-ring dial (0 at the left, 1 at the right) with rounded ends: a faint track and the value arc.</summary>
    public sealed class MLGauge : MaskableGraphic
    {
        [Range(0, 1)] public float value;
        public float thickness = 10f;
        public Color track = new Color(1, 1, 1, 0.08f);
        const int Segments = 64;

        public void Set(float v, Color fill) { value = Mathf.Clamp01(v); color = fill; SetVerticesDirty(); }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            var centre = new Vector2(r.center.x, r.yMin + thickness * 0.5f);
            float radius = Mathf.Min(r.width * 0.5f, r.height) - thickness * 0.5f;
            Arc(vh, centre, radius, 0f, 1f, track);
            if (value > 0.001f) Arc(vh, centre, radius, 0f, value, color);
        }

        void Arc(VertexHelper vh, Vector2 centre, float radius, float from, float to, Color c)
        {
            float inner = radius - thickness * 0.5f, outer = radius + thickness * 0.5f;
            int steps = Mathf.Max(2, Mathf.CeilToInt(Segments * (to - from)));
            int start = vh.currentVertCount;
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.PI * (1f - Mathf.Lerp(from, to, i / (float)steps));
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(centre + d * inner, c, Vector2.zero);
                vh.AddVert(centre + d * outer, c, Vector2.zero);
                if (i > 0) { int k = start + i * 2; vh.AddTriangle(k - 2, k - 1, k + 1); vh.AddTriangle(k - 2, k + 1, k); }
            }
            Cap(vh, centre, radius, from, c); Cap(vh, centre, radius, to, c);
        }

        // A round end on the arc at t (a small disc on the centre line).
        void Cap(VertexHelper vh, Vector2 centre, float radius, float t, Color c)
        {
            float a = Mathf.PI * (1f - t);
            var p = centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            int start = vh.currentVertCount; const int sides = 16;
            vh.AddVert(p, c, Vector2.zero);
            for (int i = 0; i <= sides; i++)
            {
                float b = i / (float)sides * Mathf.PI * 2f;
                vh.AddVert(p + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * thickness * 0.5f, c, Vector2.zero);
                if (i > 0) vh.AddTriangle(start, start + i, start + i + 1);
            }
        }
    }
}
