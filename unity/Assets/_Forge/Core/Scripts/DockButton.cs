using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Hover and press state of one dock button, for its light effects. Sits next to the button's invisible raycast
/// Image + Button, which never move, so the mouse (<see cref="DesktopRig"/>), Interaction SDK rays and pokes all
/// hit exactly the same spot whatever the visuals do. Several pointers can hover at once.
/// </summary>
public class DockButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    int hovering;
    int pressing;

    public bool Hovered => hovering > 0;
    public bool Pressed => pressing > 0;

    public void OnPointerEnter(PointerEventData e) => hovering++;
    public void OnPointerExit(PointerEventData e) => hovering = Mathf.Max(0, hovering - 1);
    public void OnPointerDown(PointerEventData e) => pressing++;
    public void OnPointerUp(PointerEventData e) => pressing = Mathf.Max(0, pressing - 1);

    void OnDisable() => hovering = pressing = 0;
}
