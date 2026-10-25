using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Bit's listening turns. There is no hold-to-talk key any more: every turn is hands-free, opened by
    /// BeginHandsFree() from the whistle (GatorSummon), a wake word (WakeListener) or a follow-up (GatorFollowUp).
    /// Listening + STT until endSilence after speech (mic level from IMicLevel), noSpeechTimeout without any speech
    /// (cancelled), or the maxUtterance cap; CancelSession() drops it without asking.
    /// FinalTranscript → Thinking + GatorAgentBridge.Ask(text). PartialTranscript → caption (partial).
    /// Provider: HubSpeechToText (mic over ForgeNet) in hub mode; TypedSpeechToText only exists in the sandbox scenes
    /// (with the MockEventSource), where a turn opens the input field (Enter sends, Esc cancels). Nothing is ever
    /// auto-added outside hub mode. This stays the ONLY reader of transcripts, so nothing is ever asked twice.
    /// Voice mode (AgentClient.VoiceActive with HubSpeechToText): the hub's voice session answers the turn itself,
    /// so the final transcript is captioned but not sent as agent.ask.
    /// </summary>
    public class PushToTalk : MonoBehaviour
    {
        [SerializeField, Tooltip("Optional explicit STT provider (must implement ISpeechToText). Auto-picked if empty.")]
        MonoBehaviour sttProvider;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        GatorController controller;
        [SerializeField, Tooltip("Auto-found in children / scene if empty.")]
        GatorCaption caption;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        GatorAgentBridge bridge;

        [Header("Hands-free")]
        [SerializeField, Tooltip("Give up (cancel) if no speech is heard within this many seconds.")]
        float noSpeechTimeout = 8f;
        [SerializeField, Tooltip("Hard cap (s) from the first speech: stop listening and send what was said.")]
        float maxUtterance = 20f;
        [SerializeField, Tooltip("Silence (s) after speech that ends the utterance.")]
        float endSilence = 0.9f;
        [SerializeField, Tooltip("Mic RMS (0..1) that counts as speech.")]
        float speechLevel = 0.03f;
        [SerializeField, Tooltip("Seconds above speechLevel before it counts as speech (ignores clicks / bumps).")]
        float minSpeechSeconds = 0.25f;
        [SerializeField, Tooltip("After the mic stops, the hub's stt.final must arrive within this many seconds; otherwise the turn counts as empty (so he never hangs in Listening).")]
        float finalTimeout = 6f;

        ISpeechToText _stt;
        bool _warnedNoStt;
        bool _typedMode;     // current provider is TypedSpeechToText
        bool _handsFree;     // a hands-free session is open (until SessionEnded)
        bool _discardFinal;  // CancelSession: swallow the final transcript that is still on its way
        bool _heardSpeech;
        float _sessionStart, _lastSpeech, _speechTime, _sessionNoSpeech, _firstSpeech;
        float _finalDeadline = -1f;   // > 0 while a stopped turn waits for its stt.final

        public ISpeechToText Provider => _stt;
        /// <summary>A listening turn is open (until SessionEnded).</summary>
        public bool SessionActive => _handsFree;
        public bool HandsFree => _handsFree;
        /// <summary>Hands-free session over. true = a transcript went on to be answered; false = cancelled / nothing heard / error.</summary>
        public event Action<bool> SessionEnded;
        /// <summary>The STT provider can listen without a key (mic). False for the typed field (it would just sit open).</summary>
        public bool CanListenHandsFree
        {
            get
            {
                var stt = ResolveProvider();
                return stt != null && !(stt is TypedSpeechToText);
            }
        }

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<GatorController>();
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (caption == null) caption = GetComponentInChildren<GatorCaption>(true);
            if (caption == null) caption = FindAnyObjectByType<GatorCaption>();
            if (bridge == null) bridge = GetComponent<GatorAgentBridge>();
            if (bridge == null) bridge = FindAnyObjectByType<GatorAgentBridge>();
        }

        void Update()
        {
            if (_handsFree) UpdateHandsFree();
            if (_finalDeadline > 0f && Time.time >= _finalDeadline) FinalTimedOut();
        }

        // Mic stopped, no stt.final: the hub lost the turn (socket hiccup, voice session gone). Treat it as empty and
        // swallow a late final, instead of Bit standing in Listening with the mic blocked for everyone else.
        void FinalTimedOut()
        {
            _finalDeadline = -1f;
            Debug.LogWarning("[PushToTalk] No stt.final within " + finalTimeout.ToString("F0") + " s; dropping the turn.");
            _discardFinal = true;
            if (caption != null) caption.Clear();
            if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
            EndHandsFree(false);
        }

        void StopAndAwaitFinal()
        {
            if (_stt == null || !_stt.IsListening) return;
            _stt.StopListening();
            _finalDeadline = Time.time + Mathf.Max(1f, finalTimeout);
        }

        // ---------------------------------------------------------------- hands-free API

        /// <summary>Starts a hands-free listening session (mic permission asked first). False if one is already open or there is no STT.</summary>
        public bool BeginHandsFree() => BeginHandsFree(noSpeechTimeout);

        /// <summary>Same, giving up after <paramref name="noSpeechSeconds"/> without speech (e.g. 15 s follow-up after an answer).</summary>
        public bool BeginHandsFree(float noSpeechSeconds)
        {
            if (SessionActive) return false;
            _sessionNoSpeech = noSpeechSeconds > 0f ? noSpeechSeconds : noSpeechTimeout;
            _speechTime = 0f;
            var stt = ResolveProvider();
            if (stt == null || stt.IsListening) return false;
            _handsFree = true;
            _discardFinal = false;
            _finalDeadline = -1f;
            _heardSpeech = false;
            _sessionStart = _lastSpeech = Time.time;
            StartHandsFreeAsync(stt);
            return true;
        }

        /// <summary>Drops the open session without asking anything; the gator goes back to Wandering.</summary>
        public void CancelSession()
        {
            if (!SessionActive) return;
            _finalDeadline = -1f;
            if (_stt != null && _stt.IsListening)
            {
                _discardFinal = true;
                if (_stt is HubSpeechToText) SendHub("stt.cancel");   // voice mode would otherwise answer the turn
                _stt.StopListening();
            }
            if (caption != null) caption.Clear();
            if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
            EndHandsFree(false);
        }

        async void StartHandsFreeAsync(ISpeechToText stt)
        {
            try
            {
                if (!_typedMode)
                {
                    bool ok = await MicPermission.RequestAsync();
                    if (this == null) return;
                    if (!_handsFree) return;                 // cancelled while the permission dialog was up
                    if (!ok)
                    {
                        if (caption != null) caption.SetText("I need microphone permission.", false);
                        if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
                        EndHandsFree(false);
                        return;
                    }
                }
                if (caption != null) caption.Clear();
                if (controller != null) controller.SetState(GatorState.Listening);
                _sessionStart = _lastSpeech = Time.time;
                stt.StartListening();                        // failures arrive as Error → OnSttError ends the session
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EndHandsFree(false);
            }
        }

        void UpdateHandsFree()
        {
            if (_stt == null || !_stt.IsListening || _typedMode) return;   // typed: Enter / Esc end it
            float now = Time.time;
            if (_stt is IMicLevel mic && mic.Level >= speechLevel)
            {
                _lastSpeech = now;
                _speechTime += Time.deltaTime;
                if (!_heardSpeech && _speechTime >= minSpeechSeconds) { _heardSpeech = true; _firstSpeech = now; }
            }

            if (_heardSpeech && now - _firstSpeech >= maxUtterance) StopAndAwaitFinal();  // cap: send what we have
            else if (!_heardSpeech && now - _sessionStart >= _sessionNoSpeech) CancelSession();
            else if (_heardSpeech && now - _lastSpeech >= endSilence) StopAndAwaitFinal();
        }

        void EndHandsFree(bool answered)
        {
            if (!_handsFree) return;
            _handsFree = false;
            SessionEnded?.Invoke(answered);
        }

        // ---------------------------------------------------------------- provider + transcripts

        /// <summary>
        /// Resolves the provider lazily (config is loaded async, so EventSourceIsHub is known late). Hub mode: the
        /// HubSpeechToText on this object (added if the prefab lost it). Sandbox (mock source): the TypedSpeechToText the
        /// sandbox builder put on the gator. Nothing else is ever created; no provider = a warning, not a text box.
        /// </summary>
        ISpeechToText ResolveProvider()
        {
            ISpeechToText wanted = sttProvider as ISpeechToText;
            if (wanted == null)
            {
                bool hub = GatorAgentBridge.EventSourceIsHub;
                MonoBehaviour pick = null;
                if (hub) pick = GetComponent<HubSpeechToText>();
                else pick = GetComponent<TypedSpeechToText>();
                if (pick == null)
                {
                    foreach (var mb in GetComponents<MonoBehaviour>())
                        if (mb is ISpeechToText && (hub || !(mb is HubSpeechToText))) { pick = mb; break; }
                }
                if (pick == null && hub) pick = gameObject.AddComponent<HubSpeechToText>();
                if (pick == null && !_warnedNoStt)
                {
                    _warnedNoStt = true;
                    Debug.LogWarning("[PushToTalk] No speech-to-text provider on " + name + " yet (hub mode undecided, or a sandbox gator without TypedSpeechToText).", this);
                }
                wanted = pick as ISpeechToText;
            }
            if (!ReferenceEquals(wanted, _stt))
            {
                Unsubscribe();
                _stt = wanted;
                if (_stt != null)
                {
                    _stt.PartialTranscript += OnPartial;
                    _stt.FinalTranscript += OnFinal;
                    _stt.Error += OnSttError;
                }
            }
            _typedMode = _stt is TypedSpeechToText;
            return _stt;
        }

        void OnPartial(string text)
        {
            if (_discardFinal) return;
            if (_handsFree && !string.IsNullOrEmpty(text)) { _heardSpeech = true; _lastSpeech = Time.time; }
            if (caption != null && !string.IsNullOrEmpty(text)) caption.SetText(text, true);
        }

        void OnFinal(string text)
        {
            _finalDeadline = -1f;
            if (_discardFinal)
            {
                _discardFinal = false;                       // cancelled session: the hub's (empty) final, ignore it
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                // Nothing heard: go back to idle instead of hanging in Listening.
                if (caption != null) caption.Clear();
                if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
                EndHandsFree(false);
                return;
            }
            if (caption != null) caption.SetText(text, false);
            if (controller != null) controller.SetState(GatorState.Thinking);
            if (HubVoiceAnswers()) { /* the hub's voice session is already answering this turn */ }
            else if (bridge != null) bridge.Ask(text);
            else Debug.LogWarning("[PushToTalk] No GatorAgentBridge; transcript dropped: " + text);
            EndHandsFree(true);
        }

        void OnSttError(string message)
        {
            _finalDeadline = -1f;
            _discardFinal = false;
            if (message != "cancelled")
            {
                Debug.LogWarning("[PushToTalk] STT error: " + message);
                if (caption != null) caption.SetText("(" + message + ")", false);
            }
            else if (caption != null) caption.Clear();
            if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
            EndHandsFree(false);
        }

        bool HubVoiceAnswers() =>
            _stt is HubSpeechToText && bridge != null && bridge.Source is AgentClient ac && ac.VoiceActive;

        void SendHub(string type)
        {
            if (bridge == null || bridge.Source == null || bridge.Config == null) return;
            bridge.Source.Send(type, new JObject { ["pet_id"] = bridge.Config.petId });
        }

        void Unsubscribe()
        {
            if (_stt == null) return;
            _stt.PartialTranscript -= OnPartial;
            _stt.FinalTranscript -= OnFinal;
            _stt.Error -= OnSttError;
        }

        void OnDestroy() => Unsubscribe();
    }
}
