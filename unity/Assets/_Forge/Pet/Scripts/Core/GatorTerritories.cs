using System.Collections.Generic;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>One gator's area: a rectangle on a surface (world centre on the top face, yaw, size x = local X, y = local Z).</summary>
    public struct GatorTerritory
    {
        public GatorNavSurface Surface;
        public Vector3 Center;
        public Quaternion Rotation;
        public Vector2 Size;
    }

    /// <summary>
    /// Splits a GatorNavSurface into n strips across its long side with EQUAL WALKABLE AREA: holes (the desk keep-out,
    /// furniture on the floor) don't count, so a strip that contains the keep-out is wider to make up for it.
    /// Strips come back ordered nearest the viewer first (Bit gets the one by the user, the crew fan out from there).
    /// Pure math over GatorNavSurface (no NavMesh queries); allocation-free apart from the caller's list.
    /// </summary>
    public static class GatorTerritories
    {
        const int Bins = 96;            // along the long side
        const int DepthSamples = 16;    // across it, per bin

        static readonly float[] s_cum = new float[Bins + 1];
        static readonly float[] s_edges = new float[33];
        static readonly List<float> s_keys = new List<float>(32);

        /// <summary>Fill result with n territories on s (n clamped to 1..32). False if s isn't built.</summary>
        public static bool Split(GatorNavSurface s, int n, Vector3 viewer, List<GatorTerritory> result)
        {
            result.Clear();
            if (s == null || !s.IsBuilt || n <= 0) return false;
            n = Mathf.Min(n, s_edges.Length - 1);
            Vector2 size = s.Size;
            bool alongX = size.x >= size.y;
            float length = alongX ? size.x : size.y, depth = alongX ? size.y : size.x;
            Vector3 axis = s.Rotation * (alongX ? Vector3.right : Vector3.forward);
            Vector3 across = s.Rotation * (alongX ? Vector3.forward : Vector3.right);
            Vector3 origin = s.TopCenter - axis * (length * 0.5f);

            // Cumulative walkable fraction along the long side (each bin: share of depth samples outside every hole).
            s_cum[0] = 0f;
            float binLen = length / Bins;
            for (int i = 0; i < Bins; i++)
            {
                int free = 0;
                Vector3 mid = origin + axis * ((i + 0.5f) * binLen);
                for (int k = 0; k < DepthSamples; k++)
                {
                    Vector3 p = mid + across * (((k + 0.5f) / DepthSamples - 0.5f) * depth);
                    if (!s.IsInsideHole(p, out _)) free++;
                }
                s_cum[i + 1] = s_cum[i] + (float)free / DepthSamples;
            }
            float total = s_cum[Bins];
            if (total <= 0.0001f) return false;

            // Strip edges where the cumulative area crosses k/n of the total (linear inside a bin).
            s_edges[0] = 0f;
            s_edges[n] = length;
            int bin = 0;
            for (int k = 1; k < n; k++)
            {
                float target = total * k / n;
                while (bin < Bins - 1 && s_cum[bin + 1] < target) bin++;
                float a = s_cum[bin], b = s_cum[bin + 1];
                float t = b > a ? (target - a) / (b - a) : 0.5f;
                s_edges[k] = (bin + Mathf.Clamp01(t)) * binLen;
            }

            for (int k = 0; k < n; k++)
            {
                float a = s_edges[k], b = s_edges[k + 1];
                result.Add(new GatorTerritory
                {
                    Surface = s,
                    Center = origin + axis * ((a + b) * 0.5f),
                    Rotation = s.Rotation,
                    Size = alongX ? new Vector2(b - a, depth) : new Vector2(depth, b - a),
                });
            }

            // Nearest the viewer first (insertion sort on the flat distance; n is tiny).
            s_keys.Clear();
            for (int k = 0; k < result.Count; k++)
            {
                Vector3 d = result[k].Center - viewer; d.y = 0f;
                s_keys.Add(d.sqrMagnitude);
            }
            for (int i = 1; i < result.Count; i++)
                for (int j = i; j > 0 && s_keys[j] < s_keys[j - 1]; j--)
                {
                    (s_keys[j], s_keys[j - 1]) = (s_keys[j - 1], s_keys[j]);
                    (result[j], result[j - 1]) = (result[j - 1], result[j]);
                }
            return true;
        }

        /// <summary>How many gators fit across s at minWidth metres per strip (at least 1).</summary>
        public static int Capacity(GatorNavSurface s, float minWidth)
        {
            if (s == null || !s.IsBuilt) return 0;
            float length = Mathf.Max(s.Size.x, s.Size.y);
            return Mathf.Max(1, Mathf.FloorToInt(length / Mathf.Max(0.05f, minWidth)));
        }
    }
}
