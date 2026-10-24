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
        NavMeshDataInstance dataInstance;
        NavMeshData data;

        public bool IsBuilt { get; private set; }
        /// <summary>World AABB of the walkable box (its max.y is the walkable top).</summary>
        public Bounds WorldBounds { get; private set; }
        /// <summary>World position of the center of the walkable top face.</summary>
        public Vector3 TopCenter { get; private set; }
        /// <summary>Yaw of the rectangle (local X = size.x, local Z = size.y).</summary>
        public Quaternion Rotation { get; private set; } = Quaternion.identity;
        /// <summary>Walkable rectangle size (m).</summary>
        public Vector2 Size { get; private set; }

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
            Vector3 up = Vector3.up;
            Vector3 slabSize = new Vector3(size.x, SlabThickness, size.y);

            // Walkable slab: centered half a thickness below the top so its upper face == topCenter.y.
            sources.Clear();
            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Box,
                transform = Matrix4x4.TRS(topCenter - up * (SlabThickness * 0.5f), yaw, Vector3.one),
                size = slabSize,
                area = WalkableArea
            });

            // Build bounds are in the local space of (topCenter, yaw).
            Quaternion inv = Quaternion.Inverse(yaw);
            Bounds local = new Bounds(new Vector3(0f, -SlabThickness * 0.5f, 0f), slabSize);
            if (holes != null)
            {
                for (int i = 0; i < holes.Count; i++)
                {
                    Bounds h = holes[i];
                    if (h.size.sqrMagnitude < 1e-6f) continue;
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
            }
            local.Expand(2f);   // +1 m on every side

            data = NavMeshBuilder.BuildNavMeshData(Settings, sources, local, topCenter, yaw);
            if (data == null) { Debug.LogWarning("[GatorNavSurface] NavMesh build failed for " + name, this); return; }
            dataInstance = NavMesh.AddNavMeshData(data);

            TopCenter = topCenter;
            Rotation = yaw;
            Size = size;
            WorldBounds = ComputeWorldBounds(topCenter, yaw, slabSize);
            IsBuilt = true;
            if (!s_surfaces.Contains(this)) s_surfaces.Add(this);
            if (role == Role.Desk) Desk = this;
            else if (role == Role.Floor) Floor = this;
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
