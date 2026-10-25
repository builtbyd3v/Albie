using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// "Training story" (ml-neural-story), above the Neural Net Lab: train and test accuracy over every epoch of the
    /// session (across rebuilds, so a fix shows as a drop and a climb), with numbered markers where something happened
    /// (stuck, auto-paused, Albie's changes, a neuron switched off) and the list of those events. Beside it the Why card:
    /// the one explanation that fits what's on screen now (<see cref="NNExplain"/>).
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class NeuralStory : MonoBehaviour
    {
        const int W = 256, H = 112, MaxMarkers = 8, ListLines = 4;
        const float Floor = 0.4f, DrawInterval = 0.25f;  // the chart's accuracy axis: 40% to 100%
        static readonly Color32 Clear = new Color32(255, 255, 255, 8), Grid = new Color32(255, 255, 255, 26);

        NeuralLabSim sim;
        RectTransform root;
        Texture2D chart; Color32[] pixels;
        RectTransform chartRect;
        TMP_Text events, whyTitle, whyBody, now;
        readonly List<TMP_Text> markers = new List<TMP_Text>();
        readonly StringBuilder sb = new StringBuilder(256);
        float chartW, chartH, nextDraw; bool dirty = true;
        int lastEvents = -1, lastEventAt = -1;

        void Start()
        {
            sim = NeuralLabSim.Instance;
            root = GetComponent<ForgePanel>().Content;
            float w = root.rect.width, h = root.rect.height;
            float left = w * 0.6f;
            MLUI.Caps(root, "Accuracy over the whole session", 0, 2, left * 0.7f);
            now = MLUI.Text(root, "", left * 0.5f, 0, left * 0.5f, 16, 10.5f); now.color = MLUI.Sub; now.alignment = TextAlignmentOptions.TopRight;
            chartW = left; chartH = Mathf.Min(h * 0.5f, chartW * H / W);
            MLUI.Well(root, 0, 34, chartW, chartH, 10);
            chartRect = MLUI.Rect("Chart", root, 0, 34, chartW, chartH);
            chart = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            pixels = new Color32[W * H];
            var image = chartRect.gameObject.AddComponent<RawImage>(); image.texture = chart; image.raycastTarget = false;
            var axis = MLUI.Text(root, "100%", -2, 30, 40, 12, 8.5f); axis.color = MLUI.Faint;
            var pause = MLUI.Text(root, "92% auto-pause", 4, 34 + chartH * (1 - (0.92f - Floor) / (1 - Floor)) - 13, 120, 12, 8.5f); pause.color = MLUI.Faint;
            for (int i = 0; i < MaxMarkers; i++)
            {
                var m = MLUI.Text(chartRect, "", 0, 0, 16, 14, 9.5f); m.alignment = TextAlignmentOptions.Center; m.fontStyle = FontStyles.Bold;
                m.gameObject.SetActive(false); markers.Add(m);
            }
            var key = MLUI.Text(root, "<color=#5BC8FF>━</color> train   <color=#FF9F5A>━</color> test   <color=#5B9BFF>│</color> Albie   <color=#FFFFFF60>│</color> you / the Lab",
                                0, 38 + chartH, chartW, 14, 9.5f);
            key.color = MLUI.Faint; key.richText = true;
            events = MLUI.Text(root, "", 0, 56 + chartH, chartW, h - 56 - chartH, 11.5f); events.color = MLUI.Sub; events.richText = true;
            events.overflowMode = TextOverflowModes.Ellipsis;

            float x = left + 18, cw = w - x;
            MLUI.Card(root, x, 0, cw, h, 14);
            MLUI.Caps(root, "Why", x + 14, 12, 80);
            whyTitle = MLUI.Text(root, "", x + 14, 30, cw - 28, 44, 16); whyTitle.fontStyle = FontStyles.Bold;
            whyBody = MLUI.Text(root, "", x + 14, 76, cw - 28, h - 88, 12.5f); whyBody.color = MLUI.Sub;
            whyBody.overflowMode = TextOverflowModes.Ellipsis;
            sim.Stepped += OnStepped; sim.Rebuilt += OnStepped;
        }
        void OnDestroy()
        {
            if (sim != null) { sim.Stepped -= OnStepped; sim.Rebuilt -= OnStepped; }
            if (chart != null) Destroy(chart);
        }
        void OnStepped() => dirty = true;

        void Update()
        {
            if (sim == null || sim.Net == null || !dirty || Time.unscaledTime < nextDraw) return;
            dirty = false; nextDraw = Time.unscaledTime + DrawInterval;
            int first = sim.Story.Count > 0 ? (int)sim.Story[0].x : 0, end = Mathf.Max(sim.TotalEpochs, first + 30);
            DrawChart(first, end);
            PlaceMarkers(first, end);
            ListEvents();
            NNExplain.For(sim, out string title, out string body);
            whyTitle.text = title; whyBody.text = body;
            now.text = $"{(sim.Playing ? "training" : "paused")}  ·  train {sim.TrainAccuracy:0%}  ·  test {sim.TestAccuracy:0%}";
        }

        int X(float epoch, int first, int end) => Mathf.Clamp(Mathf.RoundToInt((epoch - first) / (end - first) * (W - 1)), 0, W - 1);
        static int Y(float accuracy) => Mathf.Clamp(Mathf.RoundToInt((accuracy - Floor) / (1 - Floor) * (H - 3)) + 1, 0, H - 1);

        void DrawChart(int first, int end)
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Clear;
            int y92 = Y(NeuralLabSim.AutoPauseAccuracy);
            for (int x = 0; x < W; x += 4) { pixels[y92 * W + x] = Grid; pixels[y92 * W + x + 1] = Grid; }  // dashed 92% line
            foreach (var e in sim.Events)
            {
                if (e.at < first) continue;
                int x = X(e.at, first, end);
                Color32 c = e.albie ? new Color32(0x5B, 0x9B, 0xFF, 140) : new Color32(255, 255, 255, 60);
                for (int y = 0; y < H; y++) pixels[y * W + x] = c;
            }
            Line(first, end, 1, MLUI.Train);
            Line(first, end, 2, MLUI.Val);
            chart.SetPixels32(pixels); chart.Apply(false);
        }

        void Line(int first, int end, int which, Color color)
        {
            var story = sim.Story; Color32 c = color;
            int px = -1, py = -1;
            for (int i = 0; i < story.Count; i++)
            {
                int x = X(story[i].x, first, end), y = Y(which == 1 ? story[i].y : story[i].z);
                if (px < 0) pixels[y * W + x] = c;
                else if (x == px) Span(x, py, y, c);  // a drop at one epoch (a rebuild) is a vertical line
                else
                    for (int xx = px + 1, from = py; xx <= x; xx++)
                    {
                        int to = Mathf.RoundToInt(Mathf.Lerp(py, y, (xx - px) / (float)(x - px)));
                        Span(xx, from, to, c); from = to;
                    }
                px = x; py = y;
            }
        }

        void Span(int x, int a, int b, Color32 c) { for (int k = Mathf.Min(a, b); k <= Mathf.Max(a, b); k++) pixels[k * W + x] = c; }

        // Numbered tags at the top of the chart over the last few events (the list below says what they were).
        void PlaceMarkers(int first, int end)
        {
            var list = sim.Events; int start = Mathf.Max(0, list.Count - Mathf.Min(MaxMarkers, ListLines));
            int m = 0;
            for (int i = start; i < list.Count && m < MaxMarkers; i++)
            {
                if (list[i].at < first) continue;
                var tag = markers[m++];
                tag.gameObject.SetActive(true);
                tag.text = (i - start + 1).ToString();
                tag.color = list[i].albie ? MLUI.Accent : MLUI.Sub;
                float x = X(list[i].at, first, end) / (float)(W - 1) * chartW;
                tag.rectTransform.anchoredPosition = new Vector2(Mathf.Clamp(x - 8, 0, chartW - 16), -2);
            }
            for (; m < MaxMarkers; m++) markers[m].gameObject.SetActive(false);
        }

        void ListEvents()
        {
            var list = sim.Events;
            int newest = list.Count > 0 ? list[list.Count - 1].at : -1;
            if (list.Count == lastEvents && newest == lastEventAt) return;
            lastEvents = list.Count; lastEventAt = newest;
            if (list.Count == 0) { events.text = "Nothing yet: press Play on the Lab. Every change, stop and fix lands here."; return; }
            sb.Clear();
            int start = Mathf.Max(0, list.Count - ListLines);
            for (int i = start; i < list.Count; i++)
            {
                var e = list[i];
                if (i > start) sb.Append('\n');
                sb.Append("<b>").Append(i - start + 1).Append("</b>  <color=#FFFFFF60>epoch ").Append(e.at).Append("</color>  ");
                if (e.albie) sb.Append("<color=#5B9BFF>Albie:</color> ");
                sb.Append(e.text);
            }
            events.text = sb.ToString();
        }
    }
}
