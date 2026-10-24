using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One grab spot of a <see cref="PanelManipulator"/>: the title bar, a corner, a side or the bottom edge.
/// Takes the press from either input path (DesktopRig or the Interaction SDK's PointableCanvasModule) and owns the
/// drag, so a ScrollRect behind it doesn't scroll. The manipulator follows the pointer itself until it's released.
/// </summary>
public class PanelHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler,
    IPointerUpHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public PanelManipulator owner;
    public PanelGrab grab;

    Graphic[] tint;
    Color[] baseColors;
    int hovers;
    bool held;

    /// <summary>Graphics drawn brighter while the handle is pointed at or held.</summary>
    public void SetTint(params Graphic[] graphics)
    {
        tint = graphics;
        baseColors = new Color[graphics.Length];
        for (int i = 0; i < graphics.Length; i++) baseColors[i] = graphics[i].color;
    }

    public void SetHeld(bool value)
    {
        held = value;
        Refresh();
    }

    public void OnPointerEnter(PointerEventData e)
    {
        hovers++;
        Refresh();
    }

    public void OnPointerExit(PointerEventData e)
    {
        hovers = Mathf.Max(0, hovers - 1);
        Refresh();
    }

    public void OnPointerDown(PointerEventData e) => owner.BeginGrab(this, e);
    public void OnPointerUp(PointerEventData e) => owner.EndGrab(this);

    public void OnBeginDrag(PointerEventData e) { }
    public void OnDrag(PointerEventData e) { }
    public void OnEndDrag(PointerEventData e) { }

    void OnDisable()
    {
        hovers = 0;
        held = false;
        Refresh();
    }

    void Refresh()
    {
        if (tint == null) return;
        bool lit = held || hovers > 0;
        for (int i = 0; i < tint.Length; i++)
            tint[i].color = lit ? ForgeTheme.HandleLit : baseColors[i];
    }
}
