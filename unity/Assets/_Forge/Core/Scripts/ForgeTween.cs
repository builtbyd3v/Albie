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

    public static float EaseInCubic(float t) => t * t * t;

    public static float EaseOutCubic(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u;
    }
}
