using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>A reusable ForgePanel content renderer for lightweight ML visualizations.</summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class VizCard : MonoBehaviour
    {
        public enum Kind { Line, Bar, Heatmap, Scatter, Table, Image, Text }
        public Kind kind = Kind.Line;
        public string title = "Validation trend";
        public string[] labels = { "A", "B", "C", "D", "E" };
        public float[] values = { .15f, .42f, .3f, .7f, .82f };
        public float[] secondaryValues = { .25f, .3f, .5f, .55f, .68f };
        [TextArea] public string body = "Reusable visualization card";
        public Texture image;
        RectTransform root;

        void Start() { root = GetComponent<ForgePanel>().Content; Render(); }
        public void Render()
        {
            if (root == null) root = GetComponent<ForgePanel>().Content;
            MLUI.Clear(root);
            MLUI.Text(root, title + " · " + kind, 8, 4, 610, 27, 16);
            MLUI.Button(root, "Prev", 626, 4, 58, 28, () => Cycle(-1));
            MLUI.Button(root, "Next", 688, 4, 58, 28, () => Cycle(1));
            switch (kind)
            {
                case Kind.Line: RenderLine(); break;
                case Kind.Bar: RenderBar(); break;
                case Kind.Heatmap: RenderHeatmap(); break;
                case Kind.Scatter: RenderScatter(); break;
                case Kind.Table: RenderTable(); break;
                case Kind.Image: RenderImage(); break;
                case Kind.Text: RenderText(); break;
            }
        }
        void RenderLine()
        {
            var chart = MLUI.Rect("Line chart", root, 16, 42, 700, 260).gameObject.AddComponent<VizLine>();
            chart.values = values; chart.secondaryValues = secondaryValues;
            AddLabels(16, 308, 700);
        }
        void RenderBar()
        {
            int count = Mathf.Max(1, values == null ? 0 : values.Length); float max = Max(values); float width = 680f / count;
            for (int i = 0; i < count; i++)
            {
                float value = values != null && i < values.Length ? values[i] : 0;
                float h = 220 * value / max;
                MLUI.Fill(root, 22 + i * width, 280 - h, Mathf.Max(4, width - 8), h, new Color(.25f, .7f, 1));
            }
            AddLabels(16, 308, 700);
        }
        void RenderHeatmap()
        {
            int count = Mathf.Max(1, values == null ? 0 : values.Length); int columns = Mathf.CeilToInt(Mathf.Sqrt(count)); float cell = Mathf.Min(58, 680f / columns); float max = Max(values);
            for (int i = 0; i < count; i++)
            {
                float value = values != null && i < values.Length ? values[i] : 0; int x = i % columns, y = i / columns;
                MLUI.Fill(root, 16 + x * cell, 42 + y * cell, cell - 3, cell - 3, Color.Lerp(new Color(.08f,.14f,.24f), new Color(.15f,.8f,.45f), value / max));
                MLUI.Text(root, value.ToString("0.##"), 20 + x * cell, 57 + y * cell, cell - 8, 20, 11);
            }
        }
        void RenderScatter()
        {
            int count = Mathf.Max(1, values == null ? 0 : values.Length); float maxY = Max(values); float maxX = Max(secondaryValues);
            MLUI.Fill(root, 16, 300, 700, 2, new Color(.4f,.5f,.6f)); MLUI.Fill(root, 16, 42, 2, 260, new Color(.4f,.5f,.6f));
            for (int i = 0; i < count; i++)
            {
                float x = secondaryValues != null && i < secondaryValues.Length ? secondaryValues[i] / maxX : i / (float)Mathf.Max(1,count - 1);
                float y = values != null && i < values.Length ? values[i] / maxY : 0;
                MLUI.Fill(root, 16 + x * 690 - 4, 300 - y * 250 - 4, 9, 9, new Color(1,.62f,.22f));
            }
        }
        void RenderTable()
        {
            int count = Mathf.Max(labels == null ? 0 : labels.Length, values == null ? 0 : values.Length);
            for (int i = 0; i < count; i++)
            {
                float y = 40 + i * 29; MLUI.Fill(root, 16, y, 650, 26, i % 2 == 0 ? new Color(.11f,.16f,.24f) : new Color(.08f,.12f,.19f));
                MLUI.Text(root, labels != null && i < labels.Length ? labels[i] : $"Row {i+1}", 24, y + 4, 440, 20, 13);
                MLUI.Text(root, values != null && i < values.Length ? values[i].ToString("0.###") : "—", 520, y + 4, 120, 20, 13);
            }
        }
        void RenderImage()
        {
            var raw = MLUI.Rect("Image", root, 16, 40, 360, 260).gameObject.AddComponent<RawImage>();
            if (image != null) raw.texture = image;
            else { var session = MLSession.Resolve(null); raw.texture = session.Atlas; raw.uvRect = session.UV(0); }
            raw.raycastTarget = false;
        }
        void RenderText() { MLUI.Text(root, body, 16, 45, 690, 245, 16); }
        void AddLabels(float x, float y, float width)
        {
            if (labels == null || labels.Length == 0) return;
            float step = width / labels.Length;
            for (int i = 0; i < labels.Length; i++) MLUI.Text(root, labels[i], x + i * step, y, step, 24, 11);
        }
        static float Max(float[] source)
        {
            float max = .0001f; if (source != null) foreach (float value in source) max = Mathf.Max(max, value); return max;
        }
        public void Cycle(int direction)
        {
            int count = System.Enum.GetValues(typeof(Kind)).Length;
            kind = (Kind)(((int)kind + direction + count) % count);
            Render();
        }
    }
}
