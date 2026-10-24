using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// The grab shape of one part in the Inspect view: a mesh collider on the part's own mesh, so the pointer takes
/// exactly the part under it (a gear inside an open housing too; bounding boxes would nest). It only takes pointing:
/// it collides with nothing and sits on the Ignore Raycast layer, so other systems' physics raycasts skip it.
/// <see cref="CadTakeApart"/> adds one per leaf part on the stage and removes them on the way back to the desk.
/// Two input paths reach it, as with Core's VolumeHandle:
/// - Interaction SDK rays (controller, pinching hand): a <see cref="RayInteractable"/> on the shape, added in code.
/// - A mouse (DesktopRig in desktop mode, the input module in the sandbox): <see cref="CadPartRaycaster"/> puts the
///   shapes into the EventSystem's raycast, so they get the usual uGUI pointer events.
/// </summary>
[RequireComponent(typeof(MeshCollider))]
public class CadPartHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
{
    const int IgnoreRaycastLayer = 2;
    static readonly List<CadPartHandle> active = new List<CadPartHandle>();

    public CadTakeApart owner;
    public string id;

    MeshCollider shape;
    Mesh owned;  // a merged mesh made for a part with several meshes
    RayInteractable ray;
    int mouseHovers;

    public Collider Shape => shape;
    public static int Count => active.Count;

    /// <summary>Pointed at by the mouse or a ray (or held by a ray).</summary>
    public bool Hovered => mouseHovers > 0 || (ray != null && (ray.State == InteractableState.Hover || ray.State == InteractableState.Select));

    /// <summary>Nearest part the ray hits within <paramref name="maxDistance"/>.</summary>
    public static bool Raycast(Ray r, float maxDistance, out RaycastHit hit, out CadPartHandle handle)
    {
        hit = default;
        handle = null;
        foreach (var h in active)
        {
            if (h.shape.Raycast(r, out RaycastHit candidate, maxDistance))
            {
                maxDistance = candidate.distance;
                hit = candidate;
                handle = h;
            }
        }
        return handle != null;
    }

    /// <summary>
    /// Shape the part with <paramref name="mesh"/>, in this object's space. <paramref name="owns"/>: destroy the mesh
    /// with the handle (it was made for it).
    /// </summary>
    public void Init(CadTakeApart takeApart, string partId, Mesh mesh, bool owns)
    {
        owner = takeApart;
        id = partId;
        gameObject.layer = IgnoreRaycastLayer;
        shape = GetComponent<MeshCollider>();
        shape.sharedMesh = mesh;
        shape.excludeLayers = ~0;  // nothing to bump into, it's only for pointing
        if (owns) owned = mesh;

        var surface = gameObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(shape);
        ray = gameObject.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        ray.WhenSelectingInteractorAdded.Action += OnRaySelect;
    }

    void OnEnable() => active.Add(this);

    void OnDisable()
    {
        active.Remove(this);
        mouseHovers = 0;
    }

    void OnDestroy()
    {
        if (ray != null) ray.WhenSelectingInteractorAdded.Action -= OnRaySelect;
        if (owned != null) Destroy(owned);
    }

    // A ray lets go when its trigger / pinch does; CadTakeApart sees that through the pointer.
    void OnRaySelect(RayInteractor interactor)
    {
        Ray r = interactor.Ray;
        Vector3 point = shape.Raycast(r, out RaycastHit hit, 10f) ? hit.point : shape.bounds.center;
        owner.BeginGrab(this, CadPointer.FromRay(interactor, owner.Head), point);
    }

    public void OnPointerEnter(PointerEventData e) => mouseHovers++;
    public void OnPointerExit(PointerEventData e) => mouseHovers = Mathf.Max(0, mouseHovers - 1);

    public void OnPointerDown(PointerEventData e)
    {
        var pointer = CadPointer.FromEvent(e, owner.Head);
        if (pointer == null) return;
        Vector3 point = e.pointerCurrentRaycast.gameObject == gameObject ? e.pointerCurrentRaycast.worldPosition : shape.bounds.center;
        owner.BeginGrab(this, pointer, point);
    }
}
