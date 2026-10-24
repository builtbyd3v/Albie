using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// GatorController territory (sixth part of the partial class): with several gators (GatorCrew) each one owns a
    /// rectangle of a surface (GatorTerritories splits the desk, or the floor, into equal walkable areas). Wander
    /// points, WanderAway and the idle pauses stay inside it; a gator that ends up elsewhere (after a summon, a fall,
    /// a hatch) walks or hops back. The desk keep-out still applies: territory points are rejected inside its holes.
    /// Crew gators (StayInTerritoryWhenBusy) think and work at the front of their own area instead of coming over.
    /// </summary>
    public partial class GatorController
    {
        GatorNavSurface territorySurface;
        Vector3 territoryCenter;
        Quaternion territoryRot = Quaternion.identity;
        Vector2 territorySize;
        float nextTerritoryReturn;

        /// <summary>A territory is assigned and its surface is built.</summary>
        public bool HasTerritory => territorySurface != null && territorySurface.IsBuilt;
        public GatorNavSurface TerritorySurface => territorySurface;
        /// <summary>Thinking / Working: stay at the front of the territory (crew) instead of ComeToUser (Bit).</summary>
        public bool StayInTerritoryWhenBusy { get; set; }

        // ---------------------------------------------------------------- public API

        /// <summary>Own this rectangle (world centre on the surface top, yaw, size: x = local X, y = local Z).</summary>
        public void SetTerritory(GatorNavSurface surface, Vector3 center, Quaternion rotation, Vector2 size)
        {
            bool moved = surface != territorySurface || (center - territoryCenter).sqrMagnitude > 0.0004f
                         || (size - territorySize).sqrMagnitude > 0.0004f;
            territorySurface = surface;
            territoryCenter = center;
            territoryRot = rotation;
            territorySize = size;
            if (!moved) return;
            nextTerritoryReturn = 0f;
            if (BusyInTerritory && (State == GatorState.Thinking || State == GatorState.Working) && !hopping)
            {
                GoToTerritoryFront();                 // e.g. sent to the back-of-desk line while working
                return;
            }
            // Wandering somewhere that is no longer ours: end the trip so the next one heads home.
            if (State == GatorState.Wandering && tripActive && !InTerritory(OnMesh && agent.hasPath ? agent.destination : transform.position))
                tripActive = false;
        }

        public void ClearTerritory()
        {
            if (territorySurface == null) return;
            territorySurface = null;
            tripActive = false;
        }

        /// <summary>
        /// How restful wandering is: chance a pause is a long rest, the rest length range (s), chance a trip is a run,
        /// and the short pause range (s). GatorCrew makes every gator calmer than the solo defaults.
        /// </summary>
        public void SetIdleProfile(float restChance, Vector2 restSeconds, float runProbability, Vector2 shortPause)
        {
            idlePauseChance = Mathf.Clamp01(restChance);
            idlePauseSeconds = restSeconds;
            runChance = Mathf.Clamp01(runProbability);
            idlePause = shortPause;
        }

        public void GetIdleProfile(out float restChance, out Vector2 restSeconds, out float runProbability, out Vector2 shortPause)
        {
            restChance = idlePauseChance;
            restSeconds = idlePauseSeconds;
            runProbability = runChance;
            shortPause = idlePause;
        }

        // ---------------------------------------------------------------- internals

        // On the territory's surface for real (the desk NavMesh moves when a whistle on another table is pressed).
        bool OnTerritorySurface => CurrentSurface == territorySurface && territorySurface.ContainsXZ(transform.position)
                                   && Mathf.Abs(transform.position.y - territorySurface.TopCenter.y) < 0.15f;

        bool InTerritory(Vector3 p, float margin = 0.02f)
        {
            if (!HasTerritory) return true;
            Vector3 l = Quaternion.Inverse(territoryRot) * (p - territoryCenter);
            return Mathf.Abs(l.x) <= territorySize.x * 0.5f + margin && Mathf.Abs(l.z) <= territorySize.y * 0.5f + margin;
        }

        // Uniform point in the territory, on its surface's NavMesh, outside every hole (keep-out).
        bool TryPickTerritoryPoint(out Vector3 point, bool anyDistance = false)
        {
            GatorNavSurface s = territorySurface;
            NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
            float minTrip = anyDistance || !InTerritory(transform.position) ? 0f
                          : Mathf.Min(0.5f, 0.3f * Mathf.Min(territorySize.x, territorySize.y));
            for (int i = 0; i < 10; i++)
            {
                Vector3 l = new Vector3((Random.value - 0.5f) * territorySize.x, 0f, (Random.value - 0.5f) * territorySize.y);
                Vector3 p = territoryCenter + territoryRot * l;
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 0.15f, f) && InTerritory(hit.position)
                    && Mathf.Abs(hit.position.y - s.TopCenter.y) < 0.1f && !s.IsInsideHole(hit.position, out _)
                    && FlatDistance(hit.position) >= minTrip)
                {
                    point = hit.position;
                    return true;
                }
            }
            point = transform.position;
            return false;
        }

        // Territory point nearest the user (the "front" of the area), keep-out safe.
        bool TryTerritoryFront(out Vector3 point)
        {
            Transform head = PetUser.Head;
            Vector3 l = Quaternion.Inverse(territoryRot) * ((head != null ? head.position : transform.position) - territoryCenter);
            float inset = Mathf.Min(0.06f, 0.25f * Mathf.Min(territorySize.x, territorySize.y));
            l.x = Mathf.Clamp(l.x, -territorySize.x * 0.5f + inset, territorySize.x * 0.5f - inset);
            l.z = Mathf.Clamp(l.z, -territorySize.y * 0.5f + inset, territorySize.y * 0.5f - inset);
            l.y = 0f;
            Vector3 p = KeepOutSafePoint(territorySurface, territoryCenter + territoryRot * l);
            NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
            if (NavMesh.SamplePosition(p, out NavMeshHit hit, 0.2f, f) && !territorySurface.IsInsideHole(hit.position, out _))
            {
                point = hit.position;
                return true;
            }
            return TryPickTerritoryPoint(out point, true);
        }

        // Wandering on the wrong surface (summoned, fell off, hatched on the floor): go home. Walk closer first when
        // the area is far away so the hop stays a hop. True if a trip / hop started.
        bool ReturnToTerritory()
        {
            if (!HasTerritory || hopping || OnTerritorySurface || Time.time < nextTerritoryReturn) return false;
            nextTerritoryReturn = Time.time + 3f;
            if (!TryPickTerritoryPoint(out Vector3 p, true)) return false;
            if (FlatDistance(p) > 1.2f && CurrentSurface != null && CurrentSurface.IsBuilt)
            {
                Vector3 near = CurrentSurface.ClosestPoint(p, 0.1f);
                if (FlatDistance(near) > 0.3f && MoveTo(near, false))
                {
                    tripActive = true;
                    nextActionTime = Time.time + 10f;
                    return true;
                }
            }
            HopTo(territorySurface, p);
            return hopping;
        }

        // Crew Thinking / Working: walk (or hop) to the front of the own area, then face the user.
        void GoToTerritoryFront()
        {
            if (!TryTerritoryFront(out Vector3 p)) { StopMoving(); return; }
            if (!OnTerritorySurface)
            {
                HopTo(territorySurface, p);
                hopFaceUser = hopping;
                return;
            }
            if (FlatDistance(p) < 0.08f) { StopMoving(); return; }
            if (MoveTo(p, false)) { approachingUser = true; approachStartTime = Time.time; faceUser = true; }
        }

        bool BusyInTerritory => StayInTerritoryWhenBusy && HasTerritory;
    }
}
