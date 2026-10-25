using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// Many small atlas images as one UI mesh (the results wall's dots), drawn by Forge/ML/DotUI. Living in the
    /// canvas, popups and rings layer over it like any UI. The mesh is rebuilt only when <see cref="Set"/> is called.
    /// </summary>
    public sealed class MLDotField : MaskableGraphic
    {
        Texture atlas;
        Vector2[] centers;   // top-left mm in this rect
        Vector4[] uvs;
        Vector4[] colors;
        int count;
        float size;
        static Material fallback;

        public override Texture mainTexture => atlas != null ? atlas : s_WhiteTexture;
        public override Material defaultMaterial => fallback != null ? fallback : fallback = new Material(Shader.Find("Forge/ML/DotUI"));

        protected override void OnEnable()
        {
            base.OnEnable();
            if (canvas != null) canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
        }

        public void Set(Texture atlas, int count, Vector2[] centers, Vector4[] uvs, Vector4[] colors, float size)
        {
            this.atlas = atlas; this.count = count; this.centers = centers; this.uvs = uvs; this.colors = colors; this.size = size;
            SetMaterialDirty(); SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (count == 0 || centers == null) return;
            Rect r = rectTransform.rect; float h = size * 0.5f;
            for (int i = 0; i < count; i++)
            {
                float x = r.xMin + centers[i].x, y = r.yMax - centers[i].y;
                Vector4 uv = uvs[i]; Color32 c = (Color)colors[i];
                int start = vh.currentVertCount;
                vh.AddVert(new Vector3(x - h, y - h), c, new Vector4(uv.x, uv.y), new Vector4(0, 0), Vector3.back, Vector4.zero);
                vh.AddVert(new Vector3(x - h, y + h), c, new Vector4(uv.x, uv.y + uv.w), new Vector4(0, 1), Vector3.back, Vector4.zero);
                vh.AddVert(new Vector3(x + h, y + h), c, new Vector4(uv.x + uv.z, uv.y + uv.w), new Vector4(1, 1), Vector3.back, Vector4.zero);
                vh.AddVert(new Vector3(x + h, y - h), c, new Vector4(uv.x + uv.z, uv.y), new Vector4(1, 0), Vector3.back, Vector4.zero);
                vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
            }
        }
    }
}
