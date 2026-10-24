using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Collects the room's tables once MRUK has loaded it (simulator synthetic room or a headset room scan).
/// Any table can become the desk; <see cref="DeskPresence"/> picks the one the user walks up to.
/// </summary>
public class DeskLocator : MonoBehaviour
{
    [Tooltip("Player head, used to pick the nearest table at load. Defaults to the main camera.")]
    public Transform head;

    public UnityEvent<MRUKAnchor> deskFound = new UnityEvent<MRUKAnchor>();

    readonly List<MRUKAnchor> tables = new List<MRUKAnchor>();

    public bool RoomLoaded { get; private set; }
    public float FloorY { get; private set; }
    public IReadOnlyList<MRUKAnchor> Tables => tables;
    /// <summary>The table nearest the head when the room loaded.</summary>
    public MRUKAnchor Desk { get; private set; }

    void Start()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (MRUK.Instance == null)
        {
            Debug.LogWarning("[DeskLocator] No MRUK in the scene; no room to search.");
            return;
        }
        MRUK.Instance.RegisterSceneLoadedCallback(OnRoomLoaded);
    }

    void OnRoomLoaded()
    {
        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[DeskLocator] Room loaded but there is no current room.");
            return;
        }

        tables.Clear();
        foreach (var anchor in room.Anchors)
            if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE) && anchor.PlaneRect.HasValue)
                tables.Add(anchor);
        FloorY = room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : 0f;
        RoomLoaded = true;

        Debug.Log($"[DeskLocator] Room '{room.name}' loaded: {room.Anchors.Count} anchors, {tables.Count} table(s), floor at y={FloorY:F2}.");
        if (tables.Count == 0)
        {
            Debug.LogWarning("[DeskLocator] No TABLE in this room. Pick a simulator room with a table, or place the desk manually.");
            return;
        }
        Desk = NearestTable(head != null ? head.position : Vector3.zero, out float distance);
        Debug.Log($"[DeskLocator] Nearest desk at {DeskTop.From(Desk).center}, {distance:F2} m from head.");
        deskFound?.Invoke(Desk);
    }

    /// <summary>Table whose top is horizontally closest to <paramref name="position"/>.</summary>
    public MRUKAnchor NearestTable(Vector3 position, out float distance)
    {
        MRUKAnchor best = null;
        distance = float.MaxValue;
        for (int i = 0; i < tables.Count; i++)
        {
            if (tables[i] == null) continue;
            float d = DeskTop.From(tables[i]).HorizontalDistance(position);
            if (d < distance)
            {
                distance = d;
                best = tables[i];
            }
        }
        return best;
    }
}

/// <summary>A table's top surface in world space: a horizontal rectangle.</summary>
public struct DeskTop
{
    public Vector3 center;   // world, on the top surface
    public Vector3 right;    // horizontal unit axis along width
    public Vector3 depth;    // horizontal unit axis along depth
    public float width;
    public float depthSize;

    // MRUK table anchors: +Z is world up, the plane rect lies in local XY at the table top.
    public static DeskTop From(MRUKAnchor anchor)
    {
        var t = anchor.transform;
        Rect rect = anchor.PlaneRect.Value;
        return new DeskTop
        {
            center = t.TransformPoint(new Vector3(rect.center.x, rect.center.y, 0f)),
            right = Vector3.ProjectOnPlane(t.right, Vector3.up).normalized,
            depth = Vector3.ProjectOnPlane(t.up, Vector3.up).normalized,
            width = rect.width,
            depthSize = rect.height,
        };
    }

    /// <summary>Horizontal distance from a point to the rectangle (0 when above it).</summary>
    public float HorizontalDistance(Vector3 p)
    {
        Vector3 d = p - center;
        float dx = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, right)) - width * 0.5f);
        float dz = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(d, depth)) - depthSize * 0.5f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// The edge facing a viewer at <paramref name="viewer"/>: its midpoint, the direction into the desk
    /// (away from the viewer), the edge length, and how deep the desk is from that edge.
    /// </summary>
    public void FacingEdge(Vector3 viewer, out Vector3 midpoint, out Vector3 inward, out float edgeLength, out float deskDepth) =>
        Edge(FacingEdgeIndex(viewer), out midpoint, out inward, out edgeLength, out deskDepth);

    /// <summary>Which edge faces the viewer: 0 = +right, 1 = -right, 2 = +depth, 3 = -depth.</summary>
    public int FacingEdgeIndex(Vector3 viewer)
    {
        Vector3 d = viewer - center;
        float u = Vector3.Dot(d, right) / Mathf.Max(width * 0.5f, 0.01f);
        float v = Vector3.Dot(d, depth) / Mathf.Max(depthSize * 0.5f, 0.01f);
        if (Mathf.Abs(u) > Mathf.Abs(v)) return u > 0f ? 0 : 1;
        return v > 0f ? 2 : 3;
    }

    public void Edge(int index, out Vector3 midpoint, out Vector3 inward, out float edgeLength, out float deskDepth)
    {
        bool side = index < 2;
        Vector3 outward = (side ? right : depth) * (index % 2 == 0 ? 1f : -1f);
        midpoint = center + outward * (side ? width : depthSize) * 0.5f;
        inward = -outward;
        edgeLength = side ? depthSize : width;
        deskDepth = side ? width : depthSize;
    }
}
