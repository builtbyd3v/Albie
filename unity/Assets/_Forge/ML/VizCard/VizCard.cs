using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>Renders a VizCard contract payload and can page through module-owned mock cards.</summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class VizCard : MonoBehaviour
    {
        public enum Kind { Line, Bar, Heatmap, Scatter, Table, Image, Text }
        [Serializable] public sealed class Series { public string name; public float[] y; }
        [Serializable] public sealed class Point { public float x, y; public string id; public int color_idx; }

        public Kind kind = Kind.Line;
        public string title = "Validation trend", subtitle, body = "Reusable visualization card";
        public string[] labels = { "A", "B", "C", "D", "E" }, xLabels, yLabels, columns;
        public float[] values = { .15f, .42f, .3f, .7f, .82f }, secondaryValues = { .25f, .3f, .5f, .55f, .68f };
        public float[][] cells;
        public string[][] rows;
        public Point[] points;
        public string xLabel, yLabel, pngUrl, alt;
        public Texture image;
        public event Action<string> SampleSelected;
        RectTransform root;
        JArray mockCards;
        int mockIndex;

        void Start()
        {
            root = GetComponent<ForgePanel>().Content;
            var fixtures = Resources.Load<TextAsset>("MLVizCardMocks");
            if (fixtures != null)
            {
                mockCards = JArray.Parse(fixtures.text);
                if (mockCards.Count > 0) LoadJson(mockCards[0].ToString(Formatting.None));
                Render();
            }
            else Render();
        }

        /// <summary>Reads the section 7.6 VizCard JSON shape.</summary>
        public void LoadJson(string json)
        {
            var card = JObject.Parse(json);
            title = Text(card["title"], "Visualization");
            subtitle = Text(card["subtitle"], null);
            body = Text(card["text"], "");
            xLabel = Text(card["x_label"], null); yLabel = Text(card["y_label"], null);
            pngUrl = Text(card["png_url"], null); alt = Text(card["alt"], null);
            var kindName = Text(card["kind"], "text");
            if (!Enum.TryParse(kindName, true, out kind)) kind = Kind.Text;
            labels = Strings(card["x"] ?? card["x_labels"]);
            xLabels = Strings(card["x_labels"]); yLabels = Strings(card["y_labels"]);
            columns = Strings(card["columns"]);
            cells = Floats2D(card["cells"]);
            rows = Strings2D(card["rows"]);
            points = card["points"] == null ? null : card["points"].ToObject<Point[]>();
            var series = card["series"] as JArray;
            if (series != null && series.Count > 0)
            {
                values = Floats(series[0]["y"]);
                secondaryValues = series.Count > 1 ? Floats(series[1]["y"]) : null;
                subtitle = string.Join(" / ", Names(series));
            }
            if (cells != null && cells.Length > 0) values = Flatten(cells);
        }

        static string Text(JToken token, string fallback) => token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        static string[] Strings(JToken token) => token == null ? null : token.ToObject<string[]>();
        static float[] Floats(JToken token) => token == null ? null : token.ToObject<float[]>();
        static float[][] Floats2D(JToken token) => token == null ? null : token.ToObject<float[][]>();
        static string[][] Strings2D(JToken token)
        {
            if (!(token is JArray array)) return null;
            var result = new string[array.Count][];
            for (int r = 0; r < array.Count; r++)
            {
                var row = array[r] as JArray; result[r] = new string[row == null ? 0 : row.Count];
                if (row != null) for (int c = 0; c < row.Count; c++) result[r][c] = row[c]?.ToString() ?? "";
            }
            return result;
        }
        static string[] Names(JArray series)
        {
            var result = new List<string>();
            foreach (var item in series) result.Add(Text(item["name"], "series"));
            return result.ToArray();
        }
        static float[] Flatten(float[][] source)
        {
            var result = new List<float>(); foreach (var row in source) if (row != null) result.AddRange(row); return result.ToArray();
        }

        public void Render()
        {
            if (root == null) root = GetComponent<ForgePanel>().Content;
            MLUI.Clear(root);
            MLUI.Text(root, title + " · " + kind, 8, 4, 510, 27, 16);
            MLUI.Button(root, "Prev", 626, 4, 58, 28, () => Cycle(-1));
            MLUI.Button(root, "Next", 688, 4, 58, 28, () => Cycle(1));
            if (!string.IsNullOrEmpty(subtitle)) MLUI.Text(root, subtitle, 16, 30, 700, 20, 11);
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
            var chart = MLUI.Rect("Line chart", root, 16, 52, 700, 248).gameObject.AddComponent<VizLine>();
            chart.values = values; chart.secondaryValues = secondaryValues;
            AddLabels(labels ?? xLabels, 16, 304, 700);
        }
        void RenderBar()
        {
            int count = Mathf.Max(1, values == null ? 0 : values.Length); float max = Max(values); float width = 680f / count;
            for (int i = 0; i < count; i++)
            {
                float value = values != null && i < values.Length ? values[i] : 0;
                float h = 220 * value / max;
                MLUI.Fill(root, 22 + i * width, 278 - h, Mathf.Max(4, width - 8), h, new Color(.25f, .7f, 1));
            }
            AddLabels(labels, 16, 304, 700);
        }
        void RenderHeatmap()
        {
            if (cells == null || cells.Length == 0) { DrawFlatHeatmap(); return; }
            int rowsCount = cells.Length, colsCount = 0; foreach (var row in cells) colsCount = Mathf.Max(colsCount, row == null ? 0 : row.Length);
            float cellW = Mathf.Min(74, 660f / Mathf.Max(1, colsCount)), cellH = Mathf.Min(36, 230f / Mathf.Max(1, rowsCount)); float max = Max(Flatten(cells));
            for (int r = 0; r < rowsCount; r++)
            {
                if (yLabels != null && r < yLabels.Length) MLUI.Text(root, yLabels[r], 8, 56 + r * cellH, 84, cellH, 10);
                for (int c = 0; c < (cells[r] == null ? 0 : cells[r].Length); c++)
                {
                    float value = cells[r][c]; float x = 96 + c * cellW, y = 54 + r * cellH;
                    MLUI.Fill(root, x, y, cellW - 2, cellH - 2, Color.Lerp(new Color(.08f,.14f,.24f), new Color(.15f,.8f,.45f), value / max));
                    MLUI.Text(root, value.ToString("0.##"), x + 2, y + 3, cellW - 6, cellH - 4, 10);
                }
            }
            if (xLabels != null) AddLabels(xLabels, 96, 54 + rowsCount * cellH, cellW * colsCount);
        }
        void DrawFlatHeatmap()
        {
            int count = Mathf.Max(1, values == null ? 0 : values.Length); int columnsCount = Mathf.CeilToInt(Mathf.Sqrt(count)); float cell = Mathf.Min(58, 680f / columnsCount); float max = Max(values);
            for (int i = 0; i < count; i++)
            {
                float value = values != null && i < values.Length ? values[i] : 0; int x = i % columnsCount, y = i / columnsCount;
                MLUI.Fill(root, 16 + x * cell, 52 + y * cell, cell - 3, cell - 3, Color.Lerp(new Color(.08f,.14f,.24f), new Color(.15f,.8f,.45f), value / max));
                MLUI.Text(root, value.ToString("0.##"), 20 + x * cell, 67 + y * cell, cell - 8, 20, 11);
            }
        }
        void RenderScatter()
        {
            MLUI.Fill(root, 36, 292, 660, 2, new Color(.4f,.5f,.6f)); MLUI.Fill(root, 36, 52, 2, 242, new Color(.4f,.5f,.6f));
            MLUI.Text(root, xLabel ?? "x", 610, 300, 90, 20, 11); MLUI.Text(root, yLabel ?? "y", 8, 48, 100, 20, 11);
            if (points != null && points.Length > 0)
            {
                float minX = points[0].x, maxX = points[0].x, minY = points[0].y, maxY = points[0].y;
                foreach (var p in points) { minX = Mathf.Min(minX,p.x); maxX = Mathf.Max(maxX,p.x); minY = Mathf.Min(minY,p.y); maxY = Mathf.Max(maxY,p.y); }
                for (int i = 0; i < points.Length; i++)
                {
                    var point = points[i]; float x = Mathf.InverseLerp(minX,maxX,point.x), y = Mathf.InverseLerp(minY,maxY,point.y);
                    var dot = MLUI.Rect("Point " + (point.id ?? i.ToString()), root, 36 + x * 652 - 5, 292 - y * 236 - 5, 11, 11);
                    var imageGraphic = dot.gameObject.AddComponent<Image>(); imageGraphic.color = Palette(point.color_idx);
                    if (!string.IsNullOrEmpty(point.id))
                    {
                        var button = dot.gameObject.AddComponent<Button>(); button.targetGraphic = imageGraphic;
                        string id = point.id; button.onClick.AddListener(() => SampleSelected?.Invoke(id));
                    }
                }
                return;
            }
            int count = Mathf.Max(1, values == null ? 0 : values.Length); float maxYValue = Max(values); float maxXValue = Max(secondaryValues);
            for (int i = 0; i < count; i++)
            {
                float x = secondaryValues != null && i < secondaryValues.Length ? secondaryValues[i] / maxXValue : i / (float)Mathf.Max(1,count - 1);
                float y = values != null && i < values.Length ? values[i] / maxYValue : 0;
                MLUI.Fill(root, 36 + x * 652 - 4, 292 - y * 236 - 4, 9, 9, new Color(1,.62f,.22f));
            }
        }
        static Color Palette(int index)
        {
            Color[] colors = { new Color(.25f,.75f,1), new Color(1,.62f,.22f), new Color(.36f,.9f,.55f), new Color(.92f,.38f,.56f), new Color(.72f,.56f,1) };
            return colors[Mathf.Abs(index) % colors.Length];
        }
        void RenderTable()
        {
            int count = rows != null ? rows.Length : Mathf.Max(labels == null ? 0 : labels.Length, values == null ? 0 : values.Length);
            float y = 50;
            if (columns != null)
            {
                MLUI.Fill(root, 16, y, 690, 27, new Color(.18f,.28f,.4f));
                for (int c = 0; c < columns.Length; c++) MLUI.Text(root, columns[c], 22 + c * (660f/columns.Length), y + 4, 650f/columns.Length, 20, 12);
                y += 31;
            }
            int visible = Mathf.Min(count, 8);
            for (int i = 0; i < visible; i++)
            {
                float rowY = y + i * 29; MLUI.Fill(root, 16, rowY, 690, 26, i % 2 == 0 ? new Color(.11f,.16f,.24f) : new Color(.08f,.12f,.19f));
                if (rows != null && rows[i] != null)
                {
                    for (int c = 0; c < rows[i].Length; c++) MLUI.Text(root, rows[i][c], 22 + c * (660f/Mathf.Max(1,rows[i].Length)), rowY + 4, 650f/Mathf.Max(1,rows[i].Length), 20, 12);
                }
                else
                {
                    MLUI.Text(root, labels != null && i < labels.Length ? labels[i] : $"Row {i+1}", 24, rowY + 4, 440, 20, 13);
                    MLUI.Text(root, values != null && i < values.Length ? values[i].ToString("0.###") : "—", 520, rowY + 4, 120, 20, 13);
                }
            }
        }
        void RenderImage()
        {
            var raw = MLUI.Rect("Image", root, 16, 52, 360, 248).gameObject.AddComponent<RawImage>();
            if (image != null) raw.texture = image;
            else { var session = MLSession.Resolve(null); raw.texture = session.Atlas; raw.uvRect = session.UV(0); }
            raw.raycastTarget = false;
            if (!string.IsNullOrEmpty(alt ?? pngUrl)) MLUI.Text(root, alt ?? pngUrl, 386, 54, 320, 60, 12);
        }
        void RenderText() { MLUI.Text(root, body, 16, 52, 690, 245, 16); }
        void AddLabels(string[] source, float x, float y, float width)
        {
            if (source == null || source.Length == 0) return;
            float step = width / source.Length;
            for (int i = 0; i < source.Length; i++) MLUI.Text(root, source[i], x + i * step, y, step, 22, 10);
        }
        static float Max(float[] source)
        {
            float max = .0001f; if (source != null) foreach (float value in source) max = Mathf.Max(max, value); return max;
        }
        public void Cycle(int direction)
        {
            if (mockCards != null && mockCards.Count > 0)
            {
                mockIndex = (mockIndex + direction + mockCards.Count) % mockCards.Count;
                LoadJson(mockCards[mockIndex].ToString(Formatting.None)); Render(); return;
            }
            int count = Enum.GetValues(typeof(Kind)).Length;
            kind = (Kind)(((int)kind + direction + count) % count); Render();
        }
    }
}
