using System.Text;
using TMPro;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// "How it learns" (ml-neural-learn), left of the Neural Net Lab: one training point through the network in four
    /// steps, with its real numbers: forward (inputs → each layer's outputs), compare (the guess vs the answer, the
    /// error), backprop (which last-layer neurons the error blames, and which way) and update (one weight before and
    /// after, learning rate × blame). It's the point the 3D net is pulsing (<see cref="NeuralLabSim.PulseIndex"/>);
    /// the step being shown lights up in time with the pulse. Numbers are taken when the point arrives.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class NeuralLearn : MonoBehaviour
    {
        const float Top = 34, Footer = 46, Gap = 8;
        static readonly string[] Titles = { "Forward: the point goes in", "Compare: guess vs answer", "Backprop: who gets the blame", "Update: nudge the weights" };
        const string BlueHex = "#7FB8FF", OrangeHex = "#FFB870", BadHex = "#FF5D6C", GoodHex = "#3DDC97";

        NeuralLabSim sim;
        RectTransform root;
        TMP_Text header, footer;
        readonly ForgeSurface[] cards = new ForgeSurface[4];
        readonly TMP_Text[] titles = new TMP_Text[4], bodies = new TMP_Text[4];
        readonly ForgeSurface[] dots = new ForgeSurface[4];
        readonly StringBuilder sb = new StringBuilder(256);
        int shownPulse = -1, lit = -1; bool stale = true;

        void Start()
        {
            sim = NeuralLabSim.Instance;
            root = GetComponent<ForgePanel>().Content;
            float w = root.rect.width, h = root.rect.height;
            MLUI.Caps(root, "One training point, step by step", 0, 4, w * 0.6f);
            header = MLUI.Text(root, "", w * 0.5f, 0, w * 0.5f, 18, 11); header.color = MLUI.Sub; header.alignment = TextAlignmentOptions.TopRight;
            float cardH = (h - Top - Footer - Gap * 3) / 4;
            for (int i = 0; i < 4; i++)
            {
                float y = Top + i * (cardH + Gap);
                cards[i] = MLUI.Card(root, 0, y, w, cardH, 12);
                dots[i] = MLUI.Dot(root, 12, y + 12, 22, MLUI.CardLit);
                var number = MLUI.Text(dots[i].transform, (i + 1).ToString(), 0, 1, 22, 20, 12); number.alignment = TextAlignmentOptions.Center;
                titles[i] = MLUI.Text(root, Titles[i], 44, y + 11, w - 56, 20, 15); titles[i].fontStyle = FontStyles.Bold;
                bodies[i] = MLUI.Text(root, "", 44, y + 34, w - 56, cardH - 40, 12.5f); bodies[i].color = MLUI.Sub;
                bodies[i].richText = true; bodies[i].overflowMode = TextOverflowModes.Ellipsis;
            }
            footer = MLUI.Text(root, "", 0, h - Footer + 8, w, Footer - 8, 11.5f); footer.color = MLUI.Faint;
            sim.Rebuilt += OnRebuilt;
        }
        void OnDestroy() { if (sim != null) sim.Rebuilt -= OnRebuilt; }
        void OnRebuilt() => stale = true;

        void Update()
        {
            if (sim == null || sim.Net == null || sim.Train.Count == 0) return;
            if (stale || sim.PulseCount != shownPulse) { stale = false; shownPulse = sim.PulseCount; Snapshot(); }
            int step = Mathf.Clamp((int)((Time.unscaledTime - sim.PulseStart) / NeuralLabSim.PulseSeconds * 4f), 0, 3);
            if (step == lit) return;
            lit = step;
            for (int i = 0; i < 4; i++)
            {
                bool on = i == step;
                cards[i].color = on ? MLUI.CardLit : MLUI.CardColor;
                dots[i].color = on ? MLUI.Accent : MLUI.CardLit;
                titles[i].color = on ? MLUI.TextColor : MLUI.Sub;
                bodies[i].color = on ? MLUI.TextColor : MLUI.Sub;
            }
        }

        // Every number for this point, worked out once when it arrives.
        void Snapshot()
        {
            var net = sim.Net; int last = net.Layers - 1, prev = last - 1;
            var p = sim.Train[sim.PulseIndex % sim.Train.Count];
            float o = sim.Predict(p.x, p.y), y = p.label;
            header.text = $"point {sim.PulseIndex % sim.Train.Count + 1} of {sim.Train.Count}  ·  epoch {sim.Epoch}";

            // 1. Forward
            sb.Clear();
            sb.Append("Point (").Append(p.x.ToString("0.0")).Append(", ").Append(p.y.ToString("0.0")).Append("), really ")
              .Append(Swatch(y)).Append(".\n<color=#FFFFFF80>In:</color> ");
            int j = 0;
            for (int f = 0; f < sim.Features.Length; f++)
            {
                if (!sim.Features[f]) continue;
                if (j > 0) sb.Append("  ");
                sb.Append(NNData.Label((NNFeature)f)).Append(' ').Append(net.NeuronOutput(0, j).ToString("0.00")); j++;
            }
            for (int l = 1; l < last && l <= 3; l++)
            {
                sb.Append("\n<color=#FFFFFF80>Layer ").Append(l).Append(":</color> ");
                for (int i = 0; i < net.sizes[l]; i++)
                {
                    if (i > 0) sb.Append("  ");
                    if (net.disabled[l][i]) sb.Append("<color=#FFFFFF60>off</color>");
                    else sb.Append(net.NeuronOutput(l, i).ToString("+0.00;−0.00"));
                }
            }
            if (last > 4) sb.Append("\n<color=#FFFFFF80>… ").Append(last - 4).Append(" more layer(s)</color>");
            bodies[0].text = sb.ToString();

            // 2. Compare
            bool right = (o >= 0 ? 1 : -1) == (int)y;
            float error = 0.5f * (o - y) * (o - y);
            sb.Clear();
            sb.Append("Output ").Append(o.ToString("+0.00;−0.00")).Append(" → it guesses ").Append(Swatch(o >= 0 ? 1 : -1))
              .Append(". The answer is ").Append(Swatch(y)).Append(".\n");
            if (right) sb.Append("<color=").Append(GoodHex).Append(">Right.</color> Error ½(output − answer)² = ").Append(error.ToString("0.000"))
                         .Append(": it still learns a little, pushing the output closer to ").Append(y > 0 ? "+1." : "−1.");
            else sb.Append("<color=").Append(BadHex).Append(">Wrong.</color> Error ½(output − answer)² = ").Append(error.ToString("0.000"))
                   .Append(": a big error, so this point moves the weights a lot.");
            bodies[1].text = sb.ToString();

            // 3. Backprop: blame at the output, then the two last-layer neurons whose weights it moves most.
            float delta = (o - y) * (1f - o * o);
            int a = -1, b = -1;
            for (int i = 0; i < net.sizes[prev]; i++)
            {
                if (prev > 0 && net.disabled[prev][i]) continue;
                float g = Mathf.Abs(delta * net.NeuronOutput(prev, i));
                if (a < 0 || g > Mathf.Abs(delta * net.NeuronOutput(prev, a))) { b = a; a = i; }
                else if (b < 0 || g > Mathf.Abs(delta * net.NeuronOutput(prev, b))) b = i;
            }
            sb.Clear();
            sb.Append("Blame at the output: (output − answer) × slope = ").Append(delta.ToString("+0.000;−0.000")).Append(".\n");
            if (a >= 0) Blame(net, prev, a, delta, o);
            if (b >= 0) { sb.Append('\n'); Blame(net, prev, b, delta, o); }
            if (prev > 0) sb.Append("\nThe chain rule passes it on back to every earlier weight.");
            bodies[2].text = sb.ToString();

            // 4. Update: the most-blamed weight, before → after, then the scale of it all.
            sb.Clear();
            if (a >= 0)
            {
                float w = net.weights[last][0][a], step = -sim.LearningRate * delta * net.NeuronOutput(prev, a);
                sb.Append(Name(prev, a)).Append(" → output weight: ").Append(w.ToString("+0.000;−0.000")).Append(" → ")
                  .Append((w + step).ToString("+0.000;−0.000")).Append("  (learning rate ").Append(sim.LearningRate.ToString("0.####"))
                  .Append(" × its blame)\n");
            }
            int weights = 0;
            for (int l = 1; l <= last; l++) weights += net.sizes[l] * (net.sizes[l - 1] + 1);
            sb.Append("All ").Append(weights).Append(" weights and biases move like this, averaged over a batch of ")
              .Append(NeuralLabSim.BatchSize).Append(" points.");
            bodies[3].text = sb.ToString();

            footer.text = $"This happens for all {sim.Train.Count} training points every epoch ({sim.Train.Count / NeuralLabSim.BatchSize} " +
                          $"batches). {(sim.Playing ? "Training now" : "Paused: press Play on the Lab to train")}  ·  test accuracy {sim.TestAccuracy:0%}";
        }

        void Blame(NNNetwork net, int layer, int i, float delta, float o)
        {
            float h = net.NeuronOutput(layer, i), w = net.weights[net.Layers - 1][0][i];
            float push = w * h;  // what this neuron added to the output
            float change = -delta * h;
            sb.Append(Name(layer, i)).Append(" (").Append(h.ToString("+0.00;−0.00")).Append(" × weight ").Append(w.ToString("+0.00;−0.00"))
              .Append(") pushed the output ").Append(push >= 0 ? "up" : "down").Append(" → its weight goes ")
              .Append(Mathf.Abs(change) < 1e-4f ? "almost nowhere" : change > 0 ? "up" : "down").Append('.');
        }

        string Name(int layer, int index)
        {
            if (layer > 0) return NeuralLabSim.NeuronId(layer, index + 1);
            for (int f = 0, j = 0; f < sim.Features.Length; f++)
                if (sim.Features[f] && j++ == index) return "Input " + NNData.Label((NNFeature)f);
            return "Input";
        }
        static string Swatch(float label) => label > 0 ? $"<color={BlueHex}>blue (+1)</color>" : $"<color={OrangeHex}>orange (−1)</color>";
    }
}
