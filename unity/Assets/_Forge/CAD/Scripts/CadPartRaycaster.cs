using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

/// <summary>
/// Puts the <see cref="CadPartHandle"/> boxes into the EventSystem's raycast for a mouse: DesktopRig's pointer in
/// desktop mode (it hit-tests UI through the EventSystem, plus Core's volume boxes) and the input module in the
/// sandbox. Nearest wins against panels, as between two canvases. XR rays reach the parts through their
/// RayInteractables instead (the Interaction SDK's canvas module only keeps hits on its own canvas).
/// <see cref="CadTakeApart"/> adds it next to itself.
/// </summary>
public class CadPartRaycaster : BaseRaycaster
{
    const float MaxDistance = 6f;

    public override Camera eventCamera => Camera.main;

    public override void Raycast(PointerEventData e, List<RaycastResult> results)
    {
        if (CadPartHandle.Count == 0) return;
        Ray ray;
        var rig = DesktopRig.Current;
        if (rig != null && rig.OwnsPointer(e)) ray = rig.PointerRay;
        else if (e is ExtendedPointerEventData x && x.pointerType == UIPointerType.MouseOrPen && Camera.main != null)
            ray = Camera.main.ScreenPointToRay(e.position);
        else return;
        if (!CadPartHandle.Raycast(ray, MaxDistance, out RaycastHit hit, out CadPartHandle handle)) return;
        results.Add(new RaycastResult
        {
            gameObject = handle.gameObject,
            module = this,
            distance = hit.distance,
            worldPosition = hit.point,
            worldNormal = hit.normal,
            screenPosition = e.position,
            index = results.Count,
        });
    }
}
