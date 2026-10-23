using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Finds the desk once MRUK has loaded the room (simulator synthetic room or a headset room scan):
/// the TABLE anchor closest to the player's head. The workspace unfolds from <see cref="Desk"/>.
/// </summary>
public class DeskLocator : MonoBehaviour
{
    [Tooltip("Player head, used to pick the nearest table. Defaults to the main camera.")]
    public Transform head;

    public UnityEvent<MRUKAnchor> deskFound;

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

        Vector3 from = head != null ? head.position : Vector3.zero;
        float best = float.MaxValue;
        int tables = 0;
        foreach (var anchor in room.Anchors)
        {
            if (!anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) continue;
            tables++;
            float d = Vector3.SqrMagnitude(anchor.GetAnchorCenter() - from);
            if (d < best)
            {
                best = d;
                Desk = anchor;
            }
        }

        Debug.Log($"[DeskLocator] Room '{room.name}' loaded: {room.Anchors.Count} anchors, {tables} table(s).");
        if (Desk == null)
        {
            Debug.LogWarning("[DeskLocator] No TABLE in this room. Pick a simulator room with a table, or place the desk manually.");
            return;
        }
        Debug.Log($"[DeskLocator] Desk at {Desk.GetAnchorCenter()}, {Mathf.Sqrt(best):F2} m from head.");
        deskFound?.Invoke(Desk);
    }
}
