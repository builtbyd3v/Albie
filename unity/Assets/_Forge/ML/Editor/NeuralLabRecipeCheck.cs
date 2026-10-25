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
