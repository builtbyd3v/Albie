using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>Rest poses used by GatorController.Rest().</summary>
    public enum RestPose { Idle, Sit, Lay }

    /// <summary>
    /// GatorController antics (fourth part of the partial class): long rest pauses (sit / lay / sleepy),
    /// poke spam -> sideways Roll, and rolling off an elevated surface -> ballistic fall -> Death pose -> stand
    /// up -> hop back onto the desk. Driven from Update like the hop arc (no coroutines, no allocations).
    /// </summary>
    public partial class GatorController
    {
        [Header("Rest pauses (Wandering)")]
        [SerializeField, Range(0f, 1f), Tooltip("Chance a Wandering pause is a long rest (sit / lay / idle) instead of a short idle.")]
        float idlePauseChance = 0.65f;
        [SerializeField, Tooltip("Long rest duration, seconds (min, max).")]
        Vector2 idlePauseSeconds = new Vector2(3f, 9f);
        [SerializeField, Range(0f, 1f), Tooltip("Rest pose weights: chance of Sit (the rest of [0..1] is split by layChance).")]
        float sitChance = 0.4f;
        [SerializeField, Range(0f, 1f), Tooltip("Rest pose weights: chance of Lay. Remainder = plain idle with Idle_B/C variants.")]
        float layChance = 0.3f;
        [SerializeField, Range(0f, 1f), Tooltip("Chance a Lay rest gets sleepy eyes (Mood.Sleepy).")]
        float sleepyChance = 0.5f;

        [Header("Poke spam -> roll")]
        [SerializeField, Range(1, 8), Tooltip("Pokes inside the window that trigger a roll.")]
        int pokesToRoll = 3;
        [SerializeField, Tooltip("Sliding window (s) for counting pokes.")]
        float pokeWindowSeconds = 4f;
        [SerializeField, Tooltip("Sideways roll distance (m).")]
        float rollDistance = 0.35f;
        [SerializeField, Tooltip("Roll duration (s). The Roll clip is 0.42 s.")]
        float rollSeconds = 0.45f;
        [SerializeField, Tooltip("Dizzy time (s) after a roll that stayed on the surface.")]
        float dizzySeconds = 1.2f;

        [Header("Fall off the edge")]
        [SerializeField, Tooltip("Gravity (m/s^2) for the off-edge fall.")]
        float fallGravity = 9.81f;
        [SerializeField, Tooltip("Seconds lying in the Death pose after landing.")]
        float stunSeconds = 2.5f;
        [SerializeField, Tooltip("Seconds after standing up before hopping back onto the desk.")]
        float reviveHopDelay = 0.5f;

        static readonly int LayId = Animator.StringToHash("Lay");
        static readonly int SitId = Animator.StringToHash("Sit");
        static readonly int RollId = Animator.StringToHash("Roll");
        static readonly int DeathId = Animator.StringToHash("Death");
        static readonly int ReviveId = Animator.StringToHash("Revive");
        static readonly int ClickedId = Animator.StringToHash("Clicked");

        enum Antic { None, Rolling, Falling, Stunned, Recovering }

        Antic antic;
        float anticStart, anticUntil, fallVy, fallFloorY, reviveResetAt = -1f;
        Vector3 rollFrom, rollVel;
        bool rollElevated, rollFell, deadEyes, blinkWas = true, blinkSuppressed;
        GatorNavSurface rollSurface;

        RestPose restPose;               // current rest pose (Idle = not sitting / laying)
        bool restApplied;                // trigger fired (waits for Speed < 0.1 so Sit/Lay don't exit at once)
        float restUntil;

        GatorInteraction interaction;
        readonly float[] pokeTimes = new float[8];
        int pokeCount;

        public bool IsRolling => antic == Antic.Rolling || antic == Antic.Falling;
        public bool IsStunned => antic == Antic.Stunned || antic == Antic.Recovering;
        bool AnticsBusy => antic != Antic.None;

        // ---------------------------------------------------------------- public API

        /// <summary>Sit / lay / idle in place for idlePauseSeconds. Any SetState / ComeToUser / trip stands it up.</summary>
        public void Rest(RestPose pose)
        {
            if (AnticsBusy || hopping) return;
            StandUp();
            StopMoving();
            tripActive = approachingUser = false;
            restUntil = Time.time + Random.Range(idlePauseSeconds.x, idlePauseSeconds.y);
            nextActionTime = restUntil;      // Wandering: the next trip (Speed > 0.1) is what stands it up
            restPose = pose;
            restApplied = false;
            if (pose == RestPose.Idle) PlayIdleVariant();
        }

        /// <summary>Roll sideways (Roll anim + Dizzy). Off an elevated surface's edge it falls, lies stunned, then hops back.</summary>
        public void StartRoll()
        {
            if (AnticsBusy || hopping || agent == null) return;
            StandUp(false);                  // the Roll trigger itself leaves Sit/Lay (AnyState)
            if (OnMesh) StopMoving();
            tripActive = approachingUser = false;
            pokeCount = 0;

            // Random left/right relative to facing.
            Vector3 side = transform.right; side.y = 0f;
            side = side.sqrMagnitude < 1e-4f ? Vector3.right : side.normalized;
            if (Random.value < 0.5f) side = -side;

            // Elevated = a built surface that isn't the floor (desk, tables). Only there can it fall off.
            rollSurface = CurrentSurface != null && CurrentSurface.IsBuilt ? CurrentSurface : null;
            rollElevated = rollSurface != null && rollSurface != GatorNavSurface.Floor;
            float dist = rollDistance;
            // On the floor / plain NavMesh: stop at the NavMesh boundary instead of rolling into a wall.
            if (!rollElevated && OnMesh && agent.Raycast(transform.position + side * dist, out NavMeshHit hit))
                dist = Mathf.Max(0f, hit.distance - 0.02f);

            if (animator != null) animator.ResetTrigger(ClickedId);   // the poke that caused this queued Clicked
            AnimTrigger(RollId);
            SetMood(Mood.Dizzy);
            agent.enabled = false;           // the roll drives the transform directly (like the hop arc)
            rollFrom = transform.position;
            rollVel = side * (dist / Mathf.Max(0.05f, rollSeconds));
            rollFell = false;
            antic = Antic.Rolling;
            anticStart = Time.time;
        }

        // ---------------------------------------------------------------- lifecycle

        void OnEnable()
        {
            if (interaction == null) interaction = GetComponent<GatorInteraction>();
            if (interaction != null) interaction.Poked += OnPoked;
        }

        void OnDisable()
        {
            if (interaction != null) interaction.Poked -= OnPoked;
            if (AnticsBusy) { antic = Antic.None; SetBlink(true); }   // AgentReady() re-enables the agent later
        }

        void OnPoked()
        {
            if (AnticsBusy) { ApplyStunFace(); return; }                   // GatorInteraction just set Happy: undo it
            if (restPose != RestPose.Idle) StandUp();                      // a poke wakes it up (Clicked left Sit/Lay)
            float now = Time.time;
            int n = 0;                                                     // drop pokes outside the window
            for (int i = 0; i < pokeCount; i++) if (now - pokeTimes[i] <= pokeWindowSeconds) pokeTimes[n++] = pokeTimes[i];
            pokeCount = n;
            if (pokeCount < pokeTimes.Length) pokeTimes[pokeCount++] = now;
            if (pokeCount >= Mathf.Clamp(pokesToRoll, 1, pokeTimes.Length)) StartRoll();
        }

        // ---------------------------------------------------------------- rest

        // Called from BeginPause(): true if it chose a long rest (it then owns nextActionTime and the pose).
        bool TryBeginRest()
        {
            if (Random.value >= idlePauseChance) return false;
            float r = Random.value;
            Rest(r < sitChance ? RestPose.Sit : r < sitChance + layChance ? RestPose.Lay : RestPose.Idle);
            return true;
        }

        // Stand up from Sit/Lay now (Revive leaves Sit/Lay without needing Speed > 0.1) and drop the pause.
        void StandUp(bool fireRevive = true)
        {
            if (fireRevive && restPose != RestPose.Idle && IsInAnimState(restPose == RestPose.Sit ? SitId : LayId))
            {
                AnimTrigger(ReviveId);
                reviveResetAt = Time.time + 0.3f;
            }
            if (expressions != null && expressions.CurrentMood == Mood.Sleepy) { SetMood(Mood.Neutral); SetBlink(true); }
            restPose = RestPose.Idle;
            restApplied = false;
        }

        void UpdateRest()
        {
            if (reviveResetAt > 0f && Time.time >= reviveResetAt)
            {
                reviveResetAt = -1f;                   // never leave a stale Revive armed (it would skip the next Death)
                if (animator != null && HasParam(ReviveId)) animator.ResetTrigger(ReviveId);
            }
            if (restPose == RestPose.Idle) return;
            if (Time.time >= restUntil) { StandUp(); return; }
            if (restApplied || (animator != null && hasSpeed && animator.GetFloat(SpeedId) > 0.08f)) return;
            restApplied = true;
            if (animator != null && HasParam(ReviveId)) animator.ResetTrigger(ReviveId);
            AnimTrigger(restPose == RestPose.Sit ? SitId : LayId);
            if (restPose == RestPose.Lay && Random.value < sleepyChance && expressions != null)
            {
                expressions.SetMood(Mood.Sleepy, 0.8f);
                SetBlink(false);
            }
        }

        // ---------------------------------------------------------------- roll / fall / stun

        // Returns true while an antic owns the gator (state logic and wandering are paused).
        bool UpdateAntics()
        {
            UpdateRest();
            if (!AnticsBusy) return false;
            float now = Time.time, dt = Time.deltaTime;
            switch (antic)
            {
                case Antic.Rolling:
                {
                    float elapsed = Mathf.Min(now - anticStart, rollSeconds);
                    Vector3 p = rollFrom + rollVel * elapsed;
                    transform.position = p;
                    // Edge test: once the body's XZ leaves the elevated surface's top rectangle there is nothing
                    // under it, so hand the current horizontal velocity to a ballistic fall from desk height.
                    if (rollElevated && !rollSurface.ContainsXZ(p)) { BeginFall(p); break; }
                    if (now - anticStart >= rollSeconds) EndRollOnSurface();
                    break;
                }
                case Antic.Falling:
                {
                    // Semi-implicit Euler: v += g*dt, p += v*dt. Horizontal velocity is kept from the roll.
                    fallVy -= fallGravity * dt;
                    Vector3 p = transform.position + new Vector3(rollVel.x, fallVy, rollVel.z) * dt;
                    if (p.y <= fallFloorY) { p.y = fallFloorY; transform.position = p; LandFromFall(); }
                    else transform.position = p;
                    break;
                }
                case Antic.Stunned:
                    if (!deadEyes && now - anticStart >= 0.6f) { deadEyes = true; ApplyStunFace(); }
                    if (now >= anticUntil) Revive();
                    break;
                case Antic.Recovering:
                    if (now >= anticUntil) FinishAntics();
                    break;
            }
            return true;
        }

        void BeginFall(Vector3 p)
        {
            antic = Antic.Falling;
            rollFell = true;
            fallVy = 0f;
            // Floor height: Floor surface top if known (else y = 0), refined by the NavMesh near the predicted landing.
            GatorNavSurface floor = GatorNavSurface.Floor;
            float guessY = floor != null && floor.IsBuilt ? floor.TopCenter.y : 0f;
            float tFall = Mathf.Sqrt(2f * Mathf.Max(0f, p.y - guessY) / Mathf.Max(0.1f, fallGravity));
            Vector3 land = new Vector3(p.x + rollVel.x * tFall, guessY, p.z + rollVel.z * tFall);
            NavMeshQueryFilter f = navFilter; f.areaMask = WalkableMask;
            fallFloorY = NavMesh.SamplePosition(land, out NavMeshHit hit, 0.5f, f) && hit.position.y < p.y - 0.05f
                ? hit.position.y : guessY;
            if (fallFloorY > p.y) fallFloorY = p.y;   // never "fall" upward
        }

        void LandFromFall()
        {
            antic = Antic.Stunned;
            anticStart = Time.time;
            anticUntil = Time.time + stunSeconds;
            deadEyes = false;
            if (animator != null && HasParam(ReviveId)) animator.ResetTrigger(ReviveId);
            AnimTrigger(DeathId);                     // Death holds its last frame (on its back) until Revive
            SetMood(Mood.Dizzy);
            SetBlink(false);
        }

        void ApplyStunFace()
        {
            if (expressions == null) return;
            if (deadEyes) { expressions.SetMood(Mood.Neutral, 0.15f); expressions.SetShape("eyes.dead", 1f, 0.15f); }
            else expressions.SetMood(Mood.Dizzy, 0.1f);
        }

        void Revive()
        {
            AnimTrigger(ReviveId);                    // Death -> Locomotion
            reviveResetAt = Time.time + 0.3f;
            deadEyes = false;
            SetMood(Mood.Neutral);                    // also fades eyes.dead out
            SetBlink(true);
            GatorNavSurface floor = GatorNavSurface.Floor;
            SetWanderArea(floor != null && floor.IsBuilt ? floor : null);
            WarpOntoMesh(1f);
            antic = Antic.Recovering;
            anticUntil = Time.time + reviveHopDelay;
        }

        void EndRollOnSurface()
        {
            // Ended inside the desk keep-out: stay put (agent off) and scoot out in FinishAntics instead of teleporting.
            bool inKeepOut = rollSurface != null && rollSurface == GatorNavSurface.Desk && rollSurface.IsInsideHole(transform.position, out _);
            if (!inKeepOut) WarpOntoMesh(0.4f);       // the roll may end in the edge inset / a hole: snap back
            antic = Antic.Recovering;
            anticUntil = Time.time + dizzySeconds;
        }

        void FinishAntics()
        {
            antic = Antic.None;
            // Roll stayed on the desk but ended in the keep-out: get out first; the state is re-entered on arrival.
            if (!rollFell && EvacuateKeepOutAfterRoll()) { SetMood(Mood.Neutral); return; }
            if (State != GatorState.Intro) EnterState(State);   // re-apply the state's mood / facing / movement
            else SetMood(Mood.Neutral);
            // "Hop back up": only after a fall, and not while Following (contract: follow = floor).
            GatorNavSurface desk = GatorNavSurface.Desk;
            if (rollFell && State != GatorState.Following && desk != null && desk.IsBuilt) HopToUserDesk();
            rollFell = false;
        }

        void WarpOntoMesh(float radius)
        {
            if (agent == null) return;
            NavMeshQueryFilter f = navFilter;
            if (CurrentSurface != null) f.areaMask = WalkableMask;
            if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit, radius, f)) return;   // AgentReady() retries
            agent.enabled = true;
            agent.Warp(hit.position);
        }

        // ---------------------------------------------------------------- helpers

        void AnimTrigger(int hash)
        {
            if (animator != null && HasParam(hash)) animator.SetTrigger(hash);
        }

        bool IsInAnimState(int stateHash)
        {
            if (animator == null || !animator.isActiveAndEnabled) return false;
            if (animator.GetCurrentAnimatorStateInfo(0).shortNameHash == stateHash) return true;
            return animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).shortNameHash == stateHash;
        }

        // Auto-blink looks wrong over closed/dead eyes: suppress it, then restore whatever it was before.
        void SetBlink(bool on)
        {
            if (expressions == null || on != blinkSuppressed) return;
            if (!on) { blinkWas = expressions.AutoBlink; expressions.AutoBlink = false; blinkSuppressed = true; }
            else { expressions.AutoBlink = blinkWas; blinkSuppressed = false; }
        }
    }
}
