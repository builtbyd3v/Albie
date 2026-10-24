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

    /// <summary>Slow, fast, slow.</summary>
    public static float EaseInOutCubic(float t)
    {
        if (t < 0.5f) return 4f * t * t * t;
        float u = 1f - t;
        return 1f - 4f * u * u * u;
    }

    /// <summary>
    /// A damped spring let go from rest at 0, settling on 1, at progress <paramref name="t"/> (0..1). It lands on
    /// exactly 1 at rest: whatever motion is left is eased out over the last 30%, so nothing snaps at the end.
    /// <paramref name="damping"/>: 1 = critical (no overshoot), below overshoots (0.74 is about 3%, 0.9 a hair), above
    /// is slower. <paramref name="frequency"/>: natural oscillations per tween; higher settles sooner.
    /// </summary>
    public static float SpringDamped(float t, float damping, float frequency)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        damping = Mathf.Max(0f, damping);
        float w = 2f * Mathf.PI * Mathf.Max(0.01f, frequency), x;
        if (damping < 0.999f)
        {
            float wd = w * Mathf.Sqrt(1f - damping * damping);
            x = 1f - Mathf.Exp(-damping * w * t) * (Mathf.Cos(wd * t) + damping * w / wd * Mathf.Sin(wd * t));
        }
        else if (damping <= 1.001f)
        {
            x = 1f - Mathf.Exp(-w * t) * (1f + w * t);
        }
        else
        {
            float s = w * Mathf.Sqrt(damping * damping - 1f), r1 = -damping * w + s, r2 = -damping * w - s;
            x = 1f + (r2 * Mathf.Exp(r1 * t) - r1 * Mathf.Exp(r2 * t)) / (r1 - r2);
        }
        float u = Mathf.Clamp01((t - 0.7f) / 0.3f);
        return Mathf.LerpUnclamped(x, 1f, u * u * (3f - 2f * u));
    }

    /// <summary>
    /// A point on the way from <paramref name="from"/> to <paramref name="to"/> that goes round <paramref name="pivot"/>
    /// (about the vertical) rather than cutting across: its angle and distance from the pivot follow
    /// <paramref name="swing"/>, its height follows <paramref name="rise"/>. Rise ahead of swing makes an arc that lifts
    /// first, then swings out. Both at 1 lands exactly on <paramref name="to"/>.
    /// </summary>
    public static Vector3 Orbit(Vector3 pivot, Vector3 from, Vector3 to, float swing, float rise)
    {
        if (swing == 1f && rise == 1f) return to;
        float ax = from.x - pivot.x, az = from.z - pivot.z, bx = to.x - pivot.x, bz = to.z - pivot.z;
        float a = Mathf.Atan2(ax, az) * Mathf.Rad2Deg;
        float yaw = (a + Mathf.DeltaAngle(a, Mathf.Atan2(bx, bz) * Mathf.Rad2Deg) * swing) * Mathf.Deg2Rad;
        float radius = Mathf.LerpUnclamped(Mathf.Sqrt(ax * ax + az * az), Mathf.Sqrt(bx * bx + bz * bz), swing);
        return new Vector3(pivot.x + Mathf.Sin(yaw) * radius, Mathf.LerpUnclamped(from.y, to.y, rise), pivot.z + Mathf.Cos(yaw) * radius);
    }
}
