using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// The ONLY pet script that references Meta types (MRUK). Turns the scanned room into
    /// GatorNavSurfaces: the desk top ("DeskNav") and the floor with furniture holes ("FloorNav"),
    /// then hops the gator onto the desk. See docs/PET_AGENT_CONTRACT.md Addendum A.
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
        [SerializeField, Tooltip("Hop onto the desk (edge nearest the user) once the room is processed.")]
        bool hopOnFound = true;
        [SerializeField, Tooltip("Seconds between building the surfaces and the hop.")]
        float hopDelay = 1.0f;
        [SerializeField, Tooltip("Shrink the desk walkable rectangle by this much (m) on every side.")]
        float deskInset = 0.03f;

        // Planes/meshes that are never floor obstacles.
        const MRUKAnchor.SceneLabels NotFurniture =
            MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.WALL_FACE |
            MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE |
            MRUKAnchor.SceneLabels.GLOBAL_MESH;

        readonly List<Bounds> holes = new List<Bounds>(32);
        GatorNavSurface deskNav, floorNav;
        Coroutine hopRoutine;
        DeskLocator hookedLocator;
        bool hookedMruk;

        void Awake()
        {
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (controller == null) Debug.LogWarning("[GatorMrukAdapter] No GatorController in scene.", this);
        }

        // Fallback wiring in Start (not Awake) so DeskLocator/MRUK singletons exist regardless of script order.
        void Start()
        {
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
            if (hookedLocator != null && hookedLocator.deskFound != null) hookedLocator.deskFound.RemoveListener(OnDeskFound);
            if (hookedMruk && MRUK.Instance != null) MRUK.Instance.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
        }

        // ---------------------------------------------------------------- public API

        /// <summary>Wire from DeskLocator.deskFound. Builds desk + floor surfaces, then (optionally) hops.</summary>
        public void OnDeskFound(MRUKAnchor desk)
        {
            if (desk == null) { Debug.LogWarning("[GatorMrukAdapter] OnDeskFound(null).", this); return; }
            BuildDesk(desk);
            MRUKRoom room = desk.Room != null ? desk.Room : (MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null);
            BuildFloor(room);

            if (controller == null) return;
            if (floorNav != null && floorNav.IsBuilt && controller.CurrentSurface != GatorNavSurface.Desk)
                controller.SetWanderArea(floorNav);
            if (hopOnFound && deskNav != null && deskNav.IsBuilt)
            {
                if (hopRoutine != null) StopCoroutine(hopRoutine);
                hopRoutine = StartCoroutine(HopAfterDelay());
            }
        }

        // ---------------------------------------------------------------- surfaces

        void BuildDesk(MRUKAnchor desk)
        {
            Transform t = desk.transform;
            Vector3 localTop;
            Vector2 size;
            if (desk.PlaneRect.HasValue)
            {
                Rect r = desk.PlaneRect.Value;                       // local X/Y, plane at local z = 0 (top)
                localTop = new Vector3(r.center.x, r.center.y, 0f);
                size = r.size;
            }
            else if (desk.VolumeBounds.HasValue)
            {
                Bounds v = desk.VolumeBounds.Value;                  // top face = max local z
                localTop = new Vector3(v.center.x, v.center.y, v.max.z);
                size = new Vector2(v.size.x, v.size.y);
            }
            else { Debug.LogWarning("[GatorMrukAdapter] Desk anchor has neither PlaneRect nor VolumeBounds.", this); return; }

            size -= Vector2.one * (2f * deskInset);
            Vector3 top = t.TransformPoint(localTop);
            EnsureSurface(ref deskNav, "DeskNav");
            deskNav.BuildBox(top, YawOf(t), size);
            if (!deskNav.IsBuilt) return;
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
