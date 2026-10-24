using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Push-to-talk: hold Space / XR primary button → Listening + STT; release → StopListening.
    /// FinalTranscript → Thinking + GatorAgentBridge.Ask(text). PartialTranscript → caption (partial).
    /// With TypedSpeechToText the key only OPENS the input field (Enter sends, Esc cancels),
    /// because Space must be typeable while the field is focused.
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

        ISpeechToText _stt;
        bool _held;          // key physically held (hold-to-talk providers)
        bool _typedMode;     // current provider is TypedSpeechToText

        public ISpeechToText Provider => _stt;

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
            if (talkAction == null) return;
            if (talkAction.WasPressedThisFrame()) OnPressed();
            if (talkAction.WasReleasedThisFrame()) OnReleased();
        }

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
                var stt = ResolveProvider();
                if (stt == null) return;
                if (_typedMode && stt.IsListening) return;   // typing: Space is a character, not PTT
                _held = true;

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
            if (caption != null && !string.IsNullOrEmpty(text)) caption.SetText(text, true);
        }

        void OnFinal(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                // Nothing heard: go back to idle instead of hanging in Listening.
                if (caption != null) caption.Clear();
                if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
                return;
            }
            if (caption != null) caption.SetText(text, false);
            if (controller != null) controller.SetState(GatorState.Thinking);
            if (bridge != null) bridge.Ask(text);
            else Debug.LogWarning("[PushToTalk] No GatorAgentBridge; transcript dropped: " + text);
        }

        void OnSttError(string message)
        {
            _held = false;
            if (message != "cancelled")
            {
                Debug.LogWarning("[PushToTalk] STT error: " + message);
                if (caption != null) caption.SetText("(" + message + ")", false);
            }
            else if (caption != null) caption.Clear();
            if (controller != null && controller.State == GatorState.Listening) controller.SetState(GatorState.Wandering);
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
