using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// An invisible hit area on a canvas: it takes the mouse (DesktopRig) and Interaction SDK rays and pokes but draws
/// nothing (a slider's whole 32 mm track, a parameter row's hover zone).
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class CadHitArea : Graphic
{
    protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();
}
