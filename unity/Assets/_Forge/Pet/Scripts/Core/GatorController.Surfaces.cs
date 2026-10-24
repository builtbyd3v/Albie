using System;
using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// GatorController walkable surfaces (desk / floor GatorNavSurface) and the desk hop
    /// (third part of the partial class). See docs/PET_AGENT_CONTRACT.md Addendum A.
    /// </summary>
    public partial class GatorController
    {
        [Header("Surfaces / hop")]
        [SerializeField, Tooltip("Duration (s) of a hop between surfaces.")]
        float hopSeconds = 0.6f;
        [SerializeField, Tooltip("On the desk: stand this far (m) inside the edge nearest the user.")]
        float deskEdgeInset = 0.15f;
        [SerializeField, Tooltip("On the desk: Thinking/Working come closer if farther than this (m) from the desk point nearest the user.")]
        float deskNearDistance = 0.35f;
        [SerializeField, Tooltip("HopToFloor: horizontal distances (m) past the desk edge tried as landing points.")]
        Vector3 floorLandingOffsets = new Vector3(0.35f, 0.6f, 0.9f);

        const int WalkableMask = 1 << 0;

        /// <summary>The surface the gator walks on. Null = plain scene NavMesh / unknown.</summary>
        public GatorNavSurface CurrentSurface { get; private set; }

        bool hopping, hopFaceUser;
        float hopStartTime, hopDy, hopK, nextSurfaceCheck;
        Vector3 hopFrom, hopTo;
        Quaternion hopYaw;
        bool hopHasYaw;
        GatorNavSurface hopTarget;
        Action hopLanded;

        bool OnDesk => CurrentSurface != null && CurrentSurface.IsBuilt && CurrentSurface == GatorNavSurface.Desk;

        // ---------------------------------------------------------------- public API

        /// <summary>Wander inside surface (uniform XZ samples). Null = radius around the user/wanderCenter.</summary>
        public void SetWanderArea(GatorNavSurface surface)
        {
            CurrentSurface = surface;
            tripActive = false;
            needRepath = true;
        }

        /// <summary>Jump + parabolic arc to landing (agent disabled while airborne), then Warp + SetWanderArea(target).</summary>
        public void HopTo(GatorNavSurface target, Vector3 landing, Action onLanded = null)
        {
            if (hopping || agent == null || AnticsBusy) return;
            StandUp(false);                     // Jump leaves Sit/Lay by itself
            if (target != null && target.IsBuilt)
            {
                // Snap the landing onto the target's NavMesh (small radius so we don't pick another surface).
                NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
                if (NavMesh.SamplePosition(landing, out NavMeshHit hit, 0.3f, f)) landing = hit.position;
            }

            if (OnMesh) StopMoving();
            approachingUser = tripActive = false;
            PlayEmote("Jump");
            agent.enabled = false;              // the arc drives the transform directly

            hopFrom = transform.position;
            hopTo = landing;
            hopDy = hopTo.y - hopFrom.y;
            hopK = ArcCoefficient(hopDy, Mathf.Max(0.25f, hopDy + 0.2f));
            Vector3 flat = hopTo - hopFrom; flat.y = 0f;
            hopHasYaw = flat.sqrMagnitude > 0.0001f;
            if (hopHasYaw) hopYaw = Quaternion.LookRotation(flat);
            hopTarget = target;
            hopLanded = onLanded;
            hopFaceUser = false;
            hopStartTime = Time.time;
            hopping = true;
        }

        /// <summary>Hop onto GatorNavSurface.Desk at the edge nearest the user, then face them.</summary>
        public void HopToUserDesk()
        {
            if (hopping) return;
            GatorNavSurface desk = GatorNavSurface.Desk;
            if (desk == null || !desk.IsBuilt) { Debug.LogWarning("[GatorController] HopToUserDesk: no desk surface built.", this); return; }
            Transform head = PetUser.Head;
            Vector3 landing = head != null ? desk.ClosestPoint(head.position, deskEdgeInset) : desk.TopCenter;

            if (CurrentSurface == desk && OnMesh)
            {
                // Already up there: just walk to the near edge.
                if (MoveTo(landing, false)) { approachingUser = true; approachStartTime = Time.time; faceUser = true; }
                return;
            }
            HopTo(desk, landing);
            hopFaceUser = hopping;
        }

        /// <summary>Hop from the current surface (desk) down to GatorNavSurface.Floor, just past the edge nearest the user.</summary>
        public void HopToFloor()
        {
            if (hopping) return;
            GatorNavSurface floor = GatorNavSurface.Floor;
            if (floor == null || !floor.IsBuilt) { Debug.LogWarning("[GatorController] HopToFloor: no floor surface built.", this); return; }
            if (CurrentSurface == floor) return;

            NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
            float floorY = floor.TopCenter.y;
            GatorNavSurface src = CurrentSurface;
            if (src != null && src.IsBuilt)
            {
                Transform head = PetUser.Head;
                Vector3 edge = EdgeToward(src, head != null ? head.position : transform.position + transform.forward, out Vector3 outward);
                for (int i = 0; i < 3; i++)
                {
                    Vector3 p = edge + outward * floorLandingOffsets[i];
                    p.y = floorY;
                    if (NavMesh.SamplePosition(p, out NavMeshHit hit, 0.4f, f) && Mathf.Abs(hit.position.y - floorY) < 0.15f)
                    {
                        HopTo(floor, hit.position);
                        return;
                    }
                }
            }
            // Not on a known surface (or no clear spot by the edge): nearest floor point below us.
            Vector3 below = floor.ClosestPoint(transform.position, 0.2f);
            if (NavMesh.SamplePosition(below, out NavMeshHit h2, 1f, f)) below = h2.position;
            HopTo(floor, below);
        }

        // ---------------------------------------------------------------- internals

        void UpdateHop()
        {
            float t = Mathf.Clamp01((Time.time - hopStartTime) / Mathf.Max(0.05f, hopSeconds));
            Vector3 p = Vector3.Lerp(hopFrom, hopTo, t);
            p.y = hopFrom.y + hopDy * t + hopK * 4f * t * (1f - t);
            transform.position = p;
            if (hopHasYaw)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, hopYaw, turnSpeed * 2f * Time.deltaTime);
            if (t >= 1f) Land();
        }

        void Land()
        {
            hopping = false;
            transform.position = hopTo;
            if (agent != null) { agent.enabled = true; agent.Warp(hopTo); }
            SetWanderArea(hopTarget);
            StopMoving();
            nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
            if (hopFaceUser) faceUser = true;
            Action cb = hopLanded;
            hopLanded = null; hopTarget = null;
            cb?.Invoke();
        }

        // y(t) = y0 + d*t + k*4t(1-t); returns k so the curve's maximum is exactly `apex` above y0.
        // Solved from max = (d + 4k)^2 / 16k = apex (larger root; apex > d keeps the root real).
        static float ArcCoefficient(float d, float apex)
        {
            return (2f * apex - d + 2f * Mathf.Sqrt(Mathf.Max(0f, apex * (apex - d)))) * 0.25f;
        }

        // Uniform point inside the surface's rectangle, projected onto its NavMesh.
        bool TryPickSurfacePoint(GatorNavSurface s, out Vector3 point)
        {
            NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
            Vector2 size = s.Size;
            float minTrip = Mathf.Min(0.8f, 0.3f * Mathf.Min(size.x, size.y));
            for (int i = 0; i < 8; i++)
            {
                Vector3 l = new Vector3((UnityEngine.Random.value - 0.5f) * size.x, 0f, (UnityEngine.Random.value - 0.5f) * size.y);
                Vector3 p = s.TopCenter + s.Rotation * l;
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 0.3f, f) && FlatDistance(hit.position) > minTrip)
                {
                    point = hit.position;
                    return true;
                }
            }
            point = transform.position;
            return false;
        }

        // Point on the rectangle's boundary in the direction of worldPoint from its center.
        static Vector3 EdgeToward(GatorNavSurface s, Vector3 worldPoint, out Vector3 outward)
        {
            Vector3 l = Quaternion.Inverse(s.Rotation) * (worldPoint - s.TopCenter);
            l.y = 0f;
            if (l.sqrMagnitude < 0.0001f) l = Vector3.forward;
            float hx = s.Size.x * 0.5f, hz = s.Size.y * 0.5f;
            float k = Mathf.Min(Mathf.Abs(l.x) > 1e-4f ? hx / Mathf.Abs(l.x) : float.MaxValue,
                                Mathf.Abs(l.z) > 1e-4f ? hz / Mathf.Abs(l.z) : float.MaxValue);
            outward = s.Rotation * l.normalized;
            return s.TopCenter + s.Rotation * (l * k);
        }

        // Where "in front of the user" is: desk edge nearest the head on the desk, else frontDistance ahead.
        Vector3 UserGoal(Transform head) =>
            OnDesk ? CurrentSurface.ClosestPoint(head.position, deskEdgeInset)
                   : Ground(head.position) + FlatForward(head) * frontDistance;

        // Adopt the registered surface under the gator if nobody called SetWanderArea (throttled, no allocations).
        void AutoDetectSurface()
        {
            if (Time.time < nextSurfaceCheck) return;
            nextSurfaceCheck = Time.time + 0.5f;
            if (CurrentSurface != null && CurrentSurface.IsBuilt) return;
            if (OnMesh && GatorNavSurface.TryGetSurfaceAt(transform.position, out GatorNavSurface s)) CurrentSurface = s;
        }
    }
}
