using System.Collections.Generic;
using System.Text;
using Forge.ML;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Trains Neural Net Lab configurations headless with the Lab's own network and data, so the "Albie, fix it" recipes
/// (hub/ml/nn_lab.py RECIPES) are measured, not guessed. Each line: dataset, setup, test accuracy per seed.
/// </summary>
public static class NeuralLabRecipeCheck
{
    public static float Accuracy(NNDataset dataset, NNFeature[] features, int[] hidden, NNActivation activation, float rate,
                                 int epochs, int seed, float noise = 0f)
    {
        var points = NNData.Generate(dataset, NeuralLabSim.PointCount, noise, 7 + (int)dataset);
        var train = new List<NNPoint>(); var test = new List<NNPoint>();
        for (int i = 0; i < points.Count; i++) (i % 2 == 0 ? train : test).Add(points[i]);
        var net = new NNNetwork(features.Length, hidden, activation, seed);
        var buffer = new float[features.Length];
        float[] F(NNPoint p) { for (int f = 0; f < features.Length; f++) buffer[f] = NNData.Feature(features[f], p.x, p.y); return buffer; }
        for (int e = 0; e < epochs; e++)
            for (int i = 0; i < train.Count; i++)
            {
                net.Accumulate(F(train[i]), train[i].label);
                if ((i + 1) % NeuralLabSim.BatchSize == 0 || i == train.Count - 1) net.Apply(rate);
            }
        int right = 0;
        foreach (var p in test) if ((net.Forward(F(p)) >= 0 ? 1 : -1) == p.label) right++;
        return right / (float)test.Count;
    }

    // Play as the Lab plays it (NeuralLabSim.Update): stop at the auto-pause accuracy or after StallEpochs without a
    // 1% gain. Returns "pause"/"stall"/"running" with the epoch and test accuracy where it stopped.
    public static string Play(NNDataset dataset, NNFeature[] features, int[] hidden, NNActivation activation, float rate,
                              bool autoPause, int maxEpochs, int seed = 1)
    {
        var points = NNData.Generate(dataset, NeuralLabSim.PointCount, 0f, 7 + (int)dataset);
        var train = new List<NNPoint>(); var test = new List<NNPoint>();
        for (int i = 0; i < points.Count; i++) (i % 2 == 0 ? train : test).Add(points[i]);
        var net = new NNNetwork(features.Length, hidden, activation, seed);
        var buffer = new float[features.Length];
        float[] F(NNPoint p) { for (int f = 0; f < features.Length; f++) buffer[f] = NNData.Feature(features[f], p.x, p.y); return buffer; }
        float Acc() { int right = 0; foreach (var p in test) if ((net.Forward(F(p)) >= 0 ? 1 : -1) == p.label) right++; return right / (float)test.Count; }
        float acc = Acc(), best = acc; int bestEpoch = 0;
        for (int e = 1; e <= maxEpochs; e++)
        {
            for (int i = 0; i < train.Count; i++)
            {
                net.Accumulate(F(train[i]), train[i].label);
                if ((i + 1) % NeuralLabSim.BatchSize == 0 || i == train.Count - 1) net.Apply(rate);
            }
            acc = Acc();
            if (acc >= best + 0.01f) { best = acc; bestEpoch = e; }
            if (autoPause && acc >= NeuralLabSim.AutoPauseAccuracy) return $"pause@{e} {acc:0.000}";
            if (autoPause && e - bestEpoch >= NeuralLabSim.StallEpochs) return $"stall@{e} {acc:0.000}";
        }
        return $"running@{maxEpochs} {acc:0.000}";
    }

    static NNFeature[] Parse(params string[] ids)
    {
        var list = new List<NNFeature>();
        foreach (var id in ids) foreach (var f in NNData.AllFeatures) if (NNData.Id(f) == id) list.Add(f);
        return list.ToArray();
    }

    /// <summary>Every dataset × activation from the Lab's default network: how Play stops, then the hub's recipe for
    /// that dataset (what "Albie, fix it" applies) played the same way: where it auto-pauses.</summary>
    [MenuItem("Forge/ML/Check Neural Lab Matrix")]
    public static void LogMatrix() => Debug.Log(Matrix());

