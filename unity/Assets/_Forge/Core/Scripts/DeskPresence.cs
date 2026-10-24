using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Events;

public enum DeskState { Away, Near, Seated }

/// <summary>
/// Tracks whether the user is away from every table, standing right next to one, or sitting at it.
/// MRUK has no chair label, so "seated" means near a table with the head low (seated eye height).
/// On reaching a table it sets <see cref="Frame"/>: a transform on the table's front edge (the edge facing
/// the user), +Z pointing into the desk, +Y up. The workspace and the desk dock are laid out in that frame.
/// </summary>
public class DeskPresence : MonoBehaviour
{
    public DeskLocator locator;
    [Tooltip("Player head. Defaults to the main camera.")]
    public Transform head;

    [Header("Near: horizontal distance from head to the table edge (m)")]
    public float nearEnter = 0.7f;
    public float nearExit = 1.0f;

    [Header("Seated: head height above the floor (m)")]
    public float seatedEnter = 1.35f;
    public float seatedExit = 1.45f;

    [Tooltip("Debug / desktop: treat the user as seated at the nearest table, wherever they are.")]
    public bool forceSeated;

    [Tooltip("Keep the desk frame where it is (set by Workspace while a mode is open, so panels don't jump).")]
    public bool lockFrame;

    public UnityEvent<DeskState> stateChanged = new UnityEvent<DeskState>();
    /// <summary>The front edge moved (the user went round to another side of the desk).</summary>
    public UnityEvent frameChanged = new UnityEvent();

    const float CheckInterval = 0.1f;
    const int EdgeSwitchChecks = 5;  // a new edge must face the user for 0.5 s before the frame moves

    float nextCheck;
    Transform frame;
    int edge = -1;
    int edgeCandidateChecks;

    public DeskState State { get; private set; } = DeskState.Away;
    public MRUKAnchor Table { get; private set; }
    /// <summary>Front edge of the active desk: +Z into the desk, +Y up, origin at the edge midpoint on the top surface.</summary>
    public Transform Frame => frame;
    public float FrameWidth { get; private set; }
    public float FrameDepth { get; private set; }
    public bool AtDesk => State != DeskState.Away;

    void Awake()
    {
        frame = new GameObject("DeskFrame").transform;
        frame.SetParent(transform, false);
    }

    void Start()
    {
        if (head == null && Camera.main != null) head = Camera.main.transform;
    }

    void Update()
    {
        if (Time.time < nextCheck || locator == null || !locator.RoomLoaded || head == null) return;
        nextCheck = Time.time + CheckInterval;

        Vector3 h = head.position;
        bool wasAtDesk = AtDesk;
        // Stay locked to the current table while at it; otherwise look for the nearest one.
        MRUKAnchor table = wasAtDesk && Table != null ? Table : locator.NearestTable(h, out _);
        if (table == null)
        {
            SetState(DeskState.Away);
            return;
        }

        float distance = DeskTop.From(table).HorizontalDistance(h);
        bool near = forceSeated || distance < (wasAtDesk ? nearExit : nearEnter);
        if (!near)
        {
            Table = null;
            SetState(DeskState.Away);
            return;
        }

        if (!wasAtDesk || Table != table)
        {
            Table = table;
            UpdateFrame();
        }
        else if (!lockFrame)
        {
            // Follow the user round the desk: whichever edge they're at becomes the front.
            int facing = DeskTop.From(table).FacingEdgeIndex(h);
            edgeCandidateChecks = facing != edge ? edgeCandidateChecks + 1 : 0;
            if (edgeCandidateChecks >= EdgeSwitchChecks)
            {
                UpdateFrame();
                frameChanged?.Invoke();
            }
        }

        float headHeight = h.y - locator.FloorY;
        bool seated = forceSeated || headHeight < (State == DeskState.Seated ? seatedExit : seatedEnter);
        SetState(seated ? DeskState.Seated : DeskState.Near);
    }

    /// <summary>Re-pick the desk's front edge from where the head is now.</summary>
    public void UpdateFrame()
    {
        if (Table == null || head == null) return;
        var top = DeskTop.From(Table);
        edge = top.FacingEdgeIndex(head.position);
        edgeCandidateChecks = 0;
        top.Edge(edge, out Vector3 mid, out Vector3 inward, out float edgeLength, out float deskDepth);
        frame.SetPositionAndRotation(mid, Quaternion.LookRotation(inward, Vector3.up));
        FrameWidth = edgeLength;
        FrameDepth = deskDepth;
    }

    /// <summary>Head position in the desk frame (for laying panels out around the user's eyes).</summary>
    public Vector3 EyeInFrame() => frame.InverseTransformPoint(head.position);

    public void ToggleForceSeated() => forceSeated = !forceSeated;

    void SetState(DeskState next)
    {
        if (next == State) return;
        State = next;
        Debug.Log($"[DeskPresence] {next}" + (Table != null ? $" at table {DeskTop.From(Table).center}" : ""));
        stateChanged?.Invoke(next);
    }
}
