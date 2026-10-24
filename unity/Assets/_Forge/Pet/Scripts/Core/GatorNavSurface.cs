using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// One walkable rectangle (desk top, room floor) baked at runtime with the built-in
    /// <see cref="NavMeshBuilder"/> from primitive BOX sources only: no scene meshes, no baked
    /// assets, no AI Navigation package. See docs/PET_AGENT_CONTRACT.md Addendum A.
    ///
    /// The box-source trick: NavMeshBuilder voxelizes solid primitives and keeps the walkable
    /// top faces. A thin (0.1 m) walkable box whose TOP face sits exactly at the requested height
    /// therefore yields a NavMesh rectangle at that height. Each "hole" is another box marked with
    /// area 1 (Not Walkable): wherever it overlaps the floor the highest span belongs to the hole,
    /// so the agent cannot stand there, and the walkable mesh is eroded around it by the agent radius.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorNavSurface : MonoBehaviour
    {
        public enum Role { None, Desk, Floor }

        const float SlabThickness = 0.1f;
        const float VerticalTolerance = 0.3f;
        const int WalkableArea = 0, NotWalkableArea = 1;

        [Header("Optional self-build (sandbox / scene-authored surfaces)")]
        [SerializeField, Tooltip("Build at Start from this transform: top center = position, yaw = rotation about world up.")]
        bool buildOnStart;
        [SerializeField, Tooltip("Size (m) of the walkable rectangle for buildOnStart: x = local X, y = local Z.")]
        Vector2 startSize = new Vector2(1f, 1f);
        [SerializeField, Tooltip("Register as GatorNavSurface.Desk / .Floor when built (any builder, not only buildOnStart).")]
        Role role = Role.None;
        [SerializeField, Tooltip("Unwalkable world-space boxes for buildOnStart (e.g. furniture on a floor).")]
        Bounds[] startHoles = new Bounds[0];

        // ---------------------------------------------------------------- static registry / settings

        static readonly List<GatorNavSurface> s_surfaces = new List<GatorNavSurface>(8);
        static bool s_hasSettings;
        static NavMeshBuildSettings s_settings;

        /// <summary>Convenience slot: the desk surface (set by whoever builds it).</summary>
        public static GatorNavSurface Desk { get; set; }
        /// <summary>Convenience slot: the floor surface (set by whoever builds it).</summary>
        public static GatorNavSurface Floor { get; set; }

        /// <summary>Agent type for the gator, created once via NavMesh.CreateSettings().</summary>
        public static int GatorAgentTypeId => Settings.agentTypeID;

        static NavMeshBuildSettings Settings
        {
            get
            {
                if (!s_hasSettings)
                {
                    s_settings = NavMesh.CreateSettings();
                    s_settings.agentRadius = 0.06f;   // ~0.12 m tall gator (GatorSetup.TargetHeight); keep in sync with GatorSetup.AgentRadius
                    s_settings.agentHeight = 0.2f;
                    s_settings.agentClimb = 0.05f;
                    s_settings.agentSlope = 45f;
                    // Default minRegionArea (~2 m^2) would cull a whole desk top (~0.3 m^2 after radius erosion).
                    s_settings.minRegionArea = 0.02f;
                    s_hasSettings = true;     // voxelSize / tileSize: defaults (derived from radius)
                }
                return s_settings;
            }
        }

        /// <summary>Which registered, built surface contains worldPoint (XZ inside, Y within 0.3 m). Closest top wins.</summary>
        public static bool TryGetSurfaceAt(Vector3 worldPoint, out GatorNavSurface s)
        {
            s = null;
            float best = float.MaxValue;
            for (int i = 0; i < s_surfaces.Count; i++)
            {
                GatorNavSurface c = s_surfaces[i];
                if (c == null || !c.IsBuilt) continue;
                Bounds b = c.WorldBounds;
                if (worldPoint.x < b.min.x || worldPoint.x > b.max.x || worldPoint.z < b.min.z || worldPoint.z > b.max.z) continue;
                if (worldPoint.y < b.min.y - VerticalTolerance || worldPoint.y > b.max.y + VerticalTolerance) continue;
                if (!c.ContainsXZ(worldPoint)) continue;      // exact test for rotated rectangles
                float dy = Mathf.Abs(worldPoint.y - b.max.y);
                if (dy < best) { best = dy; s = c; }
            }
            return s != null;
        }

        // ---------------------------------------------------------------- instance

        readonly List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>(16);
        readonly List<Bounds> holeList = new List<Bounds>(8);        // non-empty holes of the last build request
        static readonly List<Bounds> s_holeScratch = new List<Bounds>(8);
        NavMeshDataInstance dataInstance;
        NavMeshData data;
        // Last requested box (remembered even if the bake failed) so Rebuild() can re-bake it with new holes.
        bool hasRequest;
        Vector3 reqTopCenter;
        Quaternion reqYaw = Quaternion.identity;
        Vector2 reqSize;

        public bool IsBuilt { get; private set; }
        /// <summary>World AABB of the walkable box (its max.y is the walkable top).</summary>
        public Bounds WorldBounds { get; private set; }
        /// <summary>World position of the center of the walkable top face.</summary>
        public Vector3 TopCenter { get; private set; }
        /// <summary>Yaw of the rectangle (local X = size.x, local Z = size.y).</summary>
        public Quaternion Rotation { get; private set; } = Quaternion.identity;
        /// <summary>Walkable rectangle size (m).</summary>
        public Vector2 Size { get; private set; }
        /// <summary>Unwalkable world AABBs (NavMesh area 1) of the current bake. Do not cache across Rebuild().</summary>
        public IReadOnlyList<Bounds> Holes => holeList;

        void Start()
        {
            if (buildOnStart && !IsBuilt)
            {
                Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
                BuildBox(transform.position, yaw, startSize, startHoles);
            }
        }

        /// <summary>Bake a walkable rectangle whose top face is at topCenter; holes are unwalkable world boxes.</summary>
        public void BuildBox(Vector3 topCenter, Quaternion yaw, Vector2 size, IList<Bounds> holes = null)
        {
            Clear();
            size.x = Mathf.Max(0.05f, size.x);
            size.y = Mathf.Max(0.05f, size.y);
            hasRequest = true;
            reqTopCenter = topCenter; reqYaw = yaw; reqSize = size;
            SetHoleList(holes);
            Bounds local = PrepareSources(topCenter, yaw, size);

            data = NavMeshBuilder.BuildNavMeshData(Settings, sources, local, topCenter, yaw);
            if (data == null) { Debug.LogWarning("[GatorNavSurface] NavMesh build failed for " + name, this); return; }
            dataInstance = NavMesh.AddNavMeshData(data);

            TopCenter = topCenter;
            Rotation = yaw;
            Size = size;
            WorldBounds = ComputeWorldBounds(topCenter, yaw, new Vector3(size.x, SlabThickness, size.y));
            IsBuilt = true;
            if (!s_surfaces.Contains(this)) s_surfaces.Add(this);
            if (role == Role.Desk) Desk = this;
            else if (role == Role.Floor) Floor = this;
        }

        /// <summary>
        /// Re-bake the same box (last BuildBox topCenter / yaw / size) with new holes, keeping the registry slot.
        /// Updates the existing NavMeshData in place so agents standing on it stay bound; falls back to a full BuildBox.
        /// </summary>
        public void Rebuild(IList<Bounds> holes)
        {
            if (!hasRequest) { Debug.LogWarning("[GatorNavSurface] Rebuild before any BuildBox on " + name, this); return; }
            if (IsBuilt && data != null)
            {
                SetHoleList(holes);
                Bounds local = PrepareSources(reqTopCenter, reqYaw, reqSize);
                if (NavMeshBuilder.UpdateNavMeshData(data, Settings, sources, local)) return;
                Debug.LogWarning("[GatorNavSurface] In-place NavMesh update failed for " + name + "; rebuilding.", this);
            }
            // BuildBox clears first; copy in case the caller passed something derived from our own list.
            s_holeScratch.Clear();
            if (holes != null) for (int i = 0; i < holes.Count; i++) s_holeScratch.Add(holes[i]);
            BuildBox(reqTopCenter, reqYaw, reqSize, s_holeScratch);
            s_holeScratch.Clear();
        }

        /// <summary>True if p lies inside one of this surface's holes (XZ test, Y within the 0.3 m surface tolerance).</summary>
        public bool IsInsideHole(Vector3 p, out Bounds hole)
        {
            hole = default;
            if (!IsBuilt) return false;
            for (int i = 0; i < holeList.Count; i++)
            {
                Bounds h = holeList[i];
                if (p.x < h.min.x || p.x > h.max.x || p.z < h.min.z || p.z > h.max.z) continue;
                if (p.y < h.min.y - VerticalTolerance || p.y > h.max.y + VerticalTolerance) continue;
                hole = h;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Keep-out box at the rectangle's edge nearest `viewer` (edge whose midpoint is closest in XZ): centered on that
        /// edge's midpoint, `width` along the edge, `depth` into the rectangle (plus 5 cm outward past the edge), height
        /// top +/- 0.3 m. `extra` (if hasExtra) is unioned in. Then clamped so at least `minSideStrip` stays free on both
        /// ends of the edge and behind the box. Returns the world AABB; false if the rectangle is too small for any box.
        /// </summary>
        public static bool ComputeEdgeKeepOut(Vector3 topCenter, Quaternion yaw, Vector2 size, Vector3 viewer,
            float width, float depth, float minSideStrip, Bounds extra, bool hasExtra, out Bounds hole, out bool shrunk)
        {
            hole = default; shrunk = false;
            Quaternion inv = Quaternion.Inverse(yaw);
            Vector3 v = inv * (viewer - topCenter);
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            float dpx = Sq(v.x - hx) + Sq(v.z), dnx = Sq(v.x + hx) + Sq(v.z);
            float dpz = Sq(v.x) + Sq(v.z - hz), dnz = Sq(v.x) + Sq(v.z + hz);
            bool alongX = Mathf.Min(dpz, dnz) <= Mathf.Min(dpx, dnx);        // edge runs along local X (a +/-Z edge)
            float sign = alongX ? (dpz <= dnz ? 1f : -1f) : (dpx <= dnx ? 1f : -1f);
            float halfLen = alongX ? hx : hz, fullDepth = alongX ? size.y : size.x, edge = alongX ? hz : hx;

            // Edge frame: a = along the edge (0 at its midpoint), d = inward from the edge (0 on the edge).
            float aMin = -width * 0.5f, aMax = width * 0.5f, dMin = -0.05f, dMax = depth;
            if (hasExtra)
            {
                Vector3 e = extra.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 l = inv * (extra.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z) - topCenter);
                    float a = alongX ? l.x : l.z, d = edge - sign * (alongX ? l.z : l.x);
                    aMin = Mathf.Min(aMin, a); aMax = Mathf.Max(aMax, a);
                    dMin = Mathf.Min(dMin, d); dMax = Mathf.Max(dMax, d);
                }
            }
            float aLim = halfLen - minSideStrip, dLim = fullDepth - minSideStrip;
            if (aMin < -aLim) { aMin = -aLim; shrunk = true; }
            if (aMax > aLim) { aMax = aLim; shrunk = true; }
            if (dMax > dLim) { dMax = dLim; shrunk = true; }
            if (aMax - aMin < 0.02f || dMax - dMin < 0.02f) return false;

            for (int i = 0; i < 4; i++)
            {
                float a = (i & 1) == 0 ? aMin : aMax, d = (i & 2) == 0 ? dMin : dMax;
                float n = sign * (edge - d);
                Vector3 w = topCenter + yaw * (alongX ? new Vector3(a, 0f, n) : new Vector3(n, 0f, a));
                if (i == 0) hole = new Bounds(w, Vector3.zero); else hole.Encapsulate(w);
            }
            hole.size = new Vector3(hole.size.x, 2f * VerticalTolerance, hole.size.z);
            return true;
        }

        /// <summary>Remove the baked NavMesh (keeps the Desk/Floor slot; check IsBuilt).</summary>
        public void Clear()
        {
            if (dataInstance.valid) dataInstance.Remove();
            dataInstance = default;
            if (data != null) { if (Application.isPlaying) Destroy(data); else DestroyImmediate(data); }
            data = null;
            IsBuilt = false;
            s_surfaces.Remove(this);
        }

        /// <summary>Closest point on the walkable top face (exact for the rotated rectangle), pulled `inset` m inward.</summary>
        public Vector3 ClosestPoint(Vector3 worldPoint, float inset = 0f)
        {
            Vector3 l = Quaternion.Inverse(Rotation) * (worldPoint - TopCenter);
            float hx = Mathf.Max(0f, Size.x * 0.5f - inset), hz = Mathf.Max(0f, Size.y * 0.5f - inset);
            l.x = Mathf.Clamp(l.x, -hx, hx);
            l.z = Mathf.Clamp(l.z, -hz, hz);
            l.y = 0f;
            return TopCenter + Rotation * l;
        }

        /// <summary>True if worldPoint's XZ lies inside the rotated rectangle.</summary>
        public bool ContainsXZ(Vector3 worldPoint)
        {
            Vector3 l = Quaternion.Inverse(Rotation) * (worldPoint - TopCenter);
            return Mathf.Abs(l.x) <= Size.x * 0.5f && Mathf.Abs(l.z) <= Size.y * 0.5f;
        }

        void OnDestroy()
        {
            Clear();
            if (Desk == this) Desk = null;
            if (Floor == this) Floor = null;
        }

        // ---------------------------------------------------------------- helpers

        static float Sq(float x) => x * x;

        void SetHoleList(IList<Bounds> holes)
        {
            if (ReferenceEquals(holes, holeList)) return;
            holeList.Clear();
            if (holes == null) return;
            for (int i = 0; i < holes.Count; i++)
                if (holes[i].size.sqrMagnitude >= 1e-6f) holeList.Add(holes[i]);
        }

        // Fills `sources` (walkable slab + one Not Walkable box per hole) and returns the build bounds in the local
        // space of (topCenter, yaw).
        Bounds PrepareSources(Vector3 topCenter, Quaternion yaw, Vector2 size)
        {
            Vector3 slabSize = new Vector3(size.x, SlabThickness, size.y);
            // Walkable slab: centered half a thickness below the top so its upper face == topCenter.y.
            sources.Clear();
            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                transform = Matrix4x4.TRS(topCenter - Vector3.up * (SlabThickness * 0.5f), yaw, Vector3.one),
                size = slabSize,
                area = WalkableArea
            });
            Quaternion inv = Quaternion.Inverse(yaw);
            Bounds local = new Bounds(new Vector3(0f, -SlabThickness * 0.5f, 0f), slabSize);
            for (int i = 0; i < holeList.Count; i++)
            {
                Bounds h = holeList[i];
                // Axis-aligned world box marked Not Walkable.
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    transform = Matrix4x4.TRS(h.center, Quaternion.identity, Vector3.one),
                    size = h.size,
                    area = NotWalkableArea
                });
                EncapsulateRotated(ref local, h, topCenter, inv);
            }
            local.Expand(2f);   // +1 m on every side
            return local;
        }

        static Bounds ComputeWorldBounds(Vector3 topCenter, Quaternion yaw, Vector3 slabSize)
        {
            Vector3 c = topCenter - Vector3.up * (slabSize.y * 0.5f);
            Vector3 ex = yaw * new Vector3(slabSize.x * 0.5f, 0f, 0f);
            Vector3 ez = yaw * new Vector3(0f, 0f, slabSize.z * 0.5f);
            Vector3 ext = new Vector3(Mathf.Abs(ex.x) + Mathf.Abs(ez.x), slabSize.y * 0.5f, Mathf.Abs(ex.z) + Mathf.Abs(ez.z));
            return new Bounds(c, ext * 2f);
        }

        static void EncapsulateRotated(ref Bounds local, Bounds world, Vector3 origin, Quaternion inv)
        {
            Vector3 e = world.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = world.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                local.Encapsulate(inv * (corner - origin));
            }
        }

        // Support "Enter Play Mode Options" with domain reload disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_surfaces.Clear();
            s_hasSettings = false;
            Desk = null;
            Floor = null;
        }
    }
}
