using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    public sealed class VizLine : MaskableGraphic
    {
        public float[] values, secondaryValues;
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear(); Draw(mesh, values, new Color(.25f,.75f,1)); Draw(mesh, secondaryValues, new Color(1,.62f,.22f));
        }
        void Draw(VertexHelper mesh, float[] series, Color color)
        {
            if (series == null || series.Length < 2) return;
            float max = .0001f; foreach (float value in series) max = Mathf.Max(max, value); Rect r = rectTransform.rect;
            for (int i = 1; i < series.Length; i++)
            {
                Vector2 a = new Vector2(r.xMin + r.width * (i - 1) / (series.Length - 1), r.yMin + r.height * series[i-1] / max);
                Vector2 b = new Vector2(r.xMin + r.width * i / (series.Length - 1), r.yMin + r.height * series[i] / max);
                Vector2 n = new Vector2(-(b-a).y, (b-a).x).normalized * 2; int v = mesh.currentVertCount;
                mesh.AddVert(a-n,color,Vector2.zero); mesh.AddVert(a+n,color,Vector2.zero); mesh.AddVert(b+n,color,Vector2.zero); mesh.AddVert(b-n,color,Vector2.zero);
                mesh.AddTriangle(v,v+1,v+2); mesh.AddTriangle(v,v+2,v+3);
            }
        }
    }
}
