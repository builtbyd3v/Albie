using System;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Fires when the pointer (mouse, ray or poke) lets go of a Slider, so we regenerate once per drag.</summary>
public class CadSliderRelease : MonoBehaviour, IPointerUpHandler
{
    public event Action released;

    public void OnPointerUp(PointerEventData eventData) => released?.Invoke();
}