    public static string Matrix()
    {
        var xy = new[] { NNFeature.X, NNFeature.Y };
        var recipes = new Dictionary<NNDataset, (NNFeature[] f, int[] h, float lr)>  // hub/ml/nn_lab.py RECIPES
        {
            [NNDataset.Spiral] = (Parse("x", "y", "x2", "y2", "sinx", "siny"), new[] { 8, 8 }, 0.005f),
            [NNDataset.Circle] = (Parse("x", "y", "x2", "y2"), new[] { 4 }, 0.0003f),
            [NNDataset.Xor] = (Parse("x", "y", "xy"), new[] { 4, 2 }, 0.0001f),
            [NNDataset.Gauss] = (Parse("x", "y"), new[] { 4, 2 }, 0.0001f),
        };
        var sb = new StringBuilder();
        foreach (NNDataset d in System.Enum.GetValues(typeof(NNDataset)))
            foreach (NNActivation a in System.Enum.GetValues(typeof(NNActivation)))
            {
                sb.Append(d).Append(' ').Append(a).Append(" | play ").Append(Play(d, xy, new[] { 4, 2 }, a, 0.03f, true, 3000));
                var r = recipes[d];
                sb.Append(" | fix ").Append(Play(d, r.f, r.h, NNActivation.Tanh, r.lr, true, 3000)).Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>How fast each fix recipe climbs to the auto-pause, by learning rate (4 seeds): after "Albie, fix it" it
    /// should take 8-15 s (250-450 epochs at 30 a second) to look like real training.</summary>
    public static string Pace(params float[] rates)
    {
        var rows = new (NNDataset d, NNFeature[] f, int[] h)[]
        {
            (NNDataset.Spiral, Parse("x", "y", "x2", "y2", "sinx", "siny"), new[] { 8, 8 }),
            (NNDataset.Circle, Parse("x", "y", "x2", "y2"), new[] { 4 }),
            (NNDataset.Xor, Parse("x", "y", "xy"), new[] { 4, 2 }),
            (NNDataset.Gauss, Parse("x", "y"), new[] { 4, 2 }),
        };
        var sb = new StringBuilder();
        foreach (var r in rows)
            foreach (float lr in rates)
            {
                sb.Append(r.d).Append(" lr ").Append(lr).Append(':');
                for (int seed = 1; seed <= 4; seed++) sb.Append(' ').Append(Play(r.d, r.f, r.h, NNActivation.Tanh, lr, true, 3000, seed));
                sb.Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>One candidate fix: "Circle", "x,y,x2,y2", [4], tanh, 0.01 → where Play auto-pauses, for 4 seeds.</summary>
    public static string PaceRow(string dataset, string features, int[] hidden, float rate, string activation = "Tanh")
    {
        var d = (NNDataset)System.Enum.Parse(typeof(NNDataset), dataset, true);
        var a = (NNActivation)System.Enum.Parse(typeof(NNActivation), activation, true);
        var sb = new StringBuilder($"{d} {features} [{string.Join(",", hidden)}] {a} {rate}:");
        for (int seed = 1; seed <= 4; seed++) sb.Append(' ').Append(Play(d, Parse(features.Split(',')), hidden, a, rate, true, 3000, seed));
        return sb.Append('\n').ToString();
    }

    [MenuItem("Forge/ML/Check Neural Lab Recipes")]
    public static void LogAll() => Debug.Log(Run());

    public static string Run()
    {
        var xy = new[] { NNFeature.X, NNFeature.Y };
        var squares = new[] { NNFeature.X, NNFeature.Y, NNFeature.XSquared, NNFeature.YSquared };
        var cross = new[] { NNFeature.X, NNFeature.Y, NNFeature.XTimesY };
        var all = new[] { NNFeature.X, NNFeature.Y, NNFeature.XSquared, NNFeature.YSquared, NNFeature.XTimesY, NNFeature.SinX, NNFeature.SinY };
        var sb = new StringBuilder();
        void Row(string label, NNDataset d, NNFeature[] f, int[] h, NNActivation a, float lr, int epochs)
        {
            sb.Append(d).Append(" | ").Append(label).Append(" |");
            for (int seed = 1; seed <= 3; seed++) sb.Append(' ').Append(Accuracy(d, f, h, a, lr, epochs, seed).ToString("0.000"));
            sb.Append('\n');
        }
        // weak starting points (what the demo begins with) and candidate fixes, 300 epochs = 10 s in the Lab
        Row("weak xy [2] tanh .03", NNDataset.Circle, xy, new[] { 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("default xy [4,2] tanh .03", NNDataset.Circle, xy, new[] { 4, 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("fix x y x2 y2 [4] tanh .03", NNDataset.Circle, squares, new[] { 4 }, NNActivation.Tanh, 0.03f, 300);
        Row("weak xy [2] tanh .03", NNDataset.Xor, xy, new[] { 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("default xy [4,2] tanh .03", NNDataset.Xor, xy, new[] { 4, 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("fix x y xy [4,2] tanh .03", NNDataset.Xor, cross, new[] { 4, 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("default xy [4,2] tanh .03", NNDataset.Gauss, xy, new[] { 4, 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("weak xy [4,2] tanh .03", NNDataset.Spiral, xy, new[] { 4, 2 }, NNActivation.Tanh, 0.03f, 300);
        Row("fix all [8,8,4] tanh .03", NNDataset.Spiral, all, new[] { 8, 8, 4 }, NNActivation.Tanh, 0.03f, 300);
        Row("fix all [8,8,4] relu .01", NNDataset.Spiral, all, new[] { 8, 8, 4 }, NNActivation.ReLU, 0.01f, 300);
        Row("fix xy x2 y2 sin [8,8] tanh .03", NNDataset.Spiral, new[] { NNFeature.X, NNFeature.Y, NNFeature.XSquared, NNFeature.YSquared, NNFeature.SinX, NNFeature.SinY }, new[] { 8, 8 }, NNActivation.Tanh, 0.03f, 300);
        Row("fix all [8,8,6] tanh .1", NNDataset.Spiral, all, new[] { 8, 8, 6 }, NNActivation.Tanh, 0.1f, 300);
        Row("lr too high xy [4,2] tanh 1", NNDataset.Circle, xy, new[] { 4, 2 }, NNActivation.Tanh, 1f, 300);
        Row("linear xy [4,2] .03", NNDataset.Circle, xy, new[] { 4, 2 }, NNActivation.Linear, 0.03f, 300);
        return sb.ToString();
    }
}
