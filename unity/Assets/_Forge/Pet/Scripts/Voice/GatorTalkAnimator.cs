using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// "Lip sync" for a gator without a mouth blendshape: while the TTS speaks, loudness drives a small head nod
    /// (additive on top of the Animator, in LateUpdate) and a squash/stretch pulse of the model, and the face goes
    /// Mood.Happy. Loudness comes from ITalkLevel (StreamedPcmTextToSpeech); a TTS without it gets a synthetic
    /// syllable envelope. Hooks ITextToSpeech.SpeechStarted/SpeechEnded (the TODO(lipsync) spot in GatorController).
    /// No allocations per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorTalkAnimator : MonoBehaviour
    {
        [SerializeField, Tooltip("TTS to follow (ITextToSpeech, ideally also ITalkLevel). Auto-found in children / scene if empty.")]
        MonoBehaviour tts;
        [SerializeField, Tooltip("Head bone (nods). Auto-found by name under the Animator if empty.")]
        Transform headBone;
        [SerializeField, Tooltip("Bone name searched when headBone is empty.")]
        string headBoneName = "head";
        [SerializeField, Tooltip("Visual root that pulses (never the NavMeshAgent root). Empty = the Animator's transform.")]
        Transform pulseRoot;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents if empty.")]
        GatorExpressions expressions;

        [Header("Motion")]
        [SerializeField, Tooltip("Head bone local axis for the nod (generic rig: try X, then Z).")]
        Vector3 nodAxis = Vector3.right;
        [SerializeField, Tooltip("Nod angle (deg) at full loudness.")]
        float nodDegrees = 9f;
        [SerializeField, Tooltip("Stretch along Y at full loudness (0.05 = 5%). X/Z shrink by half that.")]
        float pulseAmount = 0.05f;
        [SerializeField, Tooltip("Envelope attack / release (s) on top of the TTS level.")]
        float attack = 0.03f, release = 0.12f;
        [SerializeField, Tooltip("Set Mood.Happy when speech starts.")]
        bool happyWhileSpeaking = true;

        ITextToSpeech _tts;
        ITalkLevel _level;
        Vector3 _baseScale = Vector3.one;
        bool _talking, _subscribed, _scaled;
        float _env, _talkStart;

        void Awake()
        {
            if (expressions == null) expressions = GetComponentInParent<GatorExpressions>();
            Animator animator = GetComponentInChildren<Animator>();
            if (pulseRoot == null && animator != null) pulseRoot = animator.transform;
            if (pulseRoot != null) _baseScale = pulseRoot.localScale;
            if (headBone == null && animator != null) headBone = FindChild(animator.transform, headBoneName);
            if (headBone == null) Debug.LogWarning("[GatorTalkAnimator] No head bone '" + headBoneName + "'; only the pulse will run.", this);
        }

        void Start() => Bind();   // after the bridge's Awake, which may add a NullTextToSpeech

        void Bind()
        {
            if (_subscribed) return;
            _tts = tts as ITextToSpeech;
            if (_tts == null) _tts = GetComponentInChildren<ITextToSpeech>(true);
            if (_tts == null) _tts = GetComponentInParent<ITextToSpeech>();
            if (_tts == null) { Debug.LogWarning("[GatorTalkAnimator] No ITextToSpeech found.", this); return; }
            _level = _tts as ITalkLevel;
            _tts.SpeechStarted += OnSpeechStarted;
            _tts.SpeechEnded += OnSpeechEnded;
            _subscribed = true;
        }

        void OnSpeechStarted()
        {
            _talking = true;
            _talkStart = Time.time;
            if (happyWhileSpeaking && expressions != null) expressions.SetMood(Mood.Happy, 0.15f);
        }

        void OnSpeechEnded() => _talking = false;

        void LateUpdate()
        {
            float target = 0f;
            if (_talking) target = _level != null ? _level.Level : SyntheticLevel(Time.time - _talkStart);
            float tau = Mathf.Max(0.001f, target > _env ? attack : release);
            _env = Mathf.Lerp(_env, target, 1f - Mathf.Exp(-Time.deltaTime / tau));

            if (_env < 0.001f)
            {
                // Idle: leave the head to the Animator and put the scale back once.
                if (_scaled && pulseRoot != null) { pulseRoot.localScale = _baseScale; _scaled = false; }
                return;
            }

            // The Animator rewrote the bone this frame, so the nod is additive and never accumulates.
            if (headBone != null) headBone.localRotation = headBone.localRotation * Quaternion.AngleAxis(_env * nodDegrees, nodAxis);
            if (pulseRoot != null)
            {
                float k = _env * pulseAmount;
                pulseRoot.localScale = new Vector3(_baseScale.x * (1f - 0.5f * k), _baseScale.y * (1f + k), _baseScale.z * (1f - 0.5f * k));
                _scaled = true;
            }
        }

        static float SyntheticLevel(float t) =>
            Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3.1f)) * (0.65f + 0.35f * Mathf.Sin(t * Mathf.PI * 0.7f));

        static Transform FindChild(Transform root, string childName)
        {
            if (string.IsNullOrEmpty(childName)) return null;
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChild(root.GetChild(i), childName);
                if (hit != null) return hit;
            }
            return null;
        }

        void OnDisable()
        {
            if (_scaled && pulseRoot != null) { pulseRoot.localScale = _baseScale; _scaled = false; }
        }

        void OnDestroy()
        {
            if (_tts != null && _subscribed)
            {
                _tts.SpeechStarted -= OnSpeechStarted;
                _tts.SpeechEnded -= OnSpeechEnded;
            }
        }
    }
}
