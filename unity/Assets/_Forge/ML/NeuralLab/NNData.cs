using System;
using System.Collections.Generic;

namespace Forge.ML
{
    public enum NNDataset { Circle, Xor, Gauss, Spiral }
    public enum NNFeature { X, Y, XSquared, YSquared, XTimesY, SinX, SinY }

    public struct NNPoint { public float x, y, label; }  // label +1 (blue) or -1 (orange); x, y in [-6, 6]

    /// <summary>The TensorFlow Playground datasets (circle, XOR, two Gaussians, spiral) and input features.</summary>
    public static class NNData
    {
        public const float Extent = 6f;  // the boundary view spans [-6, 6] on both axes
        public static readonly NNFeature[] AllFeatures = (NNFeature[])Enum.GetValues(typeof(NNFeature));

        public static string Label(NNFeature f)
        {
            switch (f)
            {
                case NNFeature.X: return "x";
                case NNFeature.Y: return "y";
                case NNFeature.XSquared: return "x²";
                case NNFeature.YSquared: return "y²";
                case NNFeature.XTimesY: return "x·y";
                case NNFeature.SinX: return "sin x";
                default: return "sin y";
            }
        }
        public static string Id(NNFeature f)
        {
            switch (f)
            {
                case NNFeature.X: return "x";
                case NNFeature.Y: return "y";
                case NNFeature.XSquared: return "x2";
                case NNFeature.YSquared: return "y2";
                case NNFeature.XTimesY: return "xy";
                case NNFeature.SinX: return "sinx";
                default: return "siny";
            }
        }

        public static float Feature(NNFeature f, float x, float y)
        {
            switch (f)
            {
                case NNFeature.X: return x;
                case NNFeature.Y: return y;
                case NNFeature.XSquared: return x * x;
                case NNFeature.YSquared: return y * y;
                case NNFeature.XTimesY: return x * y;
                case NNFeature.SinX: return (float)Math.Sin(x);
                default: return (float)Math.Sin(y);
            }
        }

        /// <summary>Shuffled points; the caller splits them into train and test.</summary>
        public static List<NNPoint> Generate(NNDataset dataset, int count, float noise, int seed)
        {
            var rng = new Random(seed);
            var points = new List<NNPoint>(count);
            switch (dataset)
            {
                case NNDataset.Circle: Circle(points, count, noise, rng); break;
                case NNDataset.Xor: Xor(points, count, noise, rng); break;
                case NNDataset.Gauss: Gauss(points, count, noise, rng); break;
                default: Spiral(points, count, noise, rng); break;
            }
            for (int i = points.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = points[i]; points[i] = points[j]; points[j] = t; }
            return points;
        }

        static float U(Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);
        static float Normal(Random r, float mean, float variance)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return mean + (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * Math.Sqrt(variance));
        }

        static void Circle(List<NNPoint> points, int count, float noise, Random r)
        {
            const float radius = 5f;
            for (int half = 0; half < 2; half++)
                for (int i = 0; i < count / 2; i++)
                {
                    float rr = half == 0 ? U(r, 0, radius * 0.5f) : U(r, radius * 0.7f, radius), angle = U(r, 0, 2 * (float)Math.PI);
                    float x = rr * (float)Math.Sin(angle), y = rr * (float)Math.Cos(angle);
                    float nx = U(r, -radius, radius) * noise, ny = U(r, -radius, radius) * noise;
                    float label = (x + nx) * (x + nx) + (y + ny) * (y + ny) < radius * radius * 0.25f ? 1 : -1;
                    points.Add(new NNPoint { x = x, y = y, label = label });
                }
        }

        static void Xor(List<NNPoint> points, int count, float noise, Random r)
        {
            const float padding = 0.3f;
            for (int i = 0; i < count; i++)
            {
                float x = U(r, -5, 5); x += x > 0 ? padding : -padding;
                float y = U(r, -5, 5); y += y > 0 ? padding : -padding;
                float nx = U(r, -5, 5) * noise, ny = U(r, -5, 5) * noise;
                points.Add(new NNPoint { x = x, y = y, label = (x + nx) * (y + ny) >= 0 ? 1 : -1 });
            }
        }

        static void Gauss(List<NNPoint> points, int count, float noise, Random r)
        {
            float variance = 0.5f + noise * 3.5f;  // Playground: scale noise 0..0.5 to variance 0.5..4
            for (int half = 0; half < 2; half++)
            {
                float c = half == 0 ? 2 : -2;
                for (int i = 0; i < count / 2; i++)
                    points.Add(new NNPoint { x = Normal(r, c, variance), y = Normal(r, c, variance), label = half == 0 ? 1 : -1 });
            }
        }

        static void Spiral(List<NNPoint> points, int count, float noise, Random r)
        {
            int n = count / 2;
            for (int half = 0; half < 2; half++)
            {
                float delta = half == 0 ? 0 : (float)Math.PI;
                for (int i = 0; i < n; i++)
                {
                    float rr = i / (float)n * 5f, t = 1.75f * i / n * 2 * (float)Math.PI + delta;
                    float x = rr * (float)Math.Sin(t) + U(r, -1, 1) * noise, y = rr * (float)Math.Cos(t) + U(r, -1, 1) * noise;
                    points.Add(new NNPoint { x = x, y = y, label = half == 0 ? 1 : -1 });
                }
            }
        }
    }
}
