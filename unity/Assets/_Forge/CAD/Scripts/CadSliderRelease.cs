using System;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Fires when the pointer (mouse, ray or poke) presses and lets go of a Slider, so we regenerate once per drag and a
/// refresh mid-drag can leave the knob alone. Lives on the Slider's own GameObject, so both events reach it with the
/// Slider's (DesktopRig and the input modules send an event to every handler on the object that takes it).
/// </summary>
public class CadSliderRelease : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public event Action pressed;
    public event Action released;

    /// <summary>Held: pressed and not let go yet.</summary>
    public bool Held { get; private set; }

    public void OnPointerDown(PointerEventData eventData)
    {
        Held = true;
        pressed?.Invoke();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        Held = false;
        released?.Invoke();
    }

    void OnDisable() => Held = false;
}
