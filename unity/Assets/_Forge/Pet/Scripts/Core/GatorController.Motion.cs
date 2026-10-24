using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// GatorController locomotion, facing and Animator plumbing (second half of the partial class).
    /// </summary>
    public partial class GatorController
    {
        [Header("Locomotion")]
        [SerializeField, Tooltip("Agent speed (m/s) that matches the Walk clip at Animator Speed = 1.")]
        float walkSpeed = 0.6f;
        [SerializeField, Tooltip("Agent speed (m/s) that matches the Run clip at Animator Speed = 2.")]
        float runSpeed = 1.8f;
        [SerializeField, Tooltip("NavMeshAgent angular speed (deg/s) while moving.")]
        float angularSpeed = 240f;
        [SerializeField, Tooltip("NavMeshAgent acceleration (m/s^2). Low = no foot sliding on start/stop.")]
        float acceleration = 6f;
        [SerializeField, Tooltip("Manual yaw speed (deg/s) when facing a target while stopped.")]
        float turnSpeed = 270f;
        [SerializeField, Tooltip("Damp time for the Animator Speed parameter.")]
        float speedDampTime = 0.15f;
        [SerializeField, Tooltip("Max distance when snapping a goal onto the NavMesh.")]
        float sampleRadius = 1.5f;

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int IdleIndexId = Animator.StringToHash("IdleIndex");

        NavMeshAgent agent;
        Animator animator;
        int[] paramHashes = new int[0];
        bool hasSpeed, hasIdleIndex, warnedOffMesh;
        float nextNavRetry, idleIndexResetAt = -1f;
        NavMeshQueryFilter navFilter;    // gator agent type, all areas (set in InitMotion)
        Transform faceTarget;            // explicit FaceTarget(); wins over faceUser
        bool faceUser;                   // face PetUser.Head while stopped

        // ---------------------------------------------------------------- public API

        /// <summary>Animator trigger: "Jump" | "Bounce" | "Clicked" | "Spin" | "Sit" | "Lay" | "Fear" (ignored while rolling/stunned).</summary>
        public void PlayEmote(string animatorTrigger)
        {
            if (animator == null || string.IsNullOrEmpty(animatorTrigger)) return;
            if (AnticsBusy) return;              // rolling / lying stunned: ignore outside emotes (e.g. poke "Clicked")
            int hash = Animator.StringToHash(animatorTrigger);
            if (!HasParam(hash)) { Debug.LogWarning("[GatorController] Animator has no trigger '" + animatorTrigger + "'", this); return; }
            animator.SetTrigger(hash);
        }

        /// <summary>Smoothly yaw toward t while not moving. Null = stop facing anything.</summary>
        public void FaceTarget(Transform t)
        {
            faceTarget = t;
            if (t == null) faceUser = false;
        }

        // ---------------------------------------------------------------- setup

        void InitMotion()
        {
            agent = GetComponent<NavMeshAgent>();
            // Runtime agent type (radius 0.15, height 0.4) matching every GatorNavSurface bake.
            // Set before anything queries the agent; AgentReady() keeps retrying the snap if off-mesh.
            agent.agentTypeID = GatorNavSurface.GatorAgentTypeId;
            navFilter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
            agent.speed = walkSpeed;
            agent.angularSpeed = angularSpeed;
            agent.acceleration = acceleration;
            agent.autoBraking = true;
            if (agent.stoppingDistance < 0.05f) agent.stoppingDistance = 0.1f;

            animator = GetComponentInChildren<Animator>();
            if (animator == null) { Debug.LogWarning("[GatorController] No Animator under " + name, this); return; }
            animator.applyRootMotion = false;       // clips are in-place; the agent moves the root
            if (animator.runtimeAnimatorController == null)
            {
                Debug.LogWarning("[GatorController] Animator has no controller (expected AC_Gator).", this);
                return;
            }
            AnimatorControllerParameter[] ps = animator.parameters;
            paramHashes = new int[ps.Length];
            for (int i = 0; i < ps.Length; i++) paramHashes[i] = ps[i].nameHash;
            hasSpeed = HasParam(SpeedId);
            hasIdleIndex = HasParam(IdleIndexId);
        }

        bool HasParam(int hash)
        {
            for (int i = 0; i < paramHashes.Length; i++) if (paramHashes[i] == hash) return true;
            return false;
        }

        // ---------------------------------------------------------------- navigation

        bool OnMesh => agent != null && agent.enabled && agent.isOnNavMesh;

        // True if the agent can move. Off-mesh: retries a snap every second, warns once.
        bool AgentReady()
        {
            if (agent == null || AnticsBusy) return false;   // a roll/fall drives the transform with the agent off
            if (agent.enabled && agent.isOnNavMesh) return true;
            if (Time.time < nextNavRetry) return false;
            nextNavRetry = Time.time + 1f;
            // Off-mesh (or still disabled from the prefab): look for a NavMesh nearby, then enable + warp onto it.
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2f, navFilter))
            {
                if (!agent.enabled) agent.enabled = true;
                if (agent.Warp(hit.position)) return agent.isOnNavMesh;
            }
            if (!warnedOffMesh)
            {
                warnedOffMesh = true; // expected in MR until MRUK loads the room and the surfaces are built
                Debug.Log("[GatorController] No NavMesh under the gator yet; waiting for a GatorNavSurface.", this);
            }
            return false;
        }

        bool MoveTo(Vector3 worldPos, bool run)
        {
            if (!AgentReady()) return false;
            // Never leave the current surface: clamp the goal onto its top face first.
            if (CurrentSurface != null && CurrentSurface.IsBuilt) worldPos = CurrentSurface.ClosestPoint(worldPos, 0.02f);
            if (!NavMesh.SamplePosition(worldPos, out NavMeshHit hit, sampleRadius, navFilter)) return false;
            agent.speed = run ? runSpeed : walkSpeed;
            agent.isStopped = false;
            agent.updateRotation = true;
            return agent.SetDestination(hit.position);
        }

        void StopMoving()
        {
            if (!OnMesh) return;
            agent.isStopped = true;
            agent.ResetPath();
        }

        bool HasArrived()
        {
            if (!OnMesh) return true;
            if (agent.pathPending) return false;
            return !agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.05f;
        }

        bool IsMoving()
        {
            if (!OnMesh || agent.isStopped) return false;
            return agent.pathPending || (agent.hasPath && agent.remainingDistance > agent.stoppingDistance + 0.05f);
        }

        bool TryPickWanderPoint(out Vector3 point)
        {
            if (HasTerritory && OnTerritorySurface) return TryPickTerritoryPoint(out point);
            if (CurrentSurface != null && CurrentSurface.IsBuilt) return TryPickSurfacePoint(CurrentSurface, out point);
            Transform head = PetUser.Head;
            Vector3 center = wanderCenter != null ? wanderCenter.position
                           : head != null ? Ground(head.position) : homePosition;
            for (int i = 0; i < 6; i++)
            {
                Vector2 c = Random.insideUnitCircle * wanderRadius;
                Vector3 p = new Vector3(center.x + c.x, center.y, center.z + c.y);
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, sampleRadius, navFilter)
                    && FlatDistance(hit.position) > 0.8f)
                {
                    point = hit.position;
                    return true;
                }
            }
            point = transform.position;
            return false;
        }

        // ---------------------------------------------------------------- per-frame motion

        void UpdateMotion()
        {
            bool moving = IsMoving();
            if (agent != null && agent.enabled) agent.updateRotation = moving;

            if (!moving && !hopping && !AnticsBusy)       // the hop arc / roll own rotation
            {
                Transform t = faceTarget != null ? faceTarget : (faceUser ? PetUser.Head : null);
                if (t != null) RotateToward(t.position);
            }

            if (animator != null && hasSpeed)
                animator.SetFloat(SpeedId, SpeedParam(), speedDampTime, Time.deltaTime);

            if (idleIndexResetAt > 0f && Time.time >= idleIndexResetAt)
            {
                idleIndexResetAt = -1f;
                if (animator != null && hasIdleIndex) animator.SetInteger(IdleIndexId, 0);
            }
        }

        // Piecewise-linear so walkSpeed -> 1 and runSpeed -> 2 exactly (keeps feet planted).
        float SpeedParam()
        {
            float v = OnMesh ? agent.velocity.magnitude : 0f;
            if (v <= walkSpeed) return v / Mathf.Max(0.01f, walkSpeed);
            return Mathf.Min(2f, 1f + (v - walkSpeed) / Mathf.Max(0.01f, runSpeed - walkSpeed));
        }

        void RotateToward(Vector3 worldPos)
        {
            Vector3 dir = worldPos - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            Quaternion target = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
        }

        // Plays Idle_B (1) or Idle_C (2) once; IdleIndex is reset to 0 shortly after.
        void PlayIdleVariant()
        {
            if (animator == null || !hasIdleIndex || restPose != RestPose.Idle) return;   // not while sitting/laying
            animator.SetInteger(IdleIndexId, Random.value < 0.5f ? 1 : 2);
            idleIndexResetAt = Time.time + 0.3f;
        }

        // ---------------------------------------------------------------- helpers

        Vector3 Ground(Vector3 p) => new Vector3(p.x, transform.position.y, p.z);

        float FlatDistance(Vector3 p)
        {
            float dx = p.x - transform.position.x, dz = p.z - transform.position.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Head forward projected on the ground (falls back to head-up when looking straight down).
        static Vector3 FlatForward(Transform t)
        {
            Vector3 f = t.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.01f) { f = t.up; f.y = 0f; }
            return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        }
    }
}
