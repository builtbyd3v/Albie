using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Take the model apart in the Inspect view (CadViewer's stage; on the desk, dragging still turns the whole model).
/// Point at a part and it lights up; grab it with the mouse or a controller / hand ray, pull it off, turn it and let
/// go: it stays there. Let go near its place and it snaps back; Reassemble (the panel) puts everything back.
/// A card next to the part shows its name, material and mass: a graphite chip like the viewer's labels (CadLabel), sized
/// to read the same at any distance. View only: the design version never changes.
/// Mouse: drag to carry, Shift + drag to turn, scroll to push it away / pull it in. Rays: the part follows the
/// controller or hand (twist to turn it), thumbstick to push / pull.
/// A held part keeps its pose relative to the pointer (the idea of cad-vision's CADGrabSession, MIT).
/// Albie can hand you a part too (cad.action take_off / put_back, through CadSession): it flies to his jaw
/// (CadSession.PartCarrier), rides there while he brings it, then flies to just in front of your eyes and stays.
/// </summary>
[RequireComponent(typeof(CadViewer))]
public class CadTakeApart : MonoBehaviour
{
    [Tooltip("Let go this close to its place (m, at the design's real size) and the part snaps back...")]
    public float snapDistance = 0.03f;
    [Tooltip("...but never from further than this in the room (m): small designs are scaled up a lot on the stage.")]
    public float maxSnapWorld = 0.08f;
    [Tooltip("Mouse, Shift + drag: degrees of turn per degree of pointer swing.")]
    public float turnGain = 3f;
    [Tooltip("Scroll wheel / thumbstick: push the held part away or pull it in (m per notch).")]
    public float pushPerNotch = 0.04f;
    public float homeSeconds = 0.35f;
    [Tooltip("Albie handing you a part: seconds for each flight (to his jaw, from his jaw to you).")]
    public float fetchSeconds = 0.5f;
    [Tooltip("Where a handed-over part waits: this far in front of your eyes (m)...")]
    public float handOverDistance = 0.4f;
    [Tooltip("...and this far below them (m).")]
    public float handOverDrop = 0.12f;

    const float MinHold = 0.15f, MaxHold = 3f;  // grabbed point's distance along the pointer (m)
    const string LayerPrefix = "Glow ";         // CadViewer's outline layers
    const string MouseHint = "drag: move · Shift+drag: turn · scroll: push / pull";
    const string RayHint = "twist to turn · thumbstick: push / pull";
    const string HandHint = "twist to turn · reach out to push it away";

    class Piece
    {
        public string id;
        public Transform transform;
        public CadPartHandle handle;
        public Vector3 centre;           // visual centre, in the part's own space
        public Quaternion homeRotation;
        public bool off;                 // taken off: held, left somewhere (or flying home, until it lands)
        public Coroutine homing;
    }

    /// <summary>The player's eyes.</summary>
    public Transform Head => Camera.main != null ? Camera.main.transform : transform;

    CadViewer viewer;
    CadSession session;
    CadModel model;
    readonly List<Piece> pieces = new List<Piece>();
    Piece held, last, carded;
    bool cardHolding;
    CadPointer pointer;
    Vector3 heldPosition, heldGrab;  // held part's origin and the grabbed point, in pointer space
    Quaternion heldRotation;         // held part's rotation in pointer space
    Vector3 grabLocal;               // the grabbed point on the part
    bool turning;
    Vector3 turnDirection;
    CadLabel card;       // made on first use (it holds a MaterialPropertyBlock: not in a field initializer)
    CadChips cardChip;
    Piece fetched, lastTaken;        // fetched: flying to Albie's jaw, riding in it, or flying to you
    Transform fetchCarrier;          // null: flying to the hand-over spot
    Vector3 fetchFrom, handOverAt;   // the part's centre when the flight began; where it waits for you
    Quaternion fetchFromRotation, carryRotation;  // carryRotation: in the carrier's space
    float fetchT;
    string pendingTake;              // asked before the parts had grab shapes: Attach takes it

    void Awake()
    {
        viewer = GetComponent<CadViewer>();
        gameObject.AddComponent<CadPartRaycaster>();
    }

    void OnEnable()
    {
        session = CadSession.Get(viewer.mocks);
        session.ReassembleRequested += Reassemble;
        session.TakeOffRequested += OnTakeOffRequested;
        session.PutBackRequested += OnPutBackRequested;
    }

    void OnDisable()
    {
        if (session == null) return;
        session.ReassembleRequested -= Reassemble;
        session.TakeOffRequested -= OnTakeOffRequested;
        session.PutBackRequested -= OnPutBackRequested;
    }

    void OnDestroy()
    {
        card?.Destroy();
        cardChip?.Destroy();
    }

    /// <summary>Give the model's parts grab shapes (CadViewer: the model went up on the stage, or a new version arrived there).</summary>
    public void Attach(CadModel m)
    {
        string take = pendingTake;
        Release(instant: true);
        model = m;
        var filters = new List<MeshFilter>();
        foreach (var id in m.partIds)
        {
            var t = m.objects[id].transform;
            filters.Clear();
            foreach (var f in t.GetComponentsInChildren<MeshFilter>())
                if (f.sharedMesh != null && !f.name.StartsWith(LayerPrefix, System.StringComparison.Ordinal)) filters.Add(f);
            if (filters.Count == 0) continue;
            // One mesh (the usual case): shape it as is, beside the mesh. Several: merge them in the part's space.
            Transform at = filters.Count == 1 ? filters[0].transform : t;
            Mesh mesh = filters.Count == 1 ? filters[0].sharedMesh : Merge(filters, t);
            var shape = new GameObject("Grab " + id, typeof(MeshCollider));
            shape.transform.SetParent(at, false);
            var handle = shape.AddComponent<CadPartHandle>();
            handle.Init(this, id, mesh, owns: filters.Count > 1);
            PartBounds(t, out Bounds b);
            pieces.Add(new Piece { id = id, transform = t, handle = handle, centre = b.center, homeRotation = t.localRotation });
        }
        if (take != null) TakeOff(take, session != null ? session.PartCarrier : null);
    }

    static Mesh Merge(List<MeshFilter> filters, Transform space)
    {
        var parts = new List<CombineInstance>();
        int vertices = 0;
        foreach (var f in filters)
        {
            var matrix = space.worldToLocalMatrix * f.transform.localToWorldMatrix;
            vertices += f.sharedMesh.vertexCount;
            for (int i = 0; i < f.sharedMesh.subMeshCount; i++)
                parts.Add(new CombineInstance { mesh = f.sharedMesh, subMeshIndex = i, transform = matrix });
        }
        var mesh = new Mesh { name = space.name + " (grab)" };
        if (vertices > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.CombineMeshes(parts.ToArray(), mergeSubMeshes: true, useMatrices: true);
        return mesh;
    }

    /// <summary>
    /// Remove the grab shapes and put every part back: instantly (a new version is coming in, CAD mode closed) or
    /// with a short tween (Back to desk, while the model flies home).
    /// </summary>
    public void Release(bool instant)
    {
        held = null;
        pointer = null;
        DropFetch();
        lastTaken = null;
        pendingTake = null;
        foreach (var p in pieces)
        {
            if (p.handle != null)
            {
                p.handle.gameObject.SetActive(false);  // Destroy waits for the end of the frame; stop raycasts now
                Destroy(p.handle.gameObject);
            }
            if (p.off || p.homing != null) SendHome(p, instant);
        }
        pieces.Clear();
        last = null;
        viewer.Highlight(null);
        ShowCard(null, false);
        if (session != null) session.SetPartsOff(0);
    }

    void Reassemble()
    {
        held = null;
        pointer = null;
        DropFetch();
        foreach (var p in pieces)
            if (p.off) SendHome(p, instant: false);
        session.SetPartsOff(0);
    }

    // Albie hands you a part

    void OnTakeOffRequested(string id) => TakeOff(id, session.PartCarrier);

    void OnPutBackRequested(string id) => PutBack(id);

    /// <summary>
    /// Take a part off for Albie: it flies to <paramref name="carrier"/> (his jaw) and rides there (CadSession.PartTaken
    /// tells the pet), or with no carrier it flies to just in front of your eyes and stays, like a part you let go.
    /// Called again with no carrier for the part he carries: the hand-over. Off already: the next part of its group
    /// that isn't (FIN_2 after FIN_1). False if nothing matches, you hold it, or a demo is running.
    /// </summary>
    public bool TakeOff(string id, Transform carrier)
    {
        if (string.IsNullOrEmpty(id) || viewer.DemoRunning) return false;
        if (pieces.Count == 0)
        {
            pendingTake = id;  // on its way up to the stage: Attach takes it
            return true;
        }
        var p = fetched != null && fetched.id == id ? fetched : Pick(id);
        if (p == null || p.transform == null || p == held) return false;
        if (p.homing != null)
        {
            StopCoroutine(p.homing);
            p.homing = null;
        }
        p.off = true;
        viewer.SetDetached(p.transform, true);
        var t = p.transform;
        fetched = last = lastTaken = p;
        fetchCarrier = carrier;
        fetchT = 0f;
        fetchFrom = t.TransformPoint(p.centre);
        fetchFromRotation = t.rotation;
        carryRotation = carrier != null ? Quaternion.Inverse(carrier.rotation) * t.rotation : Quaternion.identity;
        handOverAt = HandOverSpot();
        session.SetPartsOff(CountOff());
        if (carrier != null) session.ReportPartTaken(p.id);
        return true;
    }

    /// <summary>Put a part back in its place (null or empty: the last one Albie took off). False if it isn't off.</summary>
    public bool PutBack(string id)
    {
        Piece p = lastTaken;
        if (!string.IsNullOrEmpty(id))
        {
            p = null;
            foreach (var x in pieces)
                if (x.id == id) p = x;
        }
        if (p == null || !p.off || p == held) return false;
        if (fetched == p) fetched = null;
        SendHome(p, instant: false);
        session.SetPartsOff(CountOff());
        session.ReportPartPutBack(p.id);
        return true;
    }

    Piece Pick(string id)
    {
        Piece exact = null;
        foreach (var p in pieces)
            if (p.id == id) exact = p;
        if (exact != null && !exact.off) return exact;
        string group = Group(id);
        foreach (var p in pieces)
            if (!p.off && Group(p.id) == group) return p;
        return exact;
    }

    static string Group(string id)
    {
        int i = id.LastIndexOf('_');
        return i > 0 ? id.Substring(0, i) : id;
    }

    Vector3 HandOverSpot()
    {
        var head = Head;
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        return head.position + forward.normalized * handOverDistance - Vector3.up * handOverDrop;
    }

    void Fetch()
    {
        var p = fetched;
        var t = p.transform;
        if (t == null || held == p)
        {
            DropFetch();  // you grabbed it: yours now
            return;
        }
        fetchT = Mathf.Min(1f, fetchT + Time.deltaTime / Mathf.Max(fetchSeconds, 0.01f));
        bool riding = fetchCarrier != null;
        float e = ForgeTween.EaseOutCubic(fetchT);
        t.rotation = Quaternion.SlerpUnclamped(fetchFromRotation, riding ? fetchCarrier.rotation * carryRotation : fetchFromRotation, e);
        Vector3 centre = Vector3.LerpUnclamped(fetchFrom, riding ? fetchCarrier.position : handOverAt, e);
        t.position += centre - t.TransformPoint(p.centre);
        if (fetchT >= 1f && !riding) fetched = null;  // handed over: it stays where it is
    }

    // Albie isn't carrying it any more (you grabbed it, Reassemble, a new version): the pet hears it as put back.
    void DropFetch()
    {
        var p = fetched;
        bool riding = fetchCarrier != null;
        fetched = null;
        fetchCarrier = null;
        if (p != null && riding && session != null) session.ReportPartPutBack(p.id);
    }

    // Grab

    public void BeginGrab(CadPartHandle handle, CadPointer by, Vector3 point)
    {
        if (held != null || by == null || !viewer.OnStage || viewer.DemoRunning) return;  // a demo is moving the parts
        var p = pieces.Find(x => x.handle == handle);
        if (p == null || p.transform == null) return;
        if (p.homing != null)
        {
            StopCoroutine(p.homing);  // caught on its way home
            p.homing = null;
        }
        held = last = p;
        pointer = by;
        turning = false;
        p.off = true;
        viewer.SetDetached(p.transform, true);
        grabLocal = p.transform.InverseTransformPoint(point);
        Rebase(by.Pose);
        session.SetPartsOff(CountOff());
    }

    // Remember the held part's pose relative to the pointer; it keeps that pose as the pointer moves and turns.
    void Rebase(Pose pose)
    {
        var inverse = Quaternion.Inverse(pose.rotation);
        var t = held.transform;
        heldPosition = inverse * (t.position - pose.position);
        heldRotation = inverse * t.rotation;
        heldGrab = inverse * (t.TransformPoint(grabLocal) - pose.position);
    }

    void LateUpdate()
    {
        if (pieces.Count == 0) return;
        if (held != null) Hold();
        if (fetched != null) Fetch();
        Piece target = held ?? Pointed() ?? fetched;
        viewer.Highlight(target?.id);
        ShowCard(target ?? (last != null && last.off ? last : null), held != null);
    }

    Piece Pointed()
    {
        for (int i = 0; i < pieces.Count; i++)
            if (pieces[i].handle != null && pieces[i].handle.Hovered) return pieces[i];
        return null;
    }

    void Hold()
    {
        var t = held.transform;
        if (t == null || !pointer.Held)
        {
            Drop();
            return;
        }
        Pose pose = pointer.Pose;
        var keys = Keyboard.current;
        if (pointer.IsMouse && keys != null && keys.shiftKey.isPressed)
        {
            Turn(t, pose);
            return;
        }
        turning = false;
        float notches = pointer.Notches(Time.deltaTime);
        if (notches != 0f)
        {
            // Push away / pull in along the line from the pointer to the grabbed point.
            float d = heldGrab.magnitude;
            float next = Mathf.Clamp(d + notches * pushPerNotch, MinHold, MaxHold);
            Vector3 shift = heldGrab / Mathf.Max(d, 1e-4f) * (next - d);
            heldGrab += shift;
            heldPosition += shift;
        }
        t.SetPositionAndRotation(pose.position + pose.rotation * heldPosition, pose.rotation * heldRotation);
    }

    // Mouse + Shift: the part stays put and turns about its centre, like dragging its near side.
    void Turn(Transform t, Pose pose)
    {
        Vector3 direction = pose.forward;
        if (!turning)
        {
            turning = true;
            turnDirection = direction;
        }
        Angles(turnDirection, out float yaw0, out float pitch0);
        Angles(direction, out float yaw1, out float pitch1);
        turnDirection = direction;
        Vector3 centre = t.TransformPoint(held.centre);
        Vector3 across = Vector3.Cross(Vector3.up, centre - pose.position);
        if (across.sqrMagnitude < 1e-6f) across = Vector3.right;
        t.RotateAround(centre, Vector3.up, -Mathf.DeltaAngle(yaw0, yaw1) * turnGain);
        t.RotateAround(centre, across.normalized, -Mathf.DeltaAngle(pitch0, pitch1) * turnGain);
        Rebase(pose);  // carrying picks up from here when Shift is let go
    }

    void Drop()
    {
        var p = held;
        held = null;
        pointer = null;
        turning = false;
        if (p.transform != null && NearHome(p)) SendHome(p, instant: false);
        session.SetPartsOff(CountOff());
    }

    bool NearHome(Piece p)
    {
        var t = p.transform;
        if (!viewer.TryGetSlot(t, out Vector3 slot)) return false;
        Vector3 home = t.parent.TransformPoint(slot + p.homeRotation * Vector3.Scale(t.localScale, p.centre));
        float reach = Mathf.Min(snapDistance * viewer.Holder.lossyScale.x, maxSnapWorld);
        return Vector3.Distance(t.TransformPoint(p.centre), home) <= reach;
    }

    void SendHome(Piece p, bool instant)
    {
        if (p.homing != null) StopCoroutine(p.homing);
        p.homing = null;
        p.off = false;
        var t = p.transform;
        if (t == null) return;
        if (!viewer.TryGetSlot(t, out Vector3 slot)) slot = t.localPosition;
        Quaternion rotation = p.homeRotation;
        if (instant || !isActiveAndEnabled)
        {
            t.localPosition = slot;
            t.localRotation = rotation;
            viewer.SetDetached(t, false);
            return;
        }
        Vector3 p0 = t.localPosition;
        Quaternion r0 = t.localRotation;
        p.homing = StartCoroutine(ForgeTween.Run(homeSeconds, 0f, k =>
        {
            if (t == null) return;
            float e = ForgeTween.EaseOutCubic(k);
            t.localPosition = Vector3.LerpUnclamped(p0, slot, e);
            t.localRotation = Quaternion.SlerpUnclamped(r0, rotation, e);
        }, () =>
        {
            p.homing = null;
            if (t != null) viewer.SetDetached(t, false);
        }));
    }

    int CountOff()
    {
        int n = 0;
        foreach (var p in pieces)
            if (p.off) n++;
        return n;
    }

    // The part's mesh bounds in its own space (skipping the viewer's outline layers).
    static bool PartBounds(Transform t, out Bounds b)
    {
        b = default;
        bool first = true;
        var toPart = t.worldToLocalMatrix;
        foreach (var filter in t.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null || filter.name.StartsWith(LayerPrefix, System.StringComparison.Ordinal)) continue;
            var m = toPart * filter.transform.localToWorldMatrix;
            var mb = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                else b.Encapsulate(p);
            }
        }
        return !first;
    }

    static void Angles(Vector3 dir, out float yaw, out float pitch)
    {
        dir.Normalize();
        yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    // Card: name, material, mass (metadata.json), beside the part as seen from the eyes

    void ShowCard(Piece p, bool holding)
    {
        if (p == null || p.transform == null || model == null)
        {
            if (card != null && card.Alpha > 0f)
            {
                card.Hide();
                cardChip.Hide();
            }
            carded = null;
            return;
        }
        if (card == null)
        {
            card = new CadLabel(transform, "CadPartCard", CadLabel.Style.Read);
            card.SetAnchor(1);  // the chip reads rightward from its point
            cardChip = new CadChips("CadPartCardChip", transform);
        }
        bool moved = p != carded;
        if (moved || holding != cardHolding)
        {
            carded = p;
            cardHolding = holding;
            card.SetRaw(CardText(p, holding));
        }
        var head = Head;
        Vector3 centre = p.transform.TransformPoint(p.centre);
        Vector3 toPart = centre - head.position;
        Vector3 right = Vector3.Cross(Vector3.up, toPart).normalized;
        Vector3 extents = p.handle != null ? p.handle.Shape.bounds.extents : Vector3.one * 0.03f;
        float side = Mathf.Abs(extents.x * right.x) + Mathf.Abs(extents.z * right.z);  // the part's half-width seen from here
        Vector3 at = centre + right * (side + 0.02f) - toPart.normalized * 0.04f;
        card.MoveTo(at, head.position, Time.deltaTime);
        card.SetAlpha(1f);
        cardChip.Begin();
        card.Draw(cardChip);
        cardChip.End();
    }

    string CardText(Piece p, bool holding)
    {
        JObject o = model.Metadata.GetObject(p.id);
        string name = Pretty((string)o?["name"] ?? p.id);
        string facts = o?["material"] is JObject material ? (string)material["name"] : "";
        double? kg = (double?)o?["mass"];
        if (kg.HasValue) facts += (facts.Length > 0 ? " · " : "") + (kg.Value < 1.0 ? $"{kg.Value * 1000.0:0.#} g" : $"{kg.Value:0.##} kg");
        string hint = holding ? (pointer != null && pointer.IsMouse ? MouseHint : pointer != null && pointer.IsHand ? HandHint : RayHint) + "\nlet go near its place to put it back"
                    : p.off ? "grab to move it" : "grab to take it off";
        return "<b>" + name + "</b>" + (facts.Length > 0 ? "\n<size=82%><alpha=#B8>" + facts : "") + "\n<size=72%><alpha=#8C>" + hint;
    }

    static string Pretty(string id)
    {
        string s = id.Replace('_', ' ');
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
    }
}
