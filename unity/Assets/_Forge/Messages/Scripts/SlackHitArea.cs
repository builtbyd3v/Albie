using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// An invisible hit area on a canvas: takes the mouse (DesktopRig) and Interaction SDK rays and pokes but draws nothing
/// (a scroll viewport, so dragging empty space scrolls; a row's hover zone). Same idea as CAD's CadHitArea.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class SlackHitArea : Graphic
{
    protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();
}
