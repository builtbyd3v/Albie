using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Forge.Pet
{
    /// <summary>
    /// SANDBOX ONLY (PetSandbox / PetSandboxXR; GatorSetup adds it to the scene instance, never to the prefab).
    /// Keyboard-only STT mock for the simulator / editor without a mic.
    /// StartListening shows a bottom-of-screen TMP input field (built lazily in code) and focuses it.
    /// Every edit raises PartialTranscript; Enter (or StopListening) raises FinalTranscript and hides it.
    /// Esc cancels (raises Error("cancelled"), no final). Empty Enter is ignored.
    /// Note: PushToTalk treats this provider as "press to open" (no release-to-stop), because
    /// Space is both the talk key and a character you want to type.
    /// </summary>
    public class TypedSpeechToText : MonoBehaviour, ISpeechToText
    {
        [SerializeField, Tooltip("Placeholder shown in the empty input field.")]
        string placeholder = "Ask the gator... (Enter = send, Esc = cancel)";
        [SerializeField, Tooltip("Font size of the input field text.")]
        float fontSize = 28f;
        [SerializeField, Tooltip("Overlay canvas sorting order.")]
        int sortingOrder = 500;

        public bool IsListening { get => _listening; private set { if (_listening == value) return; _listening = value; s_listening += value ? 1 : -1; } }
        /// <summary>Some typed field is open: other key handlers (whistle F, crew N/X) ignore their keys. False when none exists.</summary>
        public static bool AnyListening => s_listening > 0;
        public event Action<string> PartialTranscript;
        public event Action<string> FinalTranscript;
        public event Action<string> Error;

        static int s_listening;
        bool _listening;
        Canvas _canvas;
        TMP_InputField _field;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_listening = 0;

        public void StartListening()
        {
            EnsureUI();
            if (_field == null) { Error?.Invoke("typed input UI unavailable"); return; }
            IsListening = true;
            _field.SetTextWithoutNotify(string.Empty);
            _canvas.gameObject.SetActive(true);
            Focus();
        }

        public void StopListening()
        {
            if (!IsListening) return;
            string text = _field != null ? _field.text.Trim() : string.Empty;
            Hide();
            if (text.Length > 0) FinalTranscript?.Invoke(text);
            else Error?.Invoke("cancelled"); // nothing typed: let PushToTalk leave Listening
        }

        void Update()
        {
            if (!IsListening) return;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                Hide();
                Error?.Invoke("cancelled");
            }
        }

        void OnValueChanged(string text)
        {
            if (IsListening) PartialTranscript?.Invoke(text.Trim());
        }

        void OnSubmit(string text)
        {
            if (!IsListening) return;
            text = text != null ? text.Trim() : string.Empty;
            if (text.Length == 0) { Focus(); return; } // guard: no final for empty Enter
            Hide();
            FinalTranscript?.Invoke(text);
        }

        void Focus()
        {
            if (_field == null) return;
            _field.Select();
            _field.ActivateInputField();
        }

        void Hide()
        {
            IsListening = false;
            if (_field != null) _field.DeactivateInputField();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        void EnsureUI()
        {
            if (_field != null) return;
            EnsureEventSystem();

            var canvasGo = new GameObject("TypedSTT_Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortingOrder;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            GameObject fieldGo = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
            fieldGo.name = "TypedSTT_Input";
            fieldGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)fieldGo.transform;
            rt.anchorMin = new Vector2(0.15f, 0f);
            rt.anchorMax = new Vector2(0.85f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 40f);
            rt.sizeDelta = new Vector2(0f, 64f);

            _field = fieldGo.GetComponent<TMP_InputField>();
            _field.lineType = TMP_InputField.LineType.SingleLine;
            _field.pointSize = fontSize;
            _field.resetOnDeActivation = false;
            if (_field.placeholder is TMP_Text ph) ph.text = placeholder;
            _field.onValueChanged.AddListener(OnValueChanged);
            _field.onSubmit.AddListener(OnSubmit);
            canvasGo.SetActive(false);
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem (TypedSTT)");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        void OnDisable()
        {
            if (IsListening) Hide();
        }

        void OnDestroy()
        {
            IsListening = false;
            if (_field != null)
            {
                _field.onValueChanged.RemoveListener(OnValueChanged);
                _field.onSubmit.RemoveListener(OnSubmit);
            }
        }
    }
}
