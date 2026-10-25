using System;
using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// Whistle to summon, then listen hands-free (docs/WHISTLE_PLAN.md §1, without the keep-out zone):
    ///   press → whistle SFX + Mood.Excited → gator on the floor and a desk is built: HopToUserDesk(), else ComeToUser()
    ///   → arrived (or approachTimeout) → PushToTalk.BeginHandsFree() (Listening, faces the user)
    ///   → PushToTalk ends it: transcript asked / nothing heard (8 s) / 20 s cap / error.
    /// Any WhistleButton works (one per table; the MRUK adapter makes the pressed table the desk first).
    /// Press again while approaching or listening = cancel; a summon that ends without a question sends the gator
    /// off to idle (GatorController.WanderAway) so it's clear it stopped listening. Thinking/Working: ignored with a Bounce and
    /// "One sec!". Answering: the speech is stopped first. No new GatorState: the trip happens in Wandering, and a
    /// state change from anyone else (hub pet.state, hotkeys, a wake word) during the trip cancels the summon quietly.
    /// PushToTalk stays the only reader of transcripts. No allocations per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorSummon : MonoBehaviour
    {
        public enum SummonEnd { Listening, Cancelled, Ignored }
        enum Phase { Idle, Approaching, Listening }

        [SerializeField, Tooltip("Default button (whistle sound source). Every WhistleButton in the scene toggles the summon.")]
        WhistleButton button;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        GatorController controller;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        PushToTalk pushToTalk;
        [SerializeField, Tooltip("Auto-found if empty.")]
        GatorCaption caption;
        [SerializeField, Tooltip("Auto-found if empty.")]
        GatorExpressions expressions;
        [SerializeField, Tooltip("TTS stopped when whistling during an answer (ITextToSpeech). Auto-found in children if empty.")]
        MonoBehaviour tts;

        [Header("Timing")]
        [SerializeField, Tooltip("Start listening after this many seconds even if the gator hasn't arrived.")]
        float approachTimeout = 2.5f;

        [Header("Whistle sound")]
        [SerializeField, Tooltip("Optional clip; empty = a two-tone chirp generated at startup.")]
        AudioClip whistleClip;
        [SerializeField, Tooltip("Fallback whistle source when the pressed button has no AudioSource. Empty = one on this object.")]
        AudioSource sfxSource;
        [SerializeField, Tooltip("Cancelled / nothing heard: walk off and idle away from the user.")]
        bool wanderAwayWhenDone = true;
        [SerializeField, Range(0f, 1f)] float whistleVolume = 0.6f;

        public bool IsActive => _phase != Phase.Idle;
        public event Action<SummonEnd> Ended;

        NavMeshAgent _agent;
        ITextToSpeech _tts;
        Phase _phase;
        float _phaseStart, _deadline;
        bool _selfStateChange;
        WhistleButton _pressed;

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<GatorController>();
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (pushToTalk == null) pushToTalk = GetComponent<PushToTalk>();
            if (pushToTalk == null) pushToTalk = FindAnyObjectByType<PushToTalk>();
            if (caption == null) caption = GetComponentInChildren<GatorCaption>(true);
            if (caption == null) caption = FindAnyObjectByType<GatorCaption>();
            if (expressions == null) expressions = GetComponentInParent<GatorExpressions>();
            if (button == null) button = FindAnyObjectByType<WhistleButton>();
            if (controller != null) _agent = controller.GetComponent<NavMeshAgent>();
            if (whistleClip == null) whistleClip = BuildChirp();
        }

        void Start()
        {
            _tts = tts as ITextToSpeech;
            if (_tts == null && controller != null) _tts = controller.GetComponentInChildren<ITextToSpeech>(true);
            if (sfxSource == null) sfxSource = GetComponent<AudioSource>();
            if (sfxSource == null) { sfxSource = gameObject.AddComponent<AudioSource>(); sfxSource.playOnAwake = false; }

            WhistleButton.AnyPressed += OnAnyPressed;
            if (button == null) Debug.Log("[GatorSummon] No WhistleButton in the scene yet; F/B only work on a button, call Toggle() yourself.", this);
            if (controller != null) controller.StateChanged += OnStateChanged;
            if (pushToTalk != null) pushToTalk.SessionEnded += OnSessionEnded;
            RefreshVisual();
        }

        void OnDestroy()
        {
            WhistleButton.AnyPressed -= OnAnyPressed;
            if (controller != null) controller.StateChanged -= OnStateChanged;
            if (pushToTalk != null) pushToTalk.SessionEnded -= OnSessionEnded;
        }

        // ---------------------------------------------------------------- public API

        void OnAnyPressed(WhistleButton b)
        {
            _pressed = b;
            Toggle();
        }

        /// <summary>What the button calls: summon, or cancel a summon in progress.</summary>
        public void Toggle()
        {
            if (IsActive) Cancel();
            else Summon();
        }

        public void Summon()
        {
            if (IsActive || controller == null || pushToTalk == null) return;
            GatorState s = controller.State;
            if (s == GatorState.Thinking || s == GatorState.Working)
            {
                controller.PlayEmote("Bounce");
                if (caption != null) caption.SetText("One sec!", false);
                Ended?.Invoke(SummonEnd.Ignored);
                return;
            }
            if (pushToTalk.SessionActive) { Ended?.Invoke(SummonEnd.Ignored); return; }   // a wake / follow-up turn already has the mic
            if (s == GatorState.Answering && _tts != null) _tts.Stop();

            PlayWhistle();
            MicPermission.Request(null);              // ask now (Android), in parallel with the walk
            SetStateQuietly(GatorState.Wandering);   // the trip happens in Wandering (no new state)
            if (expressions != null) expressions.SetMood(Mood.Excited);
            if (caption != null) caption.Clear();

            GatorNavSurface desk = GatorNavSurface.Desk;
            bool deskReady = desk != null && desk.IsBuilt;
            if (deskReady && controller.CurrentSurface != desk) controller.HopToUserDesk();
            else controller.ComeToUser();

            _phase = Phase.Approaching;
            _phaseStart = Time.time;
            _deadline = Time.time + approachTimeout;
            RefreshVisual();
        }

        public void Cancel()
        {
            if (_phase == Phase.Approaching)
            {
                // Drop the path: the controller's approach then counts as arrived and it goes back to wandering
                // (a hop already in the air just lands).
                if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.ResetPath();
                if (expressions != null) expressions.SetMood(Mood.Neutral);
                Finish(SummonEnd.Cancelled);
            }
            else if (_phase == Phase.Listening && pushToTalk != null)
            {
                pushToTalk.CancelSession();                   // raises SessionEnded → Finish
                if (_phase != Phase.Idle) Finish(SummonEnd.Cancelled);
            }
        }

        // ---------------------------------------------------------------- flow

        void Update()
        {
            if (_phase != Phase.Approaching) return;
            if ((Time.time - _phaseStart > 0.25f && Arrived()) || Time.time >= _deadline) BeginListening();
        }

        bool Arrived()
        {
            if (_agent == null) return true;
            if (!_agent.enabled) return false;                // mid-hop
            if (!_agent.isOnNavMesh) return true;             // can't walk: listen in place
            if (_agent.pathPending) return false;
            return !_agent.hasPath || _agent.remainingDistance <= _agent.stoppingDistance + 0.05f;
        }

        void BeginListening()
        {
            _phase = Phase.Listening;
            _selfStateChange = true;                          // PushToTalk switches to Listening
            bool started = pushToTalk != null && pushToTalk.BeginHandsFree();
            _selfStateChange = false;
            if (!started) { Finish(SummonEnd.Cancelled); return; }
            RefreshVisual();
        }

        void OnSessionEnded(bool answered)
        {
            if (_phase == Phase.Listening) Finish(answered ? SummonEnd.Listening : SummonEnd.Cancelled);
        }

        void OnStateChanged(GatorState s)
        {
            if (_selfStateChange) return;
            // Hub events / hotkeys / a wake turn win over a summon trip.
            if (_phase == Phase.Approaching && s != GatorState.Wandering) Finish(SummonEnd.Cancelled);
            RefreshVisual();
        }

        void Finish(SummonEnd how)
        {
            if (_phase == Phase.Idle) return;
            _phase = Phase.Idle;
            RefreshVisual();
            if (how == SummonEnd.Cancelled && wanderAwayWhenDone && controller != null
                && (pushToTalk == null || !pushToTalk.SessionActive))   // not when another turn has the mic
                controller.WanderAway();
            Ended?.Invoke(how);
        }

        void SetStateQuietly(GatorState s)
        {
            if (controller == null || controller.State == s) return;
            _selfStateChange = true;
            controller.SetState(s);
            _selfStateChange = false;
        }

        void RefreshVisual()
        {
            WhistleButton.Visual v = IsActive ? WhistleButton.Visual.Active
                : controller != null && (controller.State == GatorState.Thinking || controller.State == GatorState.Working)
                    ? WhistleButton.Visual.Busy : WhistleButton.Visual.Idle;
            var all = WhistleButton.All;
            for (int i = 0; i < all.Count; i++) if (all[i] != null) all[i].SetVisual(v);
        }

        void PlayWhistle()
        {
            if (whistleClip == null) return;
            WhistleButton from = _pressed != null ? _pressed : button;
            AudioSource src = from != null ? from.GetComponent<AudioSource>() : null;
            if (src == null) src = sfxSource;
            if (src != null) src.PlayOneShot(whistleClip, whistleVolume);
        }

        // Two rising notes (~1.8→2.3 kHz, then 2.1→2.7 kHz), 0.45 s. Built once.
        static AudioClip BuildChirp()
        {
            const int rate = 44100;
            int n = Mathf.RoundToInt(rate * 0.45f);
            var data = new float[n];
            double phase = 0.0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float f, env;
                if (t < 0.18f) { float u = t / 0.18f; f = Mathf.Lerp(1800f, 2300f, u); env = Mathf.Sin(Mathf.PI * u); }
                else if (t < 0.22f) { f = 2100f; env = 0f; }
                else { float u = (t - 0.22f) / 0.23f; f = Mathf.Lerp(2100f, 2700f, u); env = Mathf.Sin(Mathf.PI * Mathf.Min(1f, u * 1.4f)) * (1f - 0.3f * u); }
                phase += 2.0 * Math.PI * f / rate;
                data[i] = 0.5f * env * (float)(Math.Sin(phase) + 0.15 * Math.Sin(2.0 * phase));
            }
            var clip = AudioClip.Create("Whistle", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
