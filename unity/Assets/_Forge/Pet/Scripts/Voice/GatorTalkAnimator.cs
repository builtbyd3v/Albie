using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Lip sync: while the TTS speaks, loudness opens the jaw (Gator_Model.fbx's "jaw" bone, PR 26: rotation about its local
    /// +X, ~20 deg at full loudness, set absolutely from the cached rest pose in LateUpdate and put back when idle), drives
    /// a small head nod (additive on top of the Animator) and a squash/stretch pulse of the model, and the face goes
    /// Mood.Happy. Without a jaw bone (the pack's original model) only the nod and the pulse run. Loudness comes from
    /// ITalkLevel (StreamedPcmTextToSpeech); a TTS without it gets a synthetic syllable envelope. Hooks
    /// ITextToSpeech.SpeechStarted/SpeechEnded (the TODO(lipsync) spot in GatorController). No allocations per frame.
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
        [SerializeField, Tooltip("Jaw bone (opens the mouth). Auto-found by name under the Animator if empty; Gator_Model.fbx (PR 26) has Rig/root/body/head/jaw. None = no mouth motion.")]
        Transform jawBone;
        [SerializeField, Tooltip("Bone name searched when jawBone is empty.")]
        string jawBoneName = "jaw";
        [SerializeField, Tooltip("Visual root that pulses (never the NavMeshAgent root). Empty = the Animator's transform.")]
        Transform pulseRoot;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents if empty.")]
        GatorExpressions expressions;

        [Header("Motion")]
        [SerializeField, Tooltip("Head bone local axis for the nod (generic rig: try X, then Z).")]
        Vector3 nodAxis = Vector3.right;
        [SerializeField, Tooltip("Nod angle (deg) at full loudness.")]
        float nodDegrees = 9f;
        [SerializeField, Tooltip("Jaw bone local axis; +X opens the mouth on Gator_Model.")]
        Vector3 jawAxis = Vector3.right;
        [SerializeField, Tooltip("Mouth opening (deg) at full loudness. ~20 looks right on Gator_Model.")]
        float jawOpenDegrees = 20f;
        [SerializeField, Tooltip("Stretch along Y at full loudness (0.05 = 5%). X/Z shrink by half that.")]
        float pulseAmount = 0.05f;
        [SerializeField, Tooltip("Envelope attack / release (s) on top of the TTS level.")]
        float attack = 0.03f, release = 0.12f;
        [SerializeField, Tooltip("Set Mood.Happy when speech starts.")]
        bool happyWhileSpeaking = true;

        ITextToSpeech _tts;
        ITalkLevel _level;
        Vector3 _baseScale = Vector3.one;
        Quaternion _jawRest = Quaternion.identity;
        bool _talking, _subscribed, _scaled, _jawMoved;
        float _env, _talkStart;

        void Awake()
        {
            if (expressions == null) expressions = GetComponentInParent<GatorExpressions>();
            Animator animator = GetComponentInChildren<Animator>();
            if (pulseRoot == null && animator != null) pulseRoot = animator.transform;
            if (pulseRoot != null) _baseScale = pulseRoot.localScale;
            if (headBone == null && animator != null) headBone = FindChild(animator.transform, headBoneName);
            if (headBone == null) Debug.LogWarning("[GatorTalkAnimator] No head bone '" + headBoneName + "'; only the pulse will run.", this);
            if (jawBone == null && animator != null) jawBone = FindChild(animator.transform, jawBoneName);
            if (jawBone != null) _jawRest = jawBone.localRotation;   // bind pose: the pack clips never touch the jaw
            else Debug.Log("[GatorTalkAnimator] No jaw bone '" + jawBoneName + "' (PR 26's Gator_Model.fbx adds one); the mouth stays shut.", this);
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
                // Idle: leave the head to the Animator, shut the mouth and put the scale back once.
                if (_scaled && pulseRoot != null) { pulseRoot.localScale = _baseScale; _scaled = false; }
                if (_jawMoved && jawBone != null) { jawBone.localRotation = _jawRest; _jawMoved = false; }
                return;
            }

            // The Animator rewrote the head bone this frame, so the nod is additive and never accumulates. Nothing animates
            // the jaw, so it is set absolutely from its rest pose (a jaw clip on another layer would simply be overridden).
            if (headBone != null) headBone.localRotation = headBone.localRotation * Quaternion.AngleAxis(_env * nodDegrees, nodAxis);
            if (jawBone != null) { jawBone.localRotation = _jawRest * Quaternion.AngleAxis(_env * jawOpenDegrees, jawAxis); _jawMoved = true; }
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
            if (_jawMoved && jawBone != null) { jawBone.localRotation = _jawRest; _jawMoved = false; }
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
