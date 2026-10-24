using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// The ONLY pet script that references Meta types (MRUK). Turns the scanned room into
    /// GatorNavSurfaces: the desk top ("DeskNav") and the floor with furniture holes ("FloorNav").
    /// See docs/PET_AGENT_CONTRACT.md Addendum A.
    /// Which table is the desk: with Core's DeskPresence in the scene (Main) the desk is DeskPresence.Table, the table the
    /// user actually walked up to (re-built whenever it changes: Near / Seated, a whistle on another table), and the hop is
    /// left to the ArrivalDirector. Without one (PetSandboxXR) the desk is DeskLocator.Desk / the TABLE nearest the user at
    /// room load, and hopOnFound hops the gator up after hopDelay, as before.
    /// Whistles: clones whistleTemplate onto every TABLE in the room (WhistleButton.SetTable); pressing one makes its
    /// table the desk (Pressing runs before GatorSummon hears the press, so the gator hops to THAT table).
    /// Keep-out holes on the desk NavMesh: the laptop / DeskDock box at the edge nearest the user, and (while the Messages
    /// cards float beside Bit) MessagesDeskZone.Footprint, so he never stands under the card stack.
    ///
    /// Wiring: DeskLocator.deskFound (UnityEvent&lt;MRUKAnchor&gt;) -> OnDeskFound. If nothing is wired,
    /// Start() hooks an unwired DeskLocator at runtime, or (no DeskLocator at all) subscribes to
    /// MRUK's scene-loaded callback itself and picks the TABLE nearest the user.
    ///
    /// MRUK conventions relied on (MRUKAnchor.cs, MRUK 207): a plane's normal is the anchor's local +Z
    /// (transform.forward); PlaneRect spans local X/Y; for volumes "anchor transform.position is at
    /// top of volume" (the volume extends along -forward). So for TABLE/FLOOR, forward == world up.
    /// </summary>
    public class GatorMrukAdapter : MonoBehaviour
    {
        [SerializeField, Tooltip("Gator to drive. Empty = first GatorController found at Awake.")]
        GatorController controller;
        [SerializeField, Tooltip("No DeskPresence in the scene (sandbox XR): hop onto the desk once the room is processed. With a DeskPresence the ArrivalDirector hops on Near.")]
        bool hopOnFound = true;
        [SerializeField, Tooltip("Seconds between building the surfaces and the hop.")]
        float hopDelay = 1.0f;
        [SerializeField, Tooltip("Shrink the desk walkable rectangle by this much (m) on every side.")]
        float deskInset = 0.03f;
        [SerializeField, Tooltip("Whistle cloned onto every TABLE (one per table). Empty = first WhistleButton in the scene; none = no whistles.")]
        WhistleButton whistleTemplate;

        [Header("Desk keep-out (laptop / mode buttons)")]
        [SerializeField, Tooltip("Carve an unwalkable box at the desk edge nearest the user (where the laptop and DeskDock buttons sit).")]
        bool keepOutEnabled = true;
        [SerializeField, Tooltip("Keep-out size along the near edge (m). Shrunk on narrow desks to keep minSideStrip free.")]
        float keepOutWidth = 0.6f;
        [SerializeField, Tooltip("Keep-out depth from the near edge into the desk (m).")]
        float keepOutDepth = 0.45f;
        [SerializeField, Tooltip("Walkable strip (m) always left free on each side of (and behind) the keep-out.")]
        float minSideStrip = 0.2f;
        [SerializeField, Tooltip("Margin (m) added around the DeskDock buttons when they are unioned into the keep-out.")]
        float dockMargin = 0.08f;
        [SerializeField, Tooltip("Seconds between checks whether the DeskDock moved / showed / hid (re-bakes the desk hole when it did).")]
        float dockPollSeconds = 1f;

        // Planes/meshes that are never floor obstacles.
        const MRUKAnchor.SceneLabels NotFurniture =
            MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.WALL_FACE |
            MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE |
            MRUKAnchor.SceneLabels.GLOBAL_MESH;

        readonly List<Bounds> holes = new List<Bounds>(32);
        readonly Dictionary<WhistleButton, MRUKAnchor> whistleTables = new Dictionary<WhistleButton, MRUKAnchor>();
        MRUKAnchor currentDesk;
        MRUKRoom whistleRoom;
        GatorNavSurface deskNav, floorNav;
        Coroutine hopRoutine;
        DeskLocator hookedLocator;
        DeskPresence presence;          // Core (Main): the desk is the table the user is at
        bool hookedMruk;

        // Keep-out state: viewer position captured at desk build time; dock footprint last baked into the hole.
        readonly List<Bounds> deskHoles = new List<Bounds>(2);
        readonly Vector3[] dockCorners = new Vector3[4];
        Vector3 keepOutViewer;
        bool hasKeepOutViewer, dockBaked, msgZoneBaked;
        Bounds dockBakedBounds, msgZoneBakedBounds;
        GameObject dockGo;
        float nextDockPoll;

        /// <summary>The desk surface this adapter built (null until a table is known).</summary>
        public GatorNavSurface DeskSurface => deskNav;

        void Awake()
        {
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (controller == null) Debug.LogWarning("[GatorMrukAdapter] No GatorController in scene.", this);
        }

        // Fallback wiring in Start (not Awake) so DeskLocator/MRUK singletons exist regardless of script order.
        void Start()
        {
            presence = FindAnyObjectByType<DeskPresence>();
            if (presence != null)
            {
                presence.stateChanged.AddListener(OnPresenceState);
                presence.frameChanged.AddListener(OnPresenceFrame);
                if (presence.AtDesk) SyncDeskToPresence();
            }

            DeskLocator locator = FindAnyObjectByType<DeskLocator>();
            if (locator != null)
            {
                if (IsWiredTo(locator)) return;
                if (locator.deskFound == null) locator.deskFound = new UnityEngine.Events.UnityEvent<MRUKAnchor>();
                locator.deskFound.AddListener(OnDeskFound);
                hookedLocator = locator;
                if (locator.Desk != null) OnDeskFound(locator.Desk);     // already fired
                return;
            }
            if (MRUK.Instance == null) { Debug.Log("[GatorMrukAdapter] No DeskLocator and no MRUK in the scene (desktop sandbox) — idle.", this); return; }
            MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
            hookedMruk = true;
        }

        void OnDestroy()
        {
            if (presence != null)
            {
                presence.stateChanged.RemoveListener(OnPresenceState);
                presence.frameChanged.RemoveListener(OnPresenceFrame);
            }
            if (hookedLocator != null && hookedLocator.deskFound != null) hookedLocator.deskFound.RemoveListener(OnDeskFound);
            if (hookedMruk && MRUK.Instance != null) MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
            foreach (var b in whistleTables.Keys) if (b != null) b.Pressing -= OnWhistlePressing;
        }

        // Re-bake the desk holes when Hudson's DeskDock appears, moves (user went round the desk) or hides, or when the
        // Messages cards start / stop floating over the desk. Throttled.
        void Update()
        {
            if (!keepOutEnabled || deskNav == null || !deskNav.IsBuilt || Time.time < nextDockPoll) return;
            nextDockPoll = Time.time + Mathf.Max(0.2f, dockPollSeconds);
            bool has = TryGetDockBounds(out Bounds b);
            bool changed = has != dockBaked || (has && (Vector3.Distance(b.center, dockBakedBounds.center) > 0.02f
                                                       || Vector3.Distance(b.size, dockBakedBounds.size) > 0.02f));
            bool hasMsg = TryGetMessagesZone(deskNav.TopCenter, deskNav.Rotation, deskNav.Size, out Bounds m);
            changed |= hasMsg != msgZoneBaked || (hasMsg && Vector3.Distance(m.center, msgZoneBakedBounds.center) > 0.02f);
            // No dock showing (user up and about): the keep-out follows the desk edge nearest the user *now*.
            // Re-bake only when the resulting box actually moves (walking around the desk), not every poll.
            if (!changed && !has)
            {
                Transform head = PetUser.Head;
                if (head != null)
                {
                    keepOutViewer = head.position;
                    hasKeepOutViewer = true;
                    if (GatorNavSurface.ComputeEdgeKeepOut(deskNav.TopCenter, deskNav.Rotation, deskNav.Size, keepOutViewer,
                            keepOutWidth, keepOutDepth, minSideStrip, default, false, out Bounds candidate, out _))
                        changed = deskHoles.Count == 0 || Vector3.Distance(candidate.center, deskHoles[0].center) > 0.05f;
                }
            }
            if (changed) RebuildKeepOut();   // the controller's keep-out check then scurries the gator out if it is inside
        }

        // ---------------------------------------------------------------- public API

        /// <summary>Recompute the desk keep-out (same viewer as at build time, current DeskDock footprint, Messages zone) and re-bake the desk.</summary>
        public void RebuildKeepOut()
        {
            if (deskNav == null || !deskNav.IsBuilt) return;
            ComputeDeskHoles(deskNav.TopCenter, deskNav.Rotation, deskNav.Size);
            deskNav.Rebuild(deskHoles);
        }

        /// <summary>
        /// Make the desk NavMesh the table DeskPresence says the user is at (built or moved as needed; a gator standing on
        /// the old desk is handed back to the floor so the next hop is a real hop). True if the desk surface is built for that
        /// table. Without a DeskPresence: true if some desk is built. Called by the ArrivalDirector before it hops.
        /// </summary>
        public bool SyncDeskToPresence()
        {
            if (presence == null) return deskNav != null && deskNav.IsBuilt;
            MRUKAnchor table = presence.Table;
            if (table == null) return false;
            if (table != currentDesk || deskNav == null || !deskNav.IsBuilt) SwitchDesk(table);
            return deskNav != null && deskNav.IsBuilt && currentDesk == table;
        }

        /// <summary>Wire from DeskLocator.deskFound. Builds desk + floor surfaces (+ whistles), then (sandbox) hops.</summary>
        public void OnDeskFound(MRUKAnchor desk)
        {
            if (desk == null) { Debug.LogWarning("[GatorMrukAdapter] OnDeskFound(null).", this); return; }
            // Main: the desk is the table the user is at. Until they walk up to one, the locator's pick (nearest at load)
            // stands in, so the egg and a hop have a desk; SyncDeskToPresence replaces it on Near.
            MRUKAnchor table = presence != null && presence.Table != null ? presence.Table : desk;
            if (table != currentDesk || deskNav == null || !deskNav.IsBuilt) BuildDesk(table);
            MRUKRoom room = table.Room != null ? table.Room : (MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null);
            BuildFloor(room);
            SpawnWhistles(room);

            if (controller == null) return;
            if (floorNav != null && floorNav.IsBuilt && controller.CurrentSurface != GatorNavSurface.Desk)
                controller.SetWanderArea(floorNav);
            if (hopOnFound && presence == null && deskNav != null && deskNav.IsBuilt)
            {
                if (hopRoutine != null) StopCoroutine(hopRoutine);
                hopRoutine = StartCoroutine(HopAfterDelay());
            }
        }

        // ---------------------------------------------------------------- DeskPresence (Main)

        void OnPresenceState(DeskState state)
        {
            if (state != DeskState.Away) SyncDeskToPresence();
        }

        // The user went round to another side: the dock moves with the front edge, so the keep-out follows right away.
        void OnPresenceFrame() => RebuildKeepOut();

        // Make `table` the desk (whistle on another table, DeskPresence picked one). The desk NavMesh moves from under a
        // gator standing on the old one: forget its surface so a HopToUserDesk hops (agent off) instead of walking.
        void SwitchDesk(MRUKAnchor table)
        {
            bool gatorWasOnDesk = controller != null && deskNav != null && deskNav.IsBuilt && controller.CurrentSurface == deskNav;
            BuildDesk(table);
            if (gatorWasOnDesk) controller.SetWanderArea(floorNav != null && floorNav.IsBuilt ? floorNav : null);
        }

        // ---------------------------------------------------------------- surfaces

        void BuildDesk(MRUKAnchor desk)
        {
            if (!TryGetTop(desk, out Vector3 top, out Quaternion yaw, out Vector2 size))
            { Debug.LogWarning("[GatorMrukAdapter] Desk anchor has neither PlaneRect nor VolumeBounds.", this); return; }

            size -= Vector2.one * (2f * deskInset);
            Transform head = PetUser.Head;
            hasKeepOutViewer = head != null;
            if (hasKeepOutViewer) keepOutViewer = head.position;
            ComputeDeskHoles(top, yaw, size);
            EnsureSurface(ref deskNav, "DeskNav");
            deskNav.BuildBox(top, yaw, size, deskHoles);
            if (!deskNav.IsBuilt) return;
            currentDesk = desk;
            GatorNavSurface.Desk = deskNav;
            Debug.Log("[GatorMrukAdapter] Desk surface " + size.x.ToString("F2") + " x " + size.y.ToString("F2") + " m at " + top, this);
        }

        void BuildFloor(MRUKRoom room)
        {
            if (room == null) { Debug.LogWarning("[GatorMrukAdapter] No room; floor surface skipped.", this); return; }
            MRUKAnchor floor = LargestFloor(room);
            if (floor == null || !floor.PlaneRect.HasValue) { Debug.LogWarning("[GatorMrukAdapter] Room has no floor plane.", this); return; }

            holes.Clear();
            List<MRUKAnchor> anchors = room.Anchors;
            for (int i = 0; i < anchors.Count; i++)
            {
                MRUKAnchor a = anchors[i];
                if (a == null || a.HasAnyLabel(NotFurniture) || !a.VolumeBounds.HasValue) continue;
                holes.Add(WorldAabb(a.transform, a.VolumeBounds.Value));
            }

            Rect r = floor.PlaneRect.Value;
            Vector3 top = floor.transform.TransformPoint(new Vector3(r.center.x, r.center.y, 0f));
            EnsureSurface(ref floorNav, "FloorNav");
            floorNav.BuildBox(top, YawOf(floor.transform), r.size, holes);
            if (!floorNav.IsBuilt) return;
            GatorNavSurface.Floor = floorNav;
            Debug.Log("[GatorMrukAdapter] Floor surface " + r.size.x.ToString("F2") + " x " + r.size.y.ToString("F2") + " m, " + holes.Count + " hole(s)", this);
        }

        // ---------------------------------------------------------------- whistles (one per table)

        void SpawnWhistles(MRUKRoom room)
        {
            if (room == null || room == whistleRoom) return;
            WhistleButton template = whistleTemplate != null ? whistleTemplate : FindAnyObjectByType<WhistleButton>();
            if (template == null) return;
            whistleRoom = room;
            bool first = true;
            List<MRUKAnchor> anchors = room.Anchors;
            for (int i = 0; i < anchors.Count; i++)
            {
                MRUKAnchor a = anchors[i];
                if (a == null || !a.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) continue;
                if (!TryGetTop(a, out Vector3 top, out Quaternion yaw, out Vector2 size)) continue;
                WhistleButton b = first ? template : Instantiate(template, template.transform.parent);
                b.name = "WhistleButton (" + a.name + ")";
                b.SetTable(top, yaw, size);
                b.Pressing += OnWhistlePressing;
                whistleTables[b] = a;
                first = false;
            }
            Debug.Log("[GatorMrukAdapter] " + whistleTables.Count + " whistle(s), one per table.", this);
        }

        // Runs before GatorSummon hears the press: make the pressed table the desk so the summon hops there.
        void OnWhistlePressing(WhistleButton b)
        {
            if (!whistleTables.TryGetValue(b, out MRUKAnchor table) || table == null || table == currentDesk) return;
            SwitchDesk(table);
        }

        // Standalone fallback: same selection as DeskLocator (TABLE nearest the user's head).
        void OnSceneLoaded()
        {
            MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room == null) { Debug.LogWarning("[GatorMrukAdapter] Scene loaded but no current room.", this); return; }
            Transform head = PetUser.Head;
            Vector3 from = head != null ? head.position : Vector3.zero;
            MRUKAnchor best = null;
            float bestSq = float.MaxValue;
            List<MRUKAnchor> anchors = room.Anchors;
            for (int i = 0; i < anchors.Count; i++)
            {
                MRUKAnchor a = anchors[i];
                if (a == null || !a.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) continue;
                float d = (a.GetAnchorCenter() - from).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = a; }
            }
            if (best != null) { OnDeskFound(best); return; }

            Debug.LogWarning("[GatorMrukAdapter] No TABLE in this room; floor only.", this);
            BuildFloor(room);
            if (controller != null && floorNav != null && floorNav.IsBuilt) controller.SetWanderArea(floorNav);
        }

        IEnumerator HopAfterDelay()
        {
            if (hopDelay > 0f) yield return new WaitForSeconds(hopDelay);
            hopRoutine = null;
            if (controller != null) controller.HopToUserDesk();
        }

        // ---------------------------------------------------------------- keep-out

        // deskHoles = [] or [keep-out box at the edge nearest the user (build-time head), unioned with the DeskDock]
        // (+ the Messages floating-card zone while cards float over the desk).
        void ComputeDeskHoles(Vector3 top, Quaternion yaw, Vector2 size)
        {
            deskHoles.Clear();
            dockBaked = TryGetDockBounds(out dockBakedBounds);
            msgZoneBaked = TryGetMessagesZone(top, yaw, size, out msgZoneBakedBounds);
            if (!keepOutEnabled) return;
            if (msgZoneBaked) deskHoles.Add(msgZoneBakedBounds);
            // A visible dock is used only if it lies on this desk top.
            Vector3 dl = Quaternion.Inverse(yaw) * (dockBakedBounds.center - top);
            bool useDock = dockBaked && Mathf.Abs(dl.y) < 0.3f
                           && Mathf.Abs(dl.x) < size.x * 0.5f + 0.1f && Mathf.Abs(dl.z) < size.y * 0.5f + 0.1f;
            if (!useDock && !hasKeepOutViewer)
            {
                Debug.LogWarning("[GatorMrukAdapter] No user head when the desk was built and no DeskDock; keep-out skipped.", this);
                return;
            }
            // Hudson lays the DeskDock on the edge the user is actually at, so when it exists it decides which edge is
            // the "front". The build-time head is only the fallback: from across the room it can pick a short end,
            // and unioning that with a dock on another edge produced one huge box spanning the desk.
            Vector3 viewer = useDock ? dockBakedBounds.center : keepOutViewer;
            if (!GatorNavSurface.ComputeEdgeKeepOut(top, yaw, size, viewer, keepOutWidth, keepOutDepth, minSideStrip,
                    dockBakedBounds, useDock, out Bounds hole, out bool shrunk))
            {
                Debug.LogWarning("[GatorMrukAdapter] Desk too small for a keep-out zone (" + size.x.ToString("F2") + " x " + size.y.ToString("F2") + " m).", this);
                return;
            }
            deskHoles.Add(hole);
            Debug.Log("[GatorMrukAdapter] Desk keep-out " + hole.size.x.ToString("F2") + " x " + hole.size.z.ToString("F2")
                      + " m at " + hole.center + (useDock ? " (incl. DeskDock)" : "")
                      + (shrunk ? " — shrunk to keep " + minSideStrip.ToString("F2") + " m strips free" : ""), this);
        }

        // World AABB of MessagesDeskZone.Footprint (desk-frame rect on the desk top) while Messages cards float beside Bit
        // and the desk frame lies on this desk. Height top +/- 0.3 m like the edge keep-out.
        bool TryGetMessagesZone(Vector3 top, Quaternion yaw, Vector2 size, out Bounds b)
        {
            b = default;
            if (MessagesDeskZone.Count <= 0 || presence == null || !presence.AtDesk || presence.Frame == null) return false;
            Transform frame = presence.Frame;
            Rect r = MessagesDeskZone.Footprint;
            Vector3 origin = frame.position;
            Vector3 dl = Quaternion.Inverse(yaw) * (origin - top);
            if (Mathf.Abs(dl.y) > 0.3f || Mathf.Abs(dl.x) > size.x * 0.5f + 0.1f || Mathf.Abs(dl.z) > size.y * 0.5f + 0.1f) return false;   // another table
            for (int i = 0; i < 4; i++)
            {
                Vector3 w = frame.TransformPoint(new Vector3((i & 1) == 0 ? r.xMin : r.xMax, 0f, (i & 2) == 0 ? r.yMin : r.yMax));
                if (i == 0) b = new Bounds(w, Vector3.zero); else b.Encapsulate(w);
            }
            b.center = new Vector3(b.center.x, top.y, b.center.z);
            b.size = new Vector3(b.size.x, 0.6f, b.size.z);
            return true;
        }

        // World AABB of Hudson's DeskDock (canvas rect, + dockMargin) while it is shown. Found by type, else by name.
        bool TryGetDockBounds(out Bounds b)
        {
            b = default;
            if (dockGo == null)
            {
                DeskDock dock = FindAnyObjectByType<DeskDock>(FindObjectsInactive.Include);
                dockGo = dock != null ? dock.gameObject : GameObject.Find("DeskDock");
                if (dockGo == null) return false;
            }
            if (!dockGo.activeInHierarchy) return false;
            if (dockGo.transform is RectTransform rt)
            {
                rt.GetWorldCorners(dockCorners);
                b = new Bounds(dockCorners[0], Vector3.zero);
                for (int i = 1; i < 4; i++) b.Encapsulate(dockCorners[i]);
            }
            else
            {
                Renderer r = dockGo.GetComponentInChildren<Renderer>();
                if (r == null) return false;
                b = r.bounds;
            }
            b.Expand(2f * dockMargin);
            return true;
        }

        // ---------------------------------------------------------------- helpers

        bool IsWiredTo(DeskLocator locator)
        {
            if (locator.deskFound == null) return false;
            for (int i = 0; i < locator.deskFound.GetPersistentEventCount(); i++)
                if (locator.deskFound.GetPersistentTarget(i) == this) return true;
            return false;
        }

        void EnsureSurface(ref GatorNavSurface s, string goName)
        {
            if (s != null) return;
            var go = new GameObject(goName);
            go.transform.SetParent(transform, false);
            s = go.AddComponent<GatorNavSurface>();
        }

        // Yaw about world up whose local X follows the anchor's X (PlaneRect x) and local Z its rect y axis
        // (up to sign, which is irrelevant for a centered rectangle). Valid for horizontal planes (forward == up).
        static Quaternion YawOf(Transform t)
        {
            Vector3 r = t.right; r.y = 0f;
            if (r.sqrMagnitude < 1e-4f) { r = t.up; r.y = 0f; }
            if (r.sqrMagnitude < 1e-4f) return Quaternion.identity;
            r.Normalize();
            return Quaternion.LookRotation(Vector3.Cross(r, Vector3.up), Vector3.up);
        }

        // World top-face centre, yaw and size of a TABLE anchor (plane rect, else volume top).
        static bool TryGetTop(MRUKAnchor a, out Vector3 top, out Quaternion yaw, out Vector2 size)
        {
            Transform t = a.transform;
            Vector3 localTop;
            if (a.PlaneRect.HasValue)
            {
                Rect r = a.PlaneRect.Value;                          // local X/Y, plane at local z = 0 (top)
                localTop = new Vector3(r.center.x, r.center.y, 0f);
                size = r.size;
            }
            else if (a.VolumeBounds.HasValue)
            {
                Bounds v = a.VolumeBounds.Value;                     // top face = max local z
                localTop = new Vector3(v.center.x, v.center.y, v.max.z);
                size = new Vector2(v.size.x, v.size.y);
            }
            else { top = default; yaw = Quaternion.identity; size = default; return false; }
            top = t.TransformPoint(localTop);
            yaw = YawOf(t);
            return true;
        }

        static MRUKAnchor LargestFloor(MRUKRoom room)
        {
            List<MRUKAnchor> floors = room.FloorAnchors;     // FloorAnchor is obsolete in MRUK 207
            MRUKAnchor best = null;
            float bestArea = -1f;
            for (int i = 0; floors != null && i < floors.Count; i++)
            {
                MRUKAnchor f = floors[i];
                if (f == null || !f.PlaneRect.HasValue) continue;
                Vector2 s = f.PlaneRect.Value.size;
                if (s.x * s.y > bestArea) { bestArea = s.x * s.y; best = f; }
            }
            return best;
        }

        static Bounds WorldAabb(Transform t, Bounds local)
        {
            Vector3 c = local.center, e = local.extents;
            Bounds w = new Bounds(t.TransformPoint(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
                w.Encapsulate(t.TransformPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return w;
        }
    }
}
