using System;
using UnityEngine;

namespace Forge.ML
{
    [Serializable] public class MLSample
    {
        public string id, name, path, split;
        public int label_idx, true_idx, pred_idx, width, height, channels;
        public long file_size;
        public float[] probs;
        public bool correct;
        [NonSerialized] public bool removed;
    }
    [Serializable] public class MLLayer { public string name, type; public int[] in_shape, out_shape; public int @params; }
    [Serializable] public class MLMetrics { public string run_id; public int epoch, step; public float train_loss, train_acc, val_loss, val_acc, lr; }
    [Serializable] public class MLFixture { public string[] classes; public MLSample[] samples; public MLLayer[] layers; public MLMetrics[] metrics; [NonSerialized] public int[][] confusion; }

    // Local presentation state. A hub adapter can replace replay by calling ApplyMetrics,
    // ApplySampleUpdate and SetRunState on the Unity main thread; these are not Core events.
    public class MLSession : MonoBehaviour
    {
        public TextAsset mock;
        public MLMockAssets mockAssets;
        public float replaySpeed = 1f;
        public bool replay = true;
        [Tooltip("When ForgeNet reaches a live hub, consume Core ml.* events instead of local replay.")]
        public bool useLiveEvents = true;
        public float secondsPerEpoch = 1.5f;
        public MLFixture Data { get; private set; }
        public Texture2D Atlas { get; private set; }
        public int EpochIndex { get; private set; } = -1;
        public int TotalEpochs { get; private set; }
        public bool Done { get; private set; }
        public bool Overfit { get; private set; }
        public string OverfitReason { get; private set; }
        public string RunStatus { get; private set; } = "idle";
        public bool UsesGeneratedMocks => timeline != null;
        public event Action Changed;
        public event Action SamplesChanged;
        public event Action<MLMetrics> Metrics;
        public event Action<int, int> PairSelected;
        public event Action<string, int> RelabelRequested;
        public event Action<string> RemoveRequested;
        float nextTick, replayStarted;
        MLAtlasMetadata atlasMetadata;
        MLReplayEvent[] timeline;
        int[] atlasCells;
        int nextEvent;
        bool ownsAtlas;
        public const int AtlasColumns = 8;

        public static MLSession Resolve(MLSession assigned)
        {
            if (assigned != null) { assigned.EnsureLoaded(); return assigned; }
            var session = FindAnyObjectByType<MLSession>();
            if (session == null) session = new GameObject("ML Mock Session").AddComponent<MLSession>();
            session.EnsureLoaded();
            return session;
        }
        void Awake() => EnsureLoaded();
        void OnEnable()
        {
            ForgeEvents.OnMlRunState += OnLiveRunState;
            ForgeEvents.OnMlMetrics += OnLiveMetrics;
            ForgeEvents.OnMlOverfitAlert += OnLiveOverfit;
            ForgeEvents.OnMlSampleUpdated += OnLiveSampleUpdated;
            ForgeNet.StatusChanged += OnNetStatus;
        }
        void OnDisable()
        {
            ForgeEvents.OnMlRunState -= OnLiveRunState;
            ForgeEvents.OnMlMetrics -= OnLiveMetrics;
            ForgeEvents.OnMlOverfitAlert -= OnLiveOverfit;
            ForgeEvents.OnMlSampleUpdated -= OnLiveSampleUpdated;
            ForgeNet.StatusChanged -= OnNetStatus;
        }
        public void EnsureLoaded()
        {
            if (Data != null) return;
            if (mockAssets == null) mockAssets = Resources.Load<MLMockAssets>("MLMockAssets");
            if (mockAssets != null && mock == null)
            {
                Data = mockAssets.Load(out atlasMetadata, out timeline, out atlasCells);
                Atlas = mockAssets.atlas;
            }
            else
            {
                if (mock == null) mock = Resources.Load<TextAsset>("ml-demo");
                if (mock == null) throw new InvalidOperationException("Missing ML mock asset references. Run Forge > ML > Build Prefabs and Sandbox.");
                Data = JsonUtility.FromJson<MLFixture>(mock.text);
                BuildAtlas(); ownsAtlas = true;
            }
            TotalEpochs = Data.metrics.Length;
            replayStarted = Time.time;
            nextTick = Time.time + secondsPerEpoch;
        }
        void Update()
        {
            if (!replay || Done) return;
            if (timeline != null)
            {
                float elapsed = (Time.time - replayStarted) * Mathf.Max(.01f, replaySpeed);
                while (nextEvent < timeline.Length && timeline[nextEvent].t <= elapsed) ApplyReplayEvent(timeline[nextEvent++]);
                return;
            }
            if (Time.time < nextTick) return;
            nextTick = Time.time + Mathf.Max(.1f, secondsPerEpoch);
            int next = EpochIndex + 1;
            if (next < Data.metrics.Length) ApplyMetrics(Data.metrics[next]);
            if (next >= Data.metrics.Length - 1) SetRunState("done", TotalEpochs);
        }
        public void ApplyReplayEvent(MLReplayEvent e)
        {
            var p = e.payload;
            switch (e.type)
            {
                case "ml.run_state": SetRunState(p.status, p.total_epochs); break;
                case "ml.metrics": ApplyMetrics(p); break;
                case "ml.overfit_alert": Overfit = true; OverfitReason = p.reason; Changed?.Invoke(); break;
                case "ml.sample_updated": ApplySampleUpdate(p.id, p.label_idx, p.removed); break;
                case "ml.results_ready": Changed?.Invoke(); break;
            }
        }

