using System.Collections.Generic;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>Facial mood presets built from the gator's eye/tear blendshapes.</summary>
    public enum Mood { Neutral, Happy, Attentive, Squint, Sad, Excited, Dizzy, Sleepy }

    /// <summary>
    /// Drives blendshapes on every SkinnedMeshRenderer under this root (all LODs) with smooth fades,
    /// mood presets and an additive auto-blink. Update only touches shapes that are currently fading.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorExpressions : MonoBehaviour
    {
        [SerializeField, Tooltip("Mood applied at Start if nobody set one before.")]
        Mood startMood = Mood.Neutral;
        [SerializeField, Tooltip("Blink automatically at random intervals.")]
        bool autoBlink = true;
        [SerializeField, Tooltip("Random seconds between auto-blinks (min, max).")]
        Vector2 blinkInterval = new Vector2(2f, 6f);
        [SerializeField, Tooltip("Duration of one blink (close + open), seconds.")]
        float blinkDuration = 0.15f;

        const string BlinkShape = "eyes.blink";
        const float MaxWeight = 100f;            // Unity blendshape weight range is 0..100

        // Mood presets: (shape name, weight 0..1). Any shape not listed fades to 0.
        struct ShapeWeight
        {
            public readonly string Name; public readonly float Weight;
            public ShapeWeight(string n, float w) { Name = n; Weight = w; }
        }
        static readonly ShapeWeight[] HappyPreset = { new ShapeWeight("eyes.happy", 1f) };
        static readonly ShapeWeight[] AttentivePreset = { new ShapeWeight("eyes.excited-1", 0.4f), new ShapeWeight("eyes.lookUp", 0.3f) };
        static readonly ShapeWeight[] SquintPreset = { new ShapeWeight("eyes.squint", 0.8f) };
        static readonly ShapeWeight[] SadPreset = { new ShapeWeight("eyes.sad", 1f), new ShapeWeight("teardrop-1.L", 0.35f), new ShapeWeight("teardrop-1.R", 0.35f) };
        static readonly ShapeWeight[] ExcitedPreset = { new ShapeWeight("eyes.excited-2", 1f) };
        static readonly ShapeWeight[] DizzyPreset = { new ShapeWeight("eyes.spin-1", 1f) };
        static readonly ShapeWeight[] SleepyPreset = { new ShapeWeight("eyes.sleep", 1f) };
        static readonly ShapeWeight[] NeutralPreset = new ShapeWeight[0];

        public bool AutoBlink { get => autoBlink; set { autoBlink = value; ScheduleBlink(); } }
        public Mood CurrentMood { get; private set; }

        SkinnedMeshRenderer[] renderers;
        int[][] rendererIndex;                       // [renderer][shapeId] -> blendshape index, -1 if missing
        readonly Dictionary<string, int> shapeIds = new Dictionary<string, int>();
        float[] current, from, to, fadeStart, fadeDur, moodTargets;
        bool[] fading;
        int[] activeList;                            // shape ids currently fading
        int activeCount;
        int blinkId = -1;
        float blinkStart = -1f, nextBlink;
        bool initialized, moodSet;

        void Awake() => EnsureInit();

        void Start()
        {
            if (!moodSet) SetMood(startMood, 0f);
            ScheduleBlink();
        }

        void EnsureInit()
        {
            if (initialized) return;
            initialized = true;
            renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // Union of blendshape names across renderers (identical per LOD for this asset, but be safe).
            for (int r = 0; r < renderers.Length; r++)
            {
                Mesh mesh = renderers[r].sharedMesh;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string n = mesh.GetBlendShapeName(i);
                    if (!shapeIds.ContainsKey(n)) shapeIds.Add(n, shapeIds.Count);
                }
            }

            int count = shapeIds.Count;
            current = new float[count]; from = new float[count]; to = new float[count];
            fadeStart = new float[count]; fadeDur = new float[count]; moodTargets = new float[count];
            fading = new bool[count]; activeList = new int[count];
            rendererIndex = new int[renderers.Length][];

            for (int r = 0; r < renderers.Length; r++)
            {
                int[] map = new int[count];
                for (int s = 0; s < count; s++) map[s] = -1;
                Mesh mesh = renderers[r].sharedMesh;
                if (mesh != null)
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        int id = shapeIds[mesh.GetBlendShapeName(i)];
                        map[id] = i;
                        current[id] = renderers[r].GetBlendShapeWeight(i) / MaxWeight;
                    }
                rendererIndex[r] = map;
            }

            if (!shapeIds.TryGetValue(BlinkShape, out blinkId)) blinkId = -1;
            if (count == 0) Debug.LogWarning("[GatorExpressions] No blendshapes found under " + name, this);
        }

        /// <summary>Fade one blendshape to weight01 (0..1) on every LOD renderer.</summary>
        public void SetShape(string blendShapeName, float weight01, float fadeSeconds = 0.25f)
        {
            EnsureInit();
            if (blendShapeName != null && shapeIds.TryGetValue(blendShapeName, out int id))
                StartFade(id, weight01, fadeSeconds);
        }

        /// <summary>Fade to a mood preset. Shapes not in the preset fade to 0.</summary>
        public void SetMood(Mood m, float fadeSeconds = 0.25f)
        {
            EnsureInit();
            moodSet = true;
            CurrentMood = m;

            for (int i = 0; i < moodTargets.Length; i++) moodTargets[i] = 0f;
            ShapeWeight[] preset = GetPreset(m);
            for (int i = 0; i < preset.Length; i++)
                if (shapeIds.TryGetValue(preset[i].Name, out int id)) moodTargets[id] = preset[i].Weight;

            for (int id = 0; id < moodTargets.Length; id++)
            {
                float target = moodTargets[id];
                if (!fading[id] && Mathf.Approximately(current[id], target)) continue;
                StartFade(id, target, fadeSeconds);
            }
        }

        /// <summary>Quick additive blink (eyes.blink 0 -> 1 -> 0).</summary>
        public void Blink()
        {
            EnsureInit();
            if (blinkId >= 0) blinkStart = Time.time;
        }

        static ShapeWeight[] GetPreset(Mood m)
        {
            switch (m)
            {
                case Mood.Happy: return HappyPreset;
                case Mood.Attentive: return AttentivePreset;
                case Mood.Squint: return SquintPreset;
                case Mood.Sad: return SadPreset;
                case Mood.Excited: return ExcitedPreset;
                case Mood.Dizzy: return DizzyPreset;
                case Mood.Sleepy: return SleepyPreset;
                default: return NeutralPreset;
            }
        }

        void StartFade(int id, float weight01, float seconds)
        {
            to[id] = Mathf.Clamp01(weight01);
            if (seconds <= 0f)
            {
                current[id] = to[id];
                if (fading[id]) RemoveActive(id);
                Apply(id);
                return;
            }
            from[id] = current[id];
            fadeStart[id] = Time.time;
            fadeDur[id] = seconds;
            if (!fading[id]) { fading[id] = true; activeList[activeCount++] = id; }
        }

        void RemoveActive(int id)
        {
            fading[id] = false;
            for (int k = 0; k < activeCount; k++)
                if (activeList[k] == id) { activeList[k] = activeList[--activeCount]; return; }
        }

        void Update()
        {
            float now = Time.time;

            // Only iterate shapes that are fading (swap-remove when done).
            for (int k = activeCount - 1; k >= 0; k--)
            {
                int id = activeList[k];
                float t = (now - fadeStart[id]) / fadeDur[id];
                if (t >= 1f)
                {
                    current[id] = to[id];
                    fading[id] = false;
                    activeList[k] = activeList[--activeCount];
                }
                else current[id] = Mathf.Lerp(from[id], to[id], t * t * (3f - 2f * t));
                Apply(id);
            }

            // Auto-blink, additive over whatever the mood set for eyes.blink.
            if (blinkId < 0) return;
            if (autoBlink && blinkStart < 0f && now >= nextBlink) blinkStart = now;
            if (blinkStart >= 0f)
            {
                if (now - blinkStart >= blinkDuration) { blinkStart = -1f; ScheduleBlink(); }
                Apply(blinkId);
            }
        }

        void ScheduleBlink() => nextBlink = Time.time + Random.Range(blinkInterval.x, blinkInterval.y);

        // Write one shape's weight (plus blink, if active) to every renderer that has it.
        void Apply(int id)
        {
            float value = current[id];
            if (id == blinkId && blinkStart >= 0f)
            {
                float t = Mathf.Clamp01((Time.time - blinkStart) / blinkDuration);
                value = Mathf.Clamp01(value + (1f - Mathf.Abs(2f * t - 1f)));   // triangle 0 -> 1 -> 0
            }
            float w = value * MaxWeight;
            if (renderers == null || rendererIndex == null) return;
            for (int r = 0; r < renderers.Length; r++)
            {
                int[] map = rendererIndex[r];                 // null when a renderer had no mesh/blendshapes (or was torn down)
                if (map == null || renderers[r] == null) continue;
                int idx = map[id];
                if (idx >= 0) renderers[r].SetBlendShapeWeight(idx, w);
            }
        }
    }
}
