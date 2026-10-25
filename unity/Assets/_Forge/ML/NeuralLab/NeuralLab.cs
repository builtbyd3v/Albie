using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// Neural Net Lab panel (ml-neural-lab): TensorFlow Playground in the ML desk. Pick a dataset, the inputs, the
    /// layers and the activation, press Play and watch the decision boundary bend while every neuron shows what it has
    /// learned. Tap a neuron (ray, poke or mouse) to switch it off: the boundary and the test accuracy change at once,
    /// and Albie can say why (nn_status). The network itself is <see cref="NeuralLabSim"/>; this panel only draws it.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class NeuralLab : MonoBehaviour
    {
        const int BoundaryRes = 128, NeuronRes = 24;
        const float HeaderY = 0, ControlsY = 50, Controls2Y = 82, GraphY = 122, DrawInterval = 0.1f;
        static readonly Color Blue = new Color32(0x4C, 0x9B, 0xFF, 0xFF), Orange = new Color32(0xFF, 0x9A, 0x3C, 0xFF);
        static readonly Color32 Mid = new Color32(0x1A, 0x1D, 0x24, 0xFF);

        NeuralLabSim sim;
        RectTransform root, graph;
        float width, height, boundarySize, graphWidth;
        TMP_Text epoch, testAcc, trainLoss, testLoss, rate, noise, layersLabel, outputLabel, playLabel, stopNote;
        ForgePill play;
        readonly Dictionary<NNDataset, ForgePill> datasetPills = new Dictionary<NNDataset, ForgePill>();
        readonly Dictionary<NNActivation, ForgePill> activationPills = new Dictionary<NNActivation, ForgePill>();
        Texture2D boundary, loss;
        Color32[] boundaryPixels, lossPixels;
        RawImage boundaryImage;

        class Node { public int layer, index; public RawImage image; public Texture2D texture; public Color32[] pixels; public GameObject off; public Vector2 left, right; }
        class Link { public Image line; public int layer, to, from; }
        readonly List<Node> nodes = new List<Node>();
        readonly List<Link> links = new List<Link>();
        float nextDraw, nodeSize = 30, nodePitch = 37; bool dirty = true, built;

        void Start()
        {
            sim = NeuralLabSim.Instance;
            root = GetComponent<ForgePanel>().Content; width = root.rect.width; height = root.rect.height;
            boundarySize = Mathf.Min(height - GraphY - 64, width * 0.4f);
            graphWidth = width - boundarySize - 24;
            BuildChrome();
            sim.Rebuilt += OnRebuilt; sim.Stepped += OnStepped;
            BuildGraph(); built = true;
            Draw();
        }
        void OnDestroy()
        {
            if (sim != null) { sim.Rebuilt -= OnRebuilt; sim.Stepped -= OnStepped; }
            foreach (var n in nodes) if (n.texture != null) Destroy(n.texture);
            if (boundary != null) Destroy(boundary);
            if (loss != null) Destroy(loss);
        }
        void OnRebuilt() { if (built) BuildGraph(); dirty = true; }
        void OnStepped() => dirty = true;

        void Update()
        {
            if (!dirty || Time.unscaledTime < nextDraw) return;
            Draw();
        }

        // ---- Layout -------------------------------------------------------------------------------------

        void BuildChrome()
        {
            epoch = MLUI.Stat(root, "Epoch", "0", 2, HeaderY, 90, MLUI.TextColor);
            testAcc = MLUI.Stat(root, "Test accuracy", "—", 96, HeaderY, 130, MLUI.Good);
            trainLoss = MLUI.Stat(root, "Train loss", "—", 232, HeaderY, 90, MLUI.Train);
            testLoss = MLUI.Stat(root, "Test loss", "—", 326, HeaderY, 90, MLUI.Val);
            foreach (var t in new[] { epoch, testAcc, trainLoss, testLoss }) t.fontSize = 21;
            play = MLUI.Pill(root, "Play", width - 254, 2, 92, 30, () => sim.SetPlaying(!sim.Playing)); play.On = true;
            playLabel = play.GetComponentInChildren<TMP_Text>();
            stopNote = MLUI.Text(root, "", 424, 6, width - 254 - 432, 34, 10.5f); stopNote.color = MLUI.Good;
            stopNote.alignment = TextAlignmentOptions.Right;
            MLUI.Button(root, "Step", width - 156, 2, 70, 30, sim.StepOnce);
            MLUI.Button(root, "Reset", width - 80, 2, 80, 30, sim.ResetWeights);

            MLUI.Caps(root, "Data", 2, ControlsY + 7, 50);
            float x = 50;
            foreach (NNDataset d in System.Enum.GetValues(typeof(NNDataset)))
            {
                var dataset = d; string name = d == NNDataset.Xor ? "XOR" : d.ToString();
                datasetPills[d] = MLUI.Pill(root, name, x, ControlsY, 72, 26, () => sim.SetDataset(dataset)); x += 78;
            }
            x += 14; MLUI.Caps(root, "Activation", x, ControlsY + 7, 90); x += 92;
            foreach (NNActivation a in new[] { NNActivation.Tanh, NNActivation.ReLU, NNActivation.Sigmoid, NNActivation.Linear })
            {
                var act = a;
                activationPills[a] = MLUI.Pill(root, a == NNActivation.ReLU ? "ReLU" : a.ToString(), x, ControlsY, 72, 26, () => sim.SetActivation(act)); x += 78;
            }

            MLUI.Caps(root, "Learning rate", 2, Controls2Y + 7, 110);
            MLUI.Button(root, "−", 112, Controls2Y, 28, 26, () => sim.ShiftLearningRate(-1));
            rate = MLUI.Text(root, "", 144, Controls2Y + 4, 56, 20, 13); rate.alignment = TextAlignmentOptions.Center;
            MLUI.Button(root, "+", 202, Controls2Y, 28, 26, () => sim.ShiftLearningRate(1));
            MLUI.Caps(root, "Noise", 252, Controls2Y + 7, 50);
            MLUI.Button(root, "−", 302, Controls2Y, 28, 26, () => sim.SetNoise(sim.Noise - 0.1f));
            noise = MLUI.Text(root, "", 334, Controls2Y + 4, 44, 20, 13); noise.alignment = TextAlignmentOptions.Center;
            MLUI.Button(root, "+", 380, Controls2Y, 28, 26, () => sim.SetNoise(sim.Noise + 0.1f));
            MLUI.Caps(root, "Hidden layers", 430, Controls2Y + 7, 110);
            MLUI.Button(root, "−", 540, Controls2Y, 28, 26, sim.RemoveLayer);
            layersLabel = MLUI.Text(root, "", 572, Controls2Y + 4, 30, 20, 13); layersLabel.alignment = TextAlignmentOptions.Center;
            MLUI.Button(root, "+", 604, Controls2Y, 28, 26, sim.AddLayer);
            MLUI.Button(root, "◀ ML project", width - 124, Controls2Y, 124, 26, () => MLWorkspaceBridge.ShowLab(false));

            // Output: the decision boundary over the data, and the test loss over time under it.
            float bx = width - boundarySize;
            MLUI.Caps(root, "Output", bx, GraphY, 100);
            outputLabel = MLUI.Text(root, "", bx + 70, GraphY - 2, boundarySize - 70, 16, 10); outputLabel.color = MLUI.Sub;
            outputLabel.alignment = TextAlignmentOptions.TopRight;
            boundary = NewTexture(BoundaryRes, BoundaryRes); boundaryPixels = new Color32[BoundaryRes * BoundaryRes];
            boundaryImage = MLUI.Rect("Decision boundary", root, bx, GraphY + 20, boundarySize, boundarySize).gameObject.AddComponent<RawImage>();
            boundaryImage.texture = boundary; boundaryImage.raycastTarget = false;
            loss = NewTexture(160, 36); lossPixels = new Color32[160 * 36];
            var lossImage = MLUI.Rect("Loss history", root, bx, GraphY + 26 + boundarySize, boundarySize, 34).gameObject.AddComponent<RawImage>();
            lossImage.texture = loss; lossImage.raycastTarget = false;
            var key = MLUI.Text(root, "<color=#FF9A3C>●</color> −1    <color=#4C9BFF>●</color> +1    test loss over epochs", bx, GraphY + 62 + boundarySize, boundarySize, 14, 9.5f);
            key.color = MLUI.Faint;

            graph = MLUI.Rect("Network", root, 0, GraphY, graphWidth, height - GraphY);
        }

        void BuildGraph()
        {
            foreach (var n in nodes) if (n.texture != null) Destroy(n.texture);
            nodes.Clear(); links.Clear(); MLUI.Clear(graph);
            int columns = sim.Hidden.Count + 1, rows = NNData.AllFeatures.Length;
            foreach (int size in sim.Hidden) rows = Mathf.Max(rows, size);
            // Nodes as big as the column allows (readable in the headset), the rows sharing the graph's height.
            float graphHeight = height - GraphY;
            nodePitch = Mathf.Min(56, (graphHeight - 22 - 52) / rows); nodeSize = Mathf.Round(nodePitch * 0.8f);
            float pitch = (graphWidth - nodeSize) / Mathf.Max(1, columns);
            var hint = MLUI.Text(graph, "Tap a neuron to switch it off  ·  tap an input to use it", 0, graphHeight - 18, graphWidth, 16, 10);
            hint.color = MLUI.Faint;

            // Inputs: every feature, the ones in use lit; tap to add or remove one.
            MLUI.Caps(graph, "Inputs", 0, 0, 80);
            var inputs = new List<Node>();
            for (int f = 0, row = 0; f < NNData.AllFeatures.Length; f++, row++)
            {
                var feature = (NNFeature)f; bool on = sim.Features[f];
                var node = NewNode(0, f, 0, 22 + row * nodePitch, () => sim.ToggleFeature(feature));
                PaintFeature(node, feature);
                node.image.color = on ? Color.white : new Color(1, 1, 1, 0.22f);
                var label = MLUI.Text(graph, NNData.Label(feature), nodeSize + 5, 22 + row * nodePitch + nodeSize * 0.5f - 8, 46, 16, 11);
                label.color = on ? MLUI.Sub : MLUI.Faint;
                if (on) inputs.Add(node);
            }

            // Hidden layers: one column each, its size and − / + over it.
            var previous = inputs;
            for (int l = 1; l <= sim.Hidden.Count; l++)
            {
                float x = l * pitch, size = sim.Hidden[l - 1];
                int layer = l;
                MLUI.Caps(graph, $"Layer {l}", x - 6, 0, 70);
                MLUI.Button(graph, "−", x - 4, graphHeight - 48, 24, 22, () => sim.ResizeLayer(layer, -1));
                MLUI.Button(graph, "+", x + 24, graphHeight - 48, 24, 22, () => sim.ResizeLayer(layer, 1));
                var column = new List<Node>();
                for (int i = 0; i < size; i++)
                {
                    int index = i + 1;
                    var node = NewNode(l, i, x, 22 + i * nodePitch, () => sim.ToggleNeuron(layer, index));
                    var number = MLUI.Text(node.image.transform, index.ToString(), nodeSize - 11, nodeSize - 13, 12, 12, 9);
                    number.color = new Color(1, 1, 1, 0.8f);
                    node.off = MLUI.Text(node.image.transform, "×", 0, 0, nodeSize, nodeSize, nodeSize * 0.8f).gameObject;
                    node.off.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;
                    node.off.GetComponent<TMP_Text>().color = MLUI.Bad;
                    column.Add(node);
                    for (int k = 0; k < previous.Count; k++) AddLink(previous[k].right, node.left, l, i, k);
                }
                previous = column;
            }
            // Last layer → the output square's left edge.
            var outputPoint = new Vector2(width - boundarySize, 20 + boundarySize * 0.5f);  // graph sits at x 0, y GraphY
            for (int k = 0; k < previous.Count; k++) AddLink(previous[k].right, outputPoint, sim.Hidden.Count + 1, 0, k);
            foreach (var n in nodes) n.image.transform.SetAsLastSibling();  // nodes over the lines
            dirty = true;
        }

        Node NewNode(int layer, int index, float x, float y, UnityEngine.Events.UnityAction click)
        {
            var image = MLUI.Rect($"Neuron L{layer}N{index + 1}", graph, x, y, nodeSize, nodeSize).gameObject.AddComponent<RawImage>();
            var node = new Node { layer = layer, index = index, image = image, texture = NewTexture(NeuronRes, NeuronRes), pixels = new Color32[NeuronRes * NeuronRes] };
            image.texture = node.texture;
            var button = image.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.onClick.AddListener(click);
            node.left = new Vector2(x, y + nodeSize * 0.5f); node.right = new Vector2(x + nodeSize, y + nodeSize * 0.5f);
            nodes.Add(node);
            return node;
        }

        void AddLink(Vector2 from, Vector2 to, int layer, int toIndex, int fromInput)
        {
            var line = MLUI.Line(graph, from, to, 1, MLUI.Faint);
            links.Add(new Link { line = line, layer = layer, to = toIndex, from = fromInput });
        }

        static Texture2D NewTexture(int w, int h) =>
            new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

        // ---- Drawing ------------------------------------------------------------------------------------

        void Draw()
        {
            dirty = false; nextDraw = Time.unscaledTime + DrawInterval;
            epoch.text = sim.Epoch.ToString("000");
            testAcc.text = sim.TestAccuracy.ToString("0.0%");
            trainLoss.text = sim.TrainLoss.ToString("0.000");
            testLoss.text = sim.TestLoss.ToString("0.000");
            rate.text = sim.LearningRate.ToString("0.###");
            noise.text = Mathf.RoundToInt(sim.Noise * 100) + "%";
            layersLabel.text = sim.Hidden.Count.ToString();
            playLabel.text = sim.Playing ? "Pause" : "Play";
            foreach (var p in datasetPills) p.Value.On = p.Key == sim.Dataset;
            foreach (var p in activationPills) p.Value.On = p.Key == sim.Activation;
            outputLabel.text = $"train {sim.TrainAccuracy:0%}  ·  test {sim.TestAccuracy:0%}";
            if (sim.Playing) stopNote.text = "";
            else if (sim.AutoPausedAt >= 0)
            {
                stopNote.color = MLUI.Good;
                stopNote.text = $"Auto-paused at {NeuralLabSim.AutoPauseAccuracy:0%}+\n<color=#FFFFFF80>epoch {sim.AutoPausedAt} · Play to keep going</color>";
            }
            else if (sim.StalledAt >= 0)
            {
                stopNote.color = MLUI.Warn;
                stopNote.text = $"Stuck at {sim.TestAccuracy:0%}\n<color=#FFFFFF80>epoch {sim.StalledAt} · ask Albie to fix it</color>";
            }
            else stopNote.text = "";
            DrawBoundary(); DrawNeurons(); DrawLinks(); DrawLoss();
        }

        static Color32 Shade(float v, byte alpha = 255)
        {
            v = Mathf.Clamp(v, -1, 1);
            Color c = v >= 0 ? Color.Lerp(Mid, Blue, v) : Color.Lerp(Mid, Orange, -v);
            Color32 o = c; o.a = alpha; return o;
        }

        void DrawBoundary()
        {
            float step = 2 * NNData.Extent / (BoundaryRes - 1);
            for (int py = 0; py < BoundaryRes; py++)
                for (int px = 0; px < BoundaryRes; px++)
                {
                    float x = -NNData.Extent + px * step, y = -NNData.Extent + py * step;
                    boundaryPixels[py * BoundaryRes + px] = Shade(sim.Predict(x, y) * 0.75f);
                }
            var train = sim.Train;
            for (int i = 0; i < train.Count; i++) Stamp(train[i]);
            boundary.SetPixels32(boundaryPixels); boundary.Apply(false);
        }

        void Stamp(NNPoint p)
        {
            int cx = Mathf.RoundToInt((p.x + NNData.Extent) / (2 * NNData.Extent) * (BoundaryRes - 1));
            int cy = Mathf.RoundToInt((p.y + NNData.Extent) / (2 * NNData.Extent) * (BoundaryRes - 1));
            Color32 fill = p.label > 0 ? new Color32(0x7F, 0xB8, 0xFF, 0xFF) : new Color32(0xFF, 0xB8, 0x70, 0xFF), rim = new Color32(0x0B, 0x0D, 0x12, 0xFF);
            for (int dy = -1; dy <= 2; dy++)
                for (int dx = -1; dx <= 2; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= BoundaryRes || y >= BoundaryRes) continue;
                    bool edge = dx == -1 || dy == -1 || dx == 2 || dy == 2, corner = (dx == -1 || dx == 2) && (dy == -1 || dy == 2);
                    if (corner) continue;
                    boundaryPixels[y * BoundaryRes + x] = edge ? Color32.Lerp(boundaryPixels[y * BoundaryRes + x], rim, 0.6f) : fill;
                }
        }

        // Every hidden neuron's output over the plane, from one forward pass per grid point.
        void DrawNeurons()
        {
            var net = sim.Net;
            float step = 2 * NNData.Extent / (NeuronRes - 1);
            float scale = sim.Activation == NNActivation.ReLU || sim.Activation == NNActivation.Linear ? 0.25f : 1f;
            bool sigmoid = sim.Activation == NNActivation.Sigmoid;
            for (int py = 0; py < NeuronRes; py++)
                for (int px = 0; px < NeuronRes; px++)
                {
                    sim.Predict(-NNData.Extent + px * step, -NNData.Extent + py * step);
                    foreach (var n in nodes)
                    {
                        if (n.layer == 0) continue;
                        float v = net.NeuronOutput(n.layer, n.index);
                        if (sigmoid) v = v * 2 - 1;
                        n.pixels[py * NeuronRes + px] = Shade(v * scale);
                    }
                }
            foreach (var n in nodes)
            {
                if (n.layer == 0) continue;
                n.texture.SetPixels32(n.pixels); n.texture.Apply(false);
                bool off = net.disabled[n.layer][n.index];
                n.image.color = off ? new Color(1, 1, 1, 0.25f) : Color.white;
                n.off.SetActive(off);
            }
        }

        void PaintFeature(Node node, NNFeature feature)
        {
            float step = 2 * NNData.Extent / (NeuronRes - 1), max = 1e-4f;
            for (int i = 0; i < node.pixels.Length; i++)
                max = Mathf.Max(max, Mathf.Abs(NNData.Feature(feature, -NNData.Extent + i % NeuronRes * step, -NNData.Extent + i / NeuronRes * step)));
            for (int i = 0; i < node.pixels.Length; i++)
                node.pixels[i] = Shade(NNData.Feature(feature, -NNData.Extent + i % NeuronRes * step, -NNData.Extent + i / NeuronRes * step) / max);
            node.texture.SetPixels32(node.pixels); node.texture.Apply(false);
        }

        // Blue for positive weights, orange for negative; thicker = larger. A switched-off neuron's links fade out.
        void DrawLinks()
        {
            var net = sim.Net; int last = net.Layers - 1;
            foreach (var link in links)
            {
                float w = net.weights[link.layer][link.to][link.from];
                bool off = (link.layer < last && net.disabled[link.layer][link.to]) || (link.layer > 1 && net.disabled[link.layer - 1][link.from]);
                Color c = w >= 0 ? Blue : Orange;
                c.a = off ? 0.06f : Mathf.Clamp(0.15f + Mathf.Abs(w) * 0.45f, 0.15f, 0.95f);
                link.line.color = c;
                var size = link.line.rectTransform.sizeDelta; size.y = off ? 0.6f : Mathf.Clamp(0.6f + Mathf.Abs(w) * 1.6f, 0.6f, 4.5f);
                link.line.rectTransform.sizeDelta = size;
            }
        }

        void DrawLoss()
        {
            const int w = 160, h = 36;
            var clear = new Color32(255, 255, 255, 10);
            for (int i = 0; i < lossPixels.Length; i++) lossPixels[i] = clear;
            var history = sim.LossHistory;
            if (history.Count > 1)
            {
                float max = 0.05f; for (int i = 0; i < history.Count; i++) max = Mathf.Max(max, history[i]);
                Color32 line = (Color32)MLUI.Val;
                int prevY = -1;
                for (int x = 0; x < w; x++)
                {
                    int i = Mathf.Min(history.Count - 1, Mathf.RoundToInt(x / (float)(w - 1) * (history.Count - 1)));
                    int y = Mathf.Clamp(Mathf.RoundToInt(history[i] / max * (h - 3)) + 1, 0, h - 1);
                    int from = prevY < 0 ? y : Mathf.Min(prevY, y), to = prevY < 0 ? y : Mathf.Max(prevY, y);
                    for (int yy = from; yy <= to; yy++) lossPixels[yy * w + x] = line;
                    prevY = y;
                }
            }
            loss.SetPixels32(lossPixels); loss.Apply(false);
        }
    }
}
