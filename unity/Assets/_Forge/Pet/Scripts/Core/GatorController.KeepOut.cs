using System;
using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// GatorController desk keep-out (fifth part of the partial class). The desk GatorNavSurface carries an
    /// unwalkable hole over the laptop / DeskDock area at the edge nearest the user (built by GatorMrukAdapter or
    /// GatorSandboxNav). The NavMesh already excludes it; this part handles the cases the NavMesh can't:
    /// standing in a hole that appeared under the gator, or a roll ending inside it. The gator then runs (or, if it is
    /// off the NavMesh, scoots with a short hop) to the nearest desk point beside the hole, preferring left/right
    /// along the near edge, and resumes its state. Throttled to keepOutCheckInterval, no allocations per frame.
    /// </summary>
    public partial class GatorController
    {
        [Header("Desk keep-out")]
        [SerializeField, Tooltip("Seconds between 'am I inside a desk hole?' checks while on the desk.")]
        float keepOutCheckInterval = 0.5f;
        [SerializeField, Tooltip("Exit goals are placed this far (m) outside the hole before snapping onto the NavMesh.")]
        float keepOutExitMargin = 0.1f;
        [SerializeField, Tooltip("An evacuation trip gives up after this many seconds.")]
        float keepOutTimeout = 4f;

        bool evacuating, evacResumeApproach, evacReenterState, warnedNoExit;
        float nextKeepOutCheck, evacUntil;
        Action evacLanded;

        /// <summary>True while the gator stands inside a hole (keep-out) of the desk surface it is on.</summary>
        public bool IsInKeepOut => OnDesk && CurrentSurface.IsInsideHole(transform.position, out _);

        /// <summary>True during an evacuation trip (state logic paused until it arrives).</summary>
        public bool IsEvacuating => evacuating;

        /// <summary>
        /// If inside a desk hole: run to the nearest desk point outside it (hop if off the NavMesh), overriding
        /// wander / rest; the current state resumes on arrival. Returns true if an evacuation started.
        /// </summary>
        public bool EvacuateKeepOut()
        {
            if (hopping || AnticsBusy || agent == null || !OnDesk) return false;
            GatorNavSurface s = CurrentSurface;
            if (!s.IsInsideHole(transform.position, out Bounds hole)) return false;
            if (!TryFindKeepOutExit(s, hole, transform.position, out Vector3 exit))
            {
                if (!warnedNoExit) Debug.LogWarning("[GatorController] Inside the desk keep-out but no exit point found.", this);
                warnedNoExit = true;
                return false;
            }
            StandUp();
            evacResumeApproach |= approachingUser;
            tripActive = approachingUser = false;
            if (OnMesh && MoveTo(exit, true))
            {
                evacuating = true;
                evacUntil = Time.time + Mathf.Max(0.5f, keepOutTimeout);
                return true;
            }
            // Off the NavMesh (standing on the carved hole, e.g. after a roll): a short scoot-hop out.
            if (evacLanded == null) evacLanded = FinishEvacuation;
            HopTo(s, exit, evacLanded);
            return hopping;
        }

        // Called from Update (not hopping, no antic). True while an evacuation trip owns the gator.
        bool UpdateKeepOut()
        {
            if (evacuating)
            {
                if (!HasArrived() && Time.time < evacUntil && OnDesk) return true;
                FinishEvacuation();
            }
            if (Time.time < nextKeepOutCheck) return false;
            nextKeepOutCheck = Time.time + Mathf.Max(0.1f, keepOutCheckInterval);
            return OnDesk && EvacuateKeepOut() && evacuating;
        }

        void FinishEvacuation()
        {
            evacuating = false;
            needRepath = true;
            if (evacReenterState && State != GatorState.Intro) EnterState(State);   // after a roll: restore mood / facing
            else
            {
                StopMoving();
                nextActionTime = Time.time + UnityEngine.Random.Range(0.3f, 1f);   // Wandering: short pause, then a new trip
            }
            evacReenterState = false;
            if (evacResumeApproach) { evacResumeApproach = false; ComeToUserInternal(false); }
        }

        // From Antics.FinishAntics after a roll that stayed on the desk: evacuate first, re-enter the state afterwards.
        bool EvacuateKeepOutAfterRoll()
        {
            evacReenterState = true;
            if (EvacuateKeepOut()) return true;
            evacReenterState = false;
            return false;
        }

        /// <summary>p, or (if p is inside one of s's holes) the nearest desk point beside that hole. Used for desk goals near the user.</summary>
        Vector3 KeepOutSafePoint(GatorNavSurface s, Vector3 p)
        {
            if (s == null || !s.IsBuilt || !s.IsInsideHole(p, out Bounds hole)) return p;
            return TryFindKeepOutExit(s, hole, p, out Vector3 exit) ? exit : p;
        }

        // Two candidates beside the hole along the near edge's world axis (keeping `from`'s other coordinate), each
        // snapped onto the desk NavMesh; the one closer to the gator wins. Fallback: past the hole's inner face, clamped
        // onto the desk, then any desk NavMesh point near `from`.
        bool TryFindKeepOutExit(GatorNavSurface s, Bounds hole, Vector3 from, out Vector3 exit)
        {
            NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
            bool alongX = EdgeRunsAlongWorldX(s, hole);
            float m = keepOutExitMargin, best = float.MaxValue;
            exit = from;
            for (int i = 0; i < 2; i++)
            {
                Vector3 c = from;
                if (alongX) c.x = i == 0 ? hole.min.x - m : hole.max.x + m;
                else c.z = i == 0 ? hole.min.z - m : hole.max.z + m;
                if (!TrySampleDesk(s, c, 0.3f, f, out Vector3 hit)) continue;
                float d = FlatDistance(hit);
                if (d < best) { best = d; exit = hit; }
            }
            if (best < float.MaxValue) return true;

            Vector3 toCenter = s.TopCenter - hole.center, back = from;
            if (alongX) back.z = toCenter.z > 0f ? hole.max.z + m : hole.min.z - m;
            else back.x = toCenter.x > 0f ? hole.max.x + m : hole.min.x - m;
            if (TrySampleDesk(s, s.ClosestPoint(back, 0.05f), 0.6f, f, out exit)) return true;
            return TrySampleDesk(s, from, 1f, f, out exit);
        }

        // Desk NavMesh point near p that is on this desk (height + rectangle) and outside every hole.
        static bool TrySampleDesk(GatorNavSurface s, Vector3 p, float radius, NavMeshQueryFilter f, out Vector3 point)
        {
            point = p;
            p.y = s.TopCenter.y;
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, radius, f)) return false;
            if (Mathf.Abs(hit.position.y - s.TopCenter.y) > 0.1f || !s.ContainsXZ(hit.position)) return false;
            if (s.IsInsideHole(hit.position, out _)) return false;
            point = hit.position;
            return true;
        }

        // The hole hugs the desk edge it is nearest to; that edge's direction decides the world axis for "left/right".
        static bool EdgeRunsAlongWorldX(GatorNavSurface s, Bounds hole)
        {
            Vector3 l = Quaternion.Inverse(s.Rotation) * (hole.center - s.TopCenter);
            float gapX = s.Size.x * 0.5f - Mathf.Abs(l.x), gapZ = s.Size.y * 0.5f - Mathf.Abs(l.z);
            Vector3 dir = s.Rotation * (gapZ <= gapX ? Vector3.right : Vector3.forward);   // hugging a +/-Z edge -> runs along local X
            return Mathf.Abs(dir.x) >= Mathf.Abs(dir.z);
        }
    }
}