        public void ApplyMetrics(MLMetrics tick)
        {
            int metricIndex = Mathf.Max(0, tick.epoch - 1);
            if (metricIndex >= Data.metrics.Length) Array.Resize(ref Data.metrics, metricIndex + 1);
            EpochIndex = metricIndex;
            Data.metrics[EpochIndex] = tick;
            if (EpochIndex >= 2)
            {
                var a = Data.metrics[EpochIndex - 2]; var b = Data.metrics[EpochIndex - 1];
                Overfit |= tick.val_loss > b.val_loss && b.val_loss > a.val_loss && tick.train_loss < b.train_loss && b.train_loss < a.train_loss;
            }
            Metrics?.Invoke(tick); Changed?.Invoke();
        }
        public void SetRunState(string status, int totalEpochs)
        {
            TotalEpochs = totalEpochs > 0 ? totalEpochs : Data.metrics.Length; RunStatus = status; Done = status == "done"; Changed?.Invoke();
        }
        public void Restart() { EpochIndex = -1; Done = Overfit = false; OverfitReason = null; RunStatus = "idle"; nextEvent = 0; replayStarted = Time.time; nextTick = Time.time + secondsPerEpoch; Changed?.Invoke(); }
        public void SelectPair(int truth, int predicted) => PairSelected?.Invoke(truth, predicted);
        public void Relabel(MLSample sample, int label)
        {
            if (!replay) { RelabelRequested?.Invoke(sample.id, label); return; }
            ApplySampleUpdate(sample.id, label, false);
        }
        public void Remove(MLSample sample)
        {
            if (!replay) { RemoveRequested?.Invoke(sample.id); return; }
            ApplySampleUpdate(sample.id, sample.true_idx, true);
        }
        public void ApplySampleUpdate(string id, int label, bool removed)
        {
            foreach (var sample in Data.samples) if (sample.id == id)
            { sample.label_idx = sample.true_idx = label; sample.removed = removed; sample.correct = sample.pred_idx == label; break; }
            SamplesChanged?.Invoke(); Changed?.Invoke();
        }
        public Rect UV(int index)
        {
            if (atlasMetadata != null)
            {
                int cell = atlasCells[index];
                float w = atlasMetadata.cell[0] / (float)Atlas.width, h = atlasMetadata.cell[1] / (float)Atlas.height;
                // Generated atlas indexes start at the top; Unity texture UVs start at the bottom.
                return new Rect(cell % atlasMetadata.cols * w, 1f - (cell / atlasMetadata.cols + 1) * h, w, h);
            }
            int rows = Mathf.CeilToInt(Data.samples.Length / (float)AtlasColumns);
            return new Rect((index % AtlasColumns) / (float)AtlasColumns, (index / AtlasColumns) / (float)rows, 1f / AtlasColumns, 1f / rows);
        }
        void BuildAtlas()
        {
            const int cell = 32;
            int rows = Mathf.CeilToInt(Data.samples.Length / (float)AtlasColumns);
            Atlas = new Texture2D(cell * AtlasColumns, cell * rows, TextureFormat.RGBA32, false) { name = "Mock image atlas", filterMode = FilterMode.Point };
            // Deliberately synthetic thumbnails: deterministic class silhouettes, no downloads.
            for (int i = 0; i < Data.samples.Length; i++)
            {
                var color = Color.HSVToRGB(Data.samples[i].pred_idx / (float)Data.classes.Length, .65f, .85f);
                for (int y = 0; y < cell; y++) for (int x = 0; x < cell; x++)
                {
                    bool shape = (x - 16) * (x - 16) + (y - 16) * (y - 16) < 45 + i % 90;
                    Atlas.SetPixel(i % AtlasColumns * cell + x, i / AtlasColumns * cell + y, shape ? color : new Color(.08f + x * .002f, .12f, .19f));
                }
            }
            Atlas.Apply(false, true);
        }
        void OnNetStatus(ForgeNetStatus status)
        {
            if (useLiveEvents && status == ForgeNetStatus.Live) replay = false;
        }
        void OnLiveRunState(global::MlRunState state)
        {
            if (!useLiveEvents || ForgeNet.Status != ForgeNetStatus.Live || state == null) return;
            replay = false; SetRunState(state.status, state.totalEpochs);
        }
        void OnLiveMetrics(global::MlMetrics tick)
        {
            if (!useLiveEvents || ForgeNet.Status != ForgeNetStatus.Live || tick == null) return;
            replay = false;
            ApplyMetrics(new MLMetrics { run_id = tick.runId, epoch = tick.epoch, step = tick.step, train_loss = tick.trainLoss, train_acc = tick.trainAcc, val_loss = tick.valLoss, val_acc = tick.valAcc, lr = tick.lr });
        }
        void OnLiveOverfit(global::MlOverfitAlert alert)
        {
            if (!useLiveEvents || ForgeNet.Status != ForgeNetStatus.Live || alert == null) return;
            replay = false; Overfit = true; OverfitReason = alert.reason; Changed?.Invoke();
        }
        void OnLiveSampleUpdated(global::MlSampleUpdated sample)
        {
            if (!useLiveEvents || ForgeNet.Status != ForgeNetStatus.Live || sample == null) return;
            replay = false; ApplySampleUpdate(sample.id, sample.labelIdx, sample.removed);
        }
        void OnDestroy() { if (ownsAtlas && Atlas != null) Destroy(Atlas); }
    }
}
