using System;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Tells its owner when the pointer (mouse, ray, a poke's hover) comes onto or leaves this hit area: +1 / -1, so an
/// owner with several areas (a parameter row: its background, steppers and slider) knows whether any is pointed at.
/// Only enter and exit: a press must still reach the Slider or Button up the hierarchy (DesktopRig sends each event to
/// the first object up the hierarchy that takes it).
/// </summary>
public class CadHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Action<int> changed;
    int inside;

    public static CadHover Add(GameObject go, Action<int> onChange)
    {
        var h = go.AddComponent<CadHover>();
        h.changed = onChange;
        return h;
    }

    public void OnPointerEnter(PointerEventData e)
    {
        inside++;
        changed?.Invoke(1);
    }

    public void OnPointerExit(PointerEventData e)
    {
        if (inside == 0) return;
        inside--;
        changed?.Invoke(-1);
    }

    void OnDisable()
    {
        // Hidden while pointed at (a tab switch, a rebuild): the exit never comes, so give it back now.
        while (inside > 0)
        {
            inside--;
            changed?.Invoke(-1);
        }
    }
}
