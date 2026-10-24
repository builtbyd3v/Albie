using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Push-to-talk: hold Space / XR primary button → Listening + STT; release → StopListening.
    /// FinalTranscript → Thinking + GatorAgentBridge.Ask(text). PartialTranscript → caption (partial).
    /// With TypedSpeechToText the key only OPENS the input field (Enter sends, Esc cancels),
    /// because Space must be typeable while the field is focused.
    ///
    /// Hands-free (GatorSummon / whistle): BeginHandsFree() listens without a held key. The session ends on
    /// endSilence after speech (mic level from IMicLevel), noSpeechTimeout without any speech (cancelled), or the
    /// maxUtterance cap; CancelSession() drops it without asking. Pressing Space/A mid-session hands it to hold mode.
    /// This stays the ONLY reader of transcripts, so nothing is ever asked twice.
    /// Voice mode (AgentClient.VoiceActive with HubSpeechToText): the hub's voice session answers the turn itself,
    /// so the final transcript is captioned but not sent as agent.ask.
    /// </summary>
    public class PushToTalk : MonoBehaviour
    {
        [SerializeField, Tooltip("Hold-to-talk action. Default bindings (Space, XR primary buttons) are added if empty.")]
        InputAction talkAction = new InputAction("PushToTalk", InputActionType.Button);
        [SerializeField, Tooltip("Optional explicit STT provider (must implement ISpeechToText). Auto-picked if empty.")]
        MonoBehaviour sttProvider;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        GatorController controller;
        [SerializeField, Tooltip("Auto-found in children / scene if empty.")]
        GatorCaption caption;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        GatorAgentBridge bridge;

        [Header("Hands-free (whistle)")]
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

        ISpeechToText _stt;
        bool _held;          // key physically held (hold-to-talk providers)
        bool _typedMode;     // current provider is TypedSpeechToText
        bool _handsFree;     // a hands-free session is open (until SessionEnded)
        bool _discardFinal;  // CancelSession: swallow the final transcript that is still on its way
        bool _heardSpeech;
        float _sessionStart, _lastSpeech, _speechTime, _sessionNoSpeech, _firstSpeech;

        public ISpeechToText Provider => _stt;
        /// <summary>A hands-free or held session is open.</summary>
        public bool SessionActive => _handsFree || _held;
        public bool HandsFree => _handsFree;
        /// <summary>Hands-free session over. true = a transcript went on to be answered; false = cancelled / nothing heard / error / handed to hold mode.</summary>
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
            if (talkAction == null) talkAction = new InputAction("PushToTalk", InputActionType.Button);
            if (talkAction.bindings.Count == 0)
            {
                talkAction.AddBinding("<Keyboard>/space");
                talkAction.AddBinding("<XRController>{RightHand}/primaryButton");
                talkAction.AddBinding("<XRController>{LeftHand}/primaryButton");
            }
            if (controller == null) controller = GetComponentInParent<GatorController>();
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (caption == null) caption = GetComponentInChildren<GatorCaption>(true);
            if (caption == null) caption = FindAnyObjectByType<GatorCaption>();
            if (bridge == null) bridge = GetComponent<GatorAgentBridge>();
            if (bridge == null) bridge = FindAnyObjectByType<GatorAgentBridge>();
        }

        void OnEnable() => talkAction?.Enable();
        void OnDisable() => talkAction?.Disable();

        void Update()
        {
            if (talkAction != null)
            {
                if (talkAction.WasPressedThisFrame()) OnPressed();
                if (talkAction.WasReleasedThisFrame()) OnReleased();
            }
            if (_handsFree) UpdateHandsFree();
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
            _heardSpeech = false;
            _sessionStart = _lastSpeech = Time.time;
            StartHandsFreeAsync(stt);
            return true;
        }

        /// <summary>Drops the open session without asking anything; the gator goes back to Wandering.</summary>
        public void CancelSession()
        {
            if (!SessionActive) return;
            _held = false;
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

            if (_heardSpeech && now - _firstSpeech >= maxUtterance) _stt.StopListening();  // cap: send what we have
            else if (!_heardSpeech && now - _sessionStart >= _sessionNoSpeech) CancelSession();
            else if (_heardSpeech && now - _lastSpeech >= endSilence) _stt.StopListening();
        }

        void EndHandsFree(bool answered)
        {
            if (!_handsFree) return;
            _handsFree = false;
            SessionEnded?.Invoke(answered);
        }

        // ---------------------------------------------------------------- push-to-talk

        /// <summary>Resolves the provider lazily (config is loaded async, so EventSourceIsHub is known late).</summary>
        ISpeechToText ResolveProvider()
        {
            ISpeechToText wanted = sttProvider as ISpeechToText;
            if (wanted == null)
            {
                MonoBehaviour pick = null;
                if (GatorAgentBridge.EventSourceIsHub) pick = GetComponent<HubSpeechToText>();
                if (pick == null) pick = GetComponent<TypedSpeechToText>();
                if (pick == null)
                {
                    foreach (var mb in GetComponents<MonoBehaviour>())
                        if (mb is ISpeechToText) { pick = mb; break; }
                }
                if (pick == null) pick = gameObject.AddComponent<TypedSpeechToText>(); // sandbox fallback
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

        async void OnPressed()
        {
            try
            {
                if (_handsFree)
                {
                    // Space/A during a whistle session: hold mode takes it over (release ends the utterance).
                    _handsFree = false;
                    SessionEnded?.Invoke(false);
                    if (_stt != null && _stt.IsListening) { _held = !_typedMode; return; }
                }

                var stt = ResolveProvider();
                if (stt == null) return;
                if (_typedMode && stt.IsListening) return;   // typing: Space is a character, not PTT
                _held = true;
                _discardFinal = false;

                if (!_typedMode)
                {
                    bool ok = await MicPermission.RequestAsync();
                    if (this == null) return;                // destroyed while awaiting
                    if (!ok) { if (caption != null) caption.SetText("I need microphone permission.", false); return; }
                    if (!_held) return;                      // released before permission resolved
                }

                if (caption != null) caption.Clear();
                if (controller != null) controller.SetState(GatorState.Listening);
                stt.StartListening();
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        void OnReleased()
        {
            if (!_held) return;
            _held = false;
            if (_typedMode || _stt == null) return;          // typed: Enter/Esc finish it
            if (_stt.IsListening) _stt.StopListening();
        }

        void OnPartial(string text)
        {
            if (_discardFinal) return;
            if (_handsFree && !string.IsNullOrEmpty(text)) { _heardSpeech = true; _lastSpeech = Time.time; }
            if (caption != null && !string.IsNullOrEmpty(text)) caption.SetText(text, true);
        }

        void OnFinal(string text)
        {
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
            _held = false;
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
