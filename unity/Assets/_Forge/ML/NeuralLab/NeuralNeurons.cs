using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// "What each neuron sees" (ml-neural-neurons), right of the Neural Net Lab: every hidden neuron as a card with
    /// its picture (its output over the whole plane), what it looks at in plain words and how much it matters: the
    /// test accuracy if it were switched off. Tap a card to switch that neuron off or on, like touching it in 3D.
    /// More neurons than fit: the ones that matter most. Importance is measured once a second.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class NeuralNeurons : MonoBehaviour
    {
        const int Res = 24;
        const float Intro = 62, MinCardH = 54, MaxCardH = 84, Gap = 6, PictureInterval = 0.2f, MeasureInterval = 1f;
        static readonly Color Blue = new Color32(0x4C, 0x9B, 0xFF, 0xFF), Orange = new Color32(0xFF, 0x9A, 0x3C, 0xFF);
        static readonly Color Mid = new Color32(0x1A, 0x1D, 0x24, 0xFF);

        class Card { public int layer, index; public ForgeSurface back; public RawImage picture; public Texture2D texture; public Color32[] pixels; public TMP_Text name, detects, matters; }

        NeuralLabSim sim;
        RectTransform root, grid;
        TMP_Text note;
        readonly List<Card> cards = new List<Card>();
        readonly List<(int layer, int index, float drop)> ranked = new List<(int, int, float)>();
        float nextPicture, nextMeasure; bool rebuild = true, dirty = true;
        int capacity;

        void Start()
        {
            sim = NeuralLabSim.Instance;
            root = GetComponent<ForgePanel>().Content;
            float w = root.rect.width, h = root.rect.height;
            var intro = MLUI.Text(root, "Each picture is one neuron's output over the whole plane: <color=#7FB8FF>blue</color> where it says +, " +
                                        "<color=#FFB870>orange</color> where it says −. The next layer adds these pictures up with its weights. " +
                                        "Tap one to switch it off and see what it was carrying.", 0, 0, w, Intro - 8, 12.5f);
            intro.color = MLUI.Sub; intro.richText = true;
            grid = MLUI.Rect("Neurons", root, 0, Intro, w, h - Intro - 20);
            note = MLUI.Text(root, "", 0, h - 16, w, 16, 10); note.color = MLUI.Faint;
            capacity = 2 * Mathf.Max(1, (int)((h - Intro - 20 + Gap) / (MinCardH + Gap)));
            sim.Rebuilt += OnRebuilt; sim.Stepped += OnStepped;
        }
        void OnDestroy()
        {
            if (sim != null) { sim.Rebuilt -= OnRebuilt; sim.Stepped -= OnStepped; }
            foreach (var c in cards) if (c.texture != null) Destroy(c.texture);
        }
        void OnRebuilt() { rebuild = true; dirty = true; }
        void OnStepped() => dirty = true;

        void Update()
        {
            if (sim == null || sim.Net == null) return;
            if (rebuild || Time.unscaledTime >= nextMeasure) { Measure(); nextMeasure = Time.unscaledTime + MeasureInterval; }
            if (dirty && Time.unscaledTime >= nextPicture) { dirty = false; nextPicture = Time.unscaledTime + PictureInterval; DrawPictures(); }
        }

        // How much each neuron matters; the cards show the ones that matter most (all of them when they fit).
        void Measure()
        {
            ranked.Clear();
            for (int l = 1; l <= sim.Hidden.Count; l++)
                for (int i = 0; i < sim.Hidden[l - 1]; i++)
                    ranked.Add((l, i, sim.TestAccuracy - sim.AccuracyIfToggled(l, i)));
            bool all = ranked.Count <= capacity;
            if (!all) ranked.Sort((x, y) => Mathf.Abs(y.drop).CompareTo(Mathf.Abs(x.drop)));
            int shown = Mathf.Min(capacity, ranked.Count);
            if (!all) ranked.RemoveRange(shown, ranked.Count - shown);
            ranked.Sort((x, y) => x.layer != y.layer ? x.layer.CompareTo(y.layer) : x.index.CompareTo(y.index));
            bool same = !rebuild && cards.Count == ranked.Count;
            for (int k = 0; same && k < ranked.Count; k++) same = cards[k].layer == ranked[k].layer && cards[k].index == ranked[k].index;
            if (!same) BuildCards();
            rebuild = false;
            for (int k = 0; k < cards.Count; k++) Describe(cards[k], ranked[k].drop);
            int total = 0; foreach (int n in sim.Hidden) total += n;
            note.text = total == 0 ? "No hidden layers: add one on the Lab (Hidden layers +)."
                      : all ? $"{total} neurons  ·  tested on the {sim.Test.Count} test points"
                      : $"The {cards.Count} that matter most of {total} neurons  ·  tested on the {sim.Test.Count} test points";
        }

        void BuildCards()
        {
            foreach (var c in cards) if (c.texture != null) Destroy(c.texture);
            cards.Clear(); MLUI.Clear(grid);
            int columns = ranked.Count <= 6 ? 1 : 2;  // few neurons: one column of bigger cards, room for the words
            float w = (grid.rect.width - Gap * (columns - 1)) / columns;
            int rows = Mathf.Max(1, (ranked.Count + columns - 1) / columns);
            float cardH = Mathf.Clamp((grid.rect.height - Gap * (rows - 1)) / rows, MinCardH, MaxCardH);
            float big = cardH / MinCardH;
            for (int k = 0; k < ranked.Count; k++)
            {
                int layer = ranked[k].layer, index = ranked[k].index;
                float x = (k % columns) * (w + Gap), y = (k / columns) * (cardH + Gap);
                var c = new Card { layer = layer, index = index, texture = new Texture2D(Res, Res, TextureFormat.RGBA32, false), pixels = new Color32[Res * Res] };
                c.texture.filterMode = FilterMode.Bilinear; c.texture.wrapMode = TextureWrapMode.Clamp;
                c.back = MLUI.Card(grid, x, y, w, cardH, 10); c.back.raycastTarget = true;
                var button = c.back.gameObject.AddComponent<Button>(); button.targetGraphic = c.back;
                button.onClick.AddListener(() => sim.ToggleNeuron(layer, index + 1));
                c.picture = MLUI.Rect("Picture", c.back.transform, 6, 6, cardH - 12, cardH - 12).gameObject.AddComponent<RawImage>();
                c.picture.texture = c.texture; c.picture.raycastTarget = false;
                float tx = cardH, tw = w - cardH - 6, line = cardH / 3.4f;
                c.name = MLUI.Text(c.back.transform, NeuralLabSim.NeuronId(layer, index + 1), tx, line * 0.3f, tw, line + 2, 12 * big); c.name.fontStyle = FontStyles.Bold;
                c.detects = MLUI.Text(c.back.transform, "", tx, line * 1.3f, tw, line, 10.5f * big); c.detects.color = MLUI.Sub;
                c.detects.textWrappingMode = TextWrappingModes.NoWrap; c.detects.overflowMode = TextOverflowModes.Ellipsis;
                c.matters = MLUI.Text(c.back.transform, "", tx, line * 2.2f, tw, line, 10.5f * big); c.matters.richText = true;
                c.matters.textWrappingMode = TextWrappingModes.NoWrap; c.matters.overflowMode = TextOverflowModes.Ellipsis;
                cards.Add(c);
            }
        }

        void Describe(Card c, float drop)
        {
            bool off = sim.Net.disabled[c.layer][c.index];
            float flipped = sim.TestAccuracy - drop;
            string detects = sim.DetectsAsIfOn(c.layer, c.index);
            int cut = detects.IndexOf(" (correlation");
            if (cut > 0) detects = detects.Substring(0, cut) + (detects.EndsWith("now switched off") ? ", now switched off" : "");
            c.detects.text = "Looks at " + detects;
            c.name.color = off ? MLUI.Faint : MLUI.TextColor;
            c.back.color = off ? new Color(1, 0.36f, 0.42f, 0.08f) : MLUI.CardColor;
            c.picture.color = off ? new Color(1, 1, 1, 0.25f) : Color.white;
            if (off) c.matters.text = $"<color=#FF5D6C>Switched off.</color> On again → {flipped:0%}";
            else if (drop >= 0.05f) c.matters.text = $"<color=#FF9A3C>Off → {flipped:0%}</color> (−{drop * 100:0}): it matters";
            else if (drop <= -0.02f) c.matters.text = $"<color=#3DDC97>Off → {flipped:0%}</color>: it's in the way";
            else c.matters.text = $"<color=#FFFFFF60>Off → {flipped:0%}: others cover</color>";
        }

        // Each card's neuron output over the plane, one forward pass per grid point.
        void DrawPictures()
        {
            if (cards.Count == 0) return;
            var net = sim.Net;
            float step = 2 * NNData.Extent / (Res - 1);
            float scale = sim.Activation == NNActivation.ReLU || sim.Activation == NNActivation.Linear ? 0.25f : 1f;
            bool sigmoid = sim.Activation == NNActivation.Sigmoid;
            for (int py = 0; py < Res; py++)
                for (int px = 0; px < Res; px++)
                {
                    sim.Predict(-NNData.Extent + px * step, -NNData.Extent + py * step);
                    for (int k = 0; k < cards.Count; k++)
                    {
                        var c = cards[k];
                        float v = net.NeuronOutput(c.layer, c.index);  // 0 when switched off: a blank picture
                        if (sigmoid && !net.disabled[c.layer][c.index]) v = v * 2 - 1;
                        c.pixels[py * Res + px] = Shade(v * scale);
                    }
                }
            foreach (var c in cards) { c.texture.SetPixels32(c.pixels); c.texture.Apply(false); }
        }

        static Color32 Shade(float v)
        {
            v = Mathf.Clamp(v, -1, 1);
            return v >= 0 ? Color.Lerp(Mid, Blue, v) : Color.Lerp(Mid, Orange, -v);
        }
    }
}
