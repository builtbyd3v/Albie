using System;
using System.Collections;
using UnityEngine;

/// <summary>Tiny coroutine tweens for unfold/fold animations (no DOTween dependency).</summary>
public static class ForgeTween
{
    public static IEnumerator Run(float duration, float delay, Action<float> step, Action done = null)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        float t = 0f;
        while (t < duration)
        {
            step(t / duration);
            t += Time.deltaTime;
            yield return null;
        }
        step(1f);
        done?.Invoke();
    }

    public static float EaseOutBack(float t)
    {
        const float c1 = 1.4f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>Springy rise with an overshoot, CSS cubic-bezier(.22, 1.45, .36, 1) (the dock's raised ring).</summary>
    public static float Spring(float t) => CubicBezier(0.22f, 1.45f, 0.36f, 1f, t);

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2) timing function at progress <paramref name="x"/> (0..1).</summary>
    public static float CubicBezier(float x1, float y1, float x2, float y2, float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        float t = x;
        for (int i = 0; i < 8; i++)  // Newton: find t where the curve's x is x
        {
            float error = Bezier(x1, x2, t) - x;
            if (Mathf.Abs(error) < 1e-5f) break;
            float slope = BezierSlope(x1, x2, t);
            if (Mathf.Abs(slope) < 1e-6f) break;
            t = Mathf.Clamp01(t - error / slope);
        }
        return Bezier(y1, y2, t);
    }

    static float Bezier(float a, float b, float t) => ((1f - 3f * b + 3f * a) * t + (3f * b - 6f * a)) * t * t + 3f * a * t;
    static float BezierSlope(float a, float b, float t) => 3f * (1f - 3f * b + 3f * a) * t * t + 2f * (3f * b - 6f * a) * t + 3f * a;

    public static float EaseInCubic(float t) => t * t * t;

    public static float EaseOutCubic(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u;
    }
}
