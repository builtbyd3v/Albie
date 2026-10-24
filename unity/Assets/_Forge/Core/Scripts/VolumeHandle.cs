using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// One grab spot of a <see cref="VolumeManipulator"/>: an invisible trigger box (the base, or the model).
/// Two input paths reach it:
/// - Interaction SDK rays (controller, pinching hand): a <see cref="RayInteractable"/> on the box, added in code.
/// - DesktopRig's mouse: it asks <see cref="Raycast"/> for the nearest box and sends it the usual uGUI pointer events.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class VolumeHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    static readonly List<VolumeHandle> active = new List<VolumeHandle>();

    public VolumeManipulator owner;
    public VolumeManipulator.Grab grab;

    BoxCollider box;
    RayInteractable ray;
    int mouseHovers;

    public BoxCollider Box => box;

    /// <summary>Pointed at by the mouse or a ray (or held).</summary>
    public bool Hovered => mouseHovers > 0 || (ray != null && ray.State != InteractableState.Normal);

    /// <summary>Nearest handle box the ray hits within <paramref name="maxDistance"/> (only these boxes, not the room).</summary>
    public static bool Raycast(Ray r, float maxDistance, out RaycastHit hit, out VolumeHandle handle)
    {
        hit = default;
        handle = null;
        foreach (var h in active)
        {
            if (h.box.Raycast(r, out RaycastHit candidate, maxDistance))
            {
                maxDistance = candidate.distance;
                hit = candidate;
                handle = h;
            }
        }
        return handle != null;
    }

    public void Init(VolumeManipulator manipulator, VolumeManipulator.Grab kind)
    {
        owner = manipulator;
        grab = kind;
        box = GetComponent<BoxCollider>();
        box.isTrigger = true;  // nothing to bump into, it's only for pointing

        var surface = gameObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(box);
        ray = gameObject.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        ray.WhenSelectingInteractorAdded.Action += OnRaySelect;
        ray.WhenSelectingInteractorRemoved.Action += OnRayRelease;
    }

    void OnEnable() => active.Add(this);

    void OnDisable()
    {
        active.Remove(this);
        mouseHovers = 0;
    }

    void OnDestroy()
    {
        if (ray == null) return;
        ray.WhenSelectingInteractorAdded.Action -= OnRaySelect;
        ray.WhenSelectingInteractorRemoved.Action -= OnRayRelease;
    }

    void OnRaySelect(RayInteractor interactor) => owner.BeginGrab(this, ForgePointer.FromRay(interactor, owner.Head));
    void OnRayRelease(RayInteractor interactor) => owner.EndGrab(this);

    public void OnPointerEnter(PointerEventData e) => mouseHovers++;
    public void OnPointerExit(PointerEventData e) => mouseHovers = Mathf.Max(0, mouseHovers - 1);

    public void OnPointerDown(PointerEventData e)
    {
        if (ForgePointer.TryFind(e, owner.Head, out ForgePointer pointer)) owner.BeginGrab(this, pointer);
    }

    public void OnPointerUp(PointerEventData e) => owner.EndGrab(this);
}
