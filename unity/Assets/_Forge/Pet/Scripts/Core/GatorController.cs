using System;
using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// Gator pet brain: a small timer-driven state machine over a NavMeshAgent + Animator.
    /// Public API per docs/PET_AGENT_CONTRACT.md. Locomotion/animator helpers live in
    /// GatorController.Motion.cs, surfaces/desk hop in GatorController.Surfaces.cs. No allocations in Update.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public partial class GatorController : MonoBehaviour
    {
        [Header("States")]
        [SerializeField, Tooltip("State entered at Start (unless something called SetState earlier).")]
        GatorState startState = GatorState.Intro;
        [SerializeField, Tooltip("State used by FinishIntro(). The bridge decides when to leave Intro.")]
        GatorState stateAfterIntro = GatorState.Wandering;
        [SerializeField, Tooltip("Leave Intro automatically this many seconds after the greeting. 0 = wait for FinishIntro()/SetState().")]
        float autoLeaveIntroSeconds = 0f;
        [SerializeField, Tooltip("TaskComplete: seconds of spin before returning to Wandering.")]
        float spinSeconds = 1.2f;
        [SerializeField, Tooltip("Error: seconds of fear before returning to Wandering.")]
        float errorSeconds = 3f;
        [SerializeField, Tooltip("Future: Working morphs the gator into a blob (not implemented yet).")]
        bool workingBlobMorph = false;

        [Header("Wandering")]
        [SerializeField, Tooltip("Optional wander center. Empty = around the user, or around the spawn point if there is no user.")]
        Transform wanderCenter;
        [SerializeField, Tooltip("Radius (m) of random wander points.")]
        float wanderRadius = 3f;
        [SerializeField, Range(0f, 1f), Tooltip("Chance a wander trip is a run instead of a walk.")]
        float runChance = 0.3f;
        [SerializeField, Tooltip("Idle pause between trips, seconds (min, max).")]
        Vector2 idlePause = new Vector2(1f, 4f);
        [SerializeField, Range(0f, 1f), Tooltip("Chance a pause plays Idle_B/Idle_C.")]
        float idleVariantChance = 0.35f;
        [SerializeField, Range(0f, 1f), Tooltip("Chance a pause plays Jump or Bounce.")]
        float emoteChance = 0.15f;

        [Header("User distances (m)")]
        [SerializeField, Tooltip("Intro/ComeToUser stop distance in front of the head.")]
        float frontDistance = 1.2f;
        [SerializeField, Tooltip("Following: distance beside/behind the user.")]
        float followDistance = 1f;
        [SerializeField, Tooltip("Re-path when the user moved more than this.")]
        float repathDistance = 0.5f;
        [SerializeField, Tooltip("Thinking/Working: come closer if farther than this.")]
        float nearUserDistance = 2f;

        public GatorState State { get; private set; }
        public event Action<GatorState> StateChanged;
        /// <summary>Raised once the intro greeting (arrive + Jump) has played.</summary>
        public event Action IntroComplete;

        GatorExpressions expressions;
        Vector3 homePosition;
        float stateEnterTime, nextActionTime, approachStartTime, greetTime;
        bool started, tripActive, approachingUser, introGreeted, needRepath;
        Vector3 lastUserPos;

        float StateElapsed => Time.time - stateEnterTime;

        void Awake()
        {
            InitMotion();
            expressions = GetComponent<GatorExpressions>();
            homePosition = transform.position;
        }

        void Start()
        {
            // The prefab saves the NavMeshAgent disabled: enabling it with no NavMesh around logs a warning, and in
            // MR the floor/desk NavMesh only exists once MRUK has loaded the room. AgentReady() enables it when a
            // NavMesh is within reach (sandbox: immediately, surfaces build in Awake).
            if (!started) SetState(startState);
        }

        // ---------------------------------------------------------------- public API

        public void SetState(GatorState s)
        {
            if (started && s == State) return;
            started = true;
            State = s;
            StandUp();                          // any state change ends a sit/lay rest immediately
            EnterState(s);
            StateChanged?.Invoke(s);
        }

        /// <summary>Leave Intro to the serialized stateAfterIntro (no-op in other states).</summary>
        public void FinishIntro()
        {
            if (State == GatorState.Intro) SetState(stateAfterIntro);
        }

        /// <summary>Path to ~frontDistance in front of PetUser.Head, then face the user.</summary>
        public void ComeToUser()
        {
            Transform head = PetUser.Head;
            if (head == null) return;
            StandUp();
            ComeToUserInternal(!OnDesk && FlatDistance(head.position) > 3f);
        }

        // ---------------------------------------------------------------- state machine

        void EnterState(GatorState s)
        {
            stateEnterTime = Time.time;
            tripActive = approachingUser = introGreeted = false;
            evacuating = false;                 // a state change cancels a keep-out run (re-checked within 0.5 s)
            needRepath = true;
            nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);

            switch (s)
            {
                case GatorState.Intro:
                    faceUser = false;
                    ComeToUserInternal(true);
                    break;
                case GatorState.Wandering:
                    faceTarget = null; faceUser = false;
                    StopMoving(); SetMood(Mood.Neutral);
                    break;
                case GatorState.Following:
                    faceUser = true; SetMood(Mood.Neutral);
                    if (OnDesk) HopToFloor();          // contract: follow = floor following
                    break;
                case GatorState.Listening:
                    StopMoving(); faceUser = true; SetMood(Mood.Attentive);
                    break;
                case GatorState.Thinking:
                case GatorState.Working:
                    faceUser = true; SetMood(Mood.Squint);
                    if (IsFarFromUser(nearUserDistance)) ComeToUser(); else StopMoving();
                    if (s == GatorState.Working && workingBlobMorph)
                    {
                        // TODO(blob): morph into the blob and arc-jump into target_panel here.
                    }
                    break;
                case GatorState.Answering:
                    StopMoving(); faceUser = true;
                    PlayEmote("Bounce"); SetMood(Mood.Happy);
                    // TODO(lipsync): hook ITextToSpeech.SpeechStarted/Ended here (mouth/jaw anim while speaking).
                    break;
                case GatorState.TaskComplete:
                    StopMoving(); PlayEmote("Spin"); SetMood(Mood.Excited);
                    break;
                case GatorState.Error:
                    StopMoving(); PlayEmote("Fear"); SetMood(Mood.Sad);
                    break;
            }
        }

        void Update()
        {
            if (!started) return;
            if (hopping) { UpdateHop(); UpdateMotion(); return; }   // state logic resumes after landing
            if (UpdateAntics()) { UpdateMotion(); return; }        // roll / fall / stun (GatorController.Antics.cs)
            AutoDetectSurface();
            if (UpdateKeepOut()) { UpdateMotion(); return; }   // running out of the desk keep-out (GatorController.KeepOut.cs)
            switch (State)
            {
                case GatorState.Intro: UpdateIntro(); break;
                case GatorState.Wandering: UpdateWander(); break;
                case GatorState.Following: UpdateFollow(); break;
                case GatorState.Thinking:
                case GatorState.Working: UpdateFidget(); break;
                case GatorState.TaskComplete: if (StateElapsed >= spinSeconds) SetState(GatorState.Wandering); break;
                case GatorState.Error: if (StateElapsed >= errorSeconds) SetState(GatorState.Wandering); break;
            }
            UpdateApproach();
            UpdateMotion();
        }

        void UpdateIntro()
        {
            if (introGreeted)
            {
                if (autoLeaveIntroSeconds > 0f && Time.time - greetTime >= autoLeaveIntroSeconds) FinishIntro();
                return;
            }
            Transform head = PetUser.Head;
            if (head == null || !approachingUser)
            {
                // No user (yet) or no path: retry, greet in place after a short grace period.
                if (head != null && ComeToUserInternal(true)) return;
                if (StateElapsed > 2f) GreetUser();
                return;
            }
            if (UserMoved(head)) ComeToUserInternal(true);
            if (HasArrived() || StateElapsed > 12f) GreetUser();
        }

        void GreetUser()
        {
            StopMoving();
            approachingUser = false;
            faceUser = true;
            PlayEmote("Jump");
            SetMood(Mood.Happy);
            introGreeted = true;
            greetTime = Time.time;
            IntroComplete?.Invoke();
        }

        void UpdateWander()
        {
            if (approachingUser) return;
            if (tripActive)
            {
                if (HasArrived() || Time.time >= nextActionTime) { tripActive = false; StopMoving(); BeginPause(); }
                return;
            }
            if (Time.time < nextActionTime) return;

            bool run = UnityEngine.Random.value < runChance;
            if (TryPickWanderPoint(out Vector3 p) && MoveTo(p, run))
            {
                tripActive = true;
                nextActionTime = Time.time + 10f;     // trip timeout
            }
            else BeginPause();
        }

        // Idle pause between trips: often a long rest (sit / lay / idle, see Antics.cs), else a short pause
        // with an occasional fidget or emote.
        void BeginPause()
        {
            if (TryBeginRest()) return;
            nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
            float r = UnityEngine.Random.value;
            if (r < emoteChance) PlayEmote(UnityEngine.Random.value < 0.5f ? "Jump" : "Bounce");
            else if (r < emoteChance + idleVariantChance) PlayIdleVariant();
        }

        void UpdateFollow()
        {
            Transform head = PetUser.Head;
            if (head == null) { UpdateWander(); return; }
            if (!needRepath && !UserMoved(head)) return;

            needRepath = false;
            Vector3 userPos = Ground(head.position);
            lastUserPos = userPos;
            Vector3 fwd = FlatForward(head);
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            Vector3 goal = OnDesk ? UserGoal(head)   // stuck on the desk (no floor): desk edge nearest the user
                                  : userPos + (right * 0.6f - fwd * 0.8f).normalized * followDistance;
            float dist = FlatDistance(goal);
            if (dist > 0.3f) MoveTo(goal, dist > 3f);
        }

        // Thinking / Working: stay near the user, idle with occasional variants.
        void UpdateFidget()
        {
            if (approachingUser) return;
            if (IsFarFromUser(nearUserDistance + 1f)) { ComeToUser(); return; }
            if (Time.time < nextActionTime) return;
            nextActionTime = Time.time + UnityEngine.Random.Range(3f, 6f);
            if (UnityEngine.Random.value < 0.5f) PlayIdleVariant();
        }

        // Finish a ComeToUser trip: stop and face the user.
        void UpdateApproach()
        {
            if (!approachingUser || State == GatorState.Intro) return;
            if (HasArrived() || Time.time - approachStartTime > 12f)
            {
                StopMoving();
                approachingUser = false;
                faceUser = true;
                nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
            }
        }

        bool ComeToUserInternal(bool run)
        {
            Transform head = PetUser.Head;
            if (head == null) return false;
            if (!MoveTo(UserGoal(head), run)) return false;
            approachingUser = true;
            approachStartTime = Time.time;
            faceUser = true;
            lastUserPos = Ground(head.position);
            return true;
        }

        bool UserMoved(Transform head) =>
            (Ground(head.position) - lastUserPos).sqrMagnitude > repathDistance * repathDistance;

        bool IsFarFromUser(float meters)
        {
            Transform head = PetUser.Head;
            if (head == null) return false;
            // On the desk "near" is measured to the desk point nearest the head, with the same hysteresis ratio.
            if (OnDesk) return FlatDistance(UserGoal(head)) > deskNearDistance * meters / Mathf.Max(0.01f, nearUserDistance);
            return FlatDistance(head.position) > meters;
        }

        void SetMood(Mood m)
        {
            if (expressions != null) expressions.SetMood(m);
        }
    }
}
