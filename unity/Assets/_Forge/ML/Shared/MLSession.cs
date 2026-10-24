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
    [Serializable] public class MLProbeSample { public string id; public int label_idx = -1, pred_idx = -1; public float[] probs; public float y_pred; }
    [Serializable] public class MLProbePayload { public string run_id; public int epoch; public MLProbeSample[] samples; }
    [Serializable] public class MLClassAccuracyPayload { public string run_id; public int epoch; public float?[] per_class; }
    public class MLRunInfo { public string run_id, status; public int? epochs; }
    // GET /ml/metrics/{run_id}: a finished run's curves, so panels opened after training still show them.
    public class MLMetricsHistory
    {
        public string run_id;
        public int total_epochs;
        public MLMetrics[] metrics;
        public MLReplayPayload overfit;
        public MLClassAccuracyPayload[] class_acc;
        public MLProbePayload probe;
    }

    // Local presentation state. Starts on the generated mocks; once ForgeNet is Live it loads the hub's dataset,
    // atlas, graph and latest results (LoadFromHub) and follows Core's ml.* events.
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
        public bool IsLive => useLiveEvents && ForgeNet.Status == ForgeNetStatus.Live;
        public bool HubData { get; private set; }
        public string RunId { get; private set; }
        public event Action Changed;
        // Data and Atlas were replaced from the hub: rebind textures, rebuild anything built from Data at Start.
        public event Action Reloaded;
        public event Action SamplesChanged;
        public event Action<MLMetrics> Metrics;
        public event Action<int, float[]> ClassAccuracyUpdated;
        public event Action<MLProbePayload> ProbeUpdated;
        public event Action<int, int> PairSelected;
        public event Action<string, int> RelabelRequested;
        public event Action<string> RemoveRequested;
        float nextTick, replayStarted;
        MLAtlasMetadata atlasMetadata;
        MLReplayEvent[] timeline;
        int[] atlasCells;
        int nextEvent;
        bool ownsAtlas, loading, reloadQueued;
        string atlasUrl;
        IDisposable epochPreds;
        IDisposable classAccEvents;
        IDisposable probeEvents;
        public const int AtlasColumns = 8;
        public bool HasReplayLiveGraphs => timeline != null && Array.Exists(timeline, e => e != null && (e.type == "ml.class_acc" || e.type == "ml.probe"));

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
            ForgeEvents.OnMlResultsReady += OnLiveResultsReady;
            ForgeEvents.OnMlGraphReady += OnLiveGraphReady;
            ForgeNet.StatusChanged += OnNetStatus;
            // ML_PLAN 7.5 live dot wall; not a Core ForgeEvents type, so subscribed by name.
            epochPreds = ForgeNet.On("ml.epoch_preds", OnLiveEpochPreds);
            classAccEvents = ForgeNet.On("ml.class_acc", OnLiveClassAccuracy);
            probeEvents = ForgeNet.On("ml.probe", OnLiveProbe);
        }
        void Start() { if (IsLive) OnNetStatus(ForgeNetStatus.Live); }
        void OnDisable()
        {
            ForgeEvents.OnMlRunState -= OnLiveRunState;
            ForgeEvents.OnMlMetrics -= OnLiveMetrics;
            ForgeEvents.OnMlOverfitAlert -= OnLiveOverfit;
            ForgeEvents.OnMlSampleUpdated -= OnLiveSampleUpdated;
            ForgeEvents.OnMlResultsReady -= OnLiveResultsReady;
            ForgeEvents.OnMlGraphReady -= OnLiveGraphReady;
            ForgeNet.StatusChanged -= OnNetStatus;
            epochPreds?.Dispose(); epochPreds = null;
            classAccEvents?.Dispose(); classAccEvents = null;
            probeEvents?.Dispose(); probeEvents = null;
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
                case "ml.epoch_preds": ApplyEpochPredictions(p.pred_idx, p.epoch); break;
                case "ml.class_acc": ApplyClassAccuracy(new MLClassAccuracyPayload { run_id = p.run_id, epoch = p.epoch, per_class = p.per_class }); break;
                case "ml.probe": ProbeUpdated?.Invoke(new MLProbePayload { run_id = p.run_id, epoch = p.epoch, samples = p.samples }); Changed?.Invoke(); break;
            }
        }

        public void ApplyMetrics(MLMetrics tick)
        {
            int metricIndex = Mathf.Max(0, tick.epoch - 1);
            if (metricIndex >= Data.metrics.Length) Array.Resize(ref Data.metrics, metricIndex + 1);
            EpochIndex = metricIndex;
            Data.metrics[EpochIndex] = tick;
            if (EpochIndex >= 2 && Data.metrics[EpochIndex - 2] != null && Data.metrics[EpochIndex - 1] != null)
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
        // Live hub: replay the run on /ws (ML_PLAN 7.4 /ml/replay); the curves restart from its ml.run_state.
        public void Restart()
        {
            if (HubData && IsLive && !string.IsNullOrEmpty(RunId))
            {
                ForgeNet.Post("/ml/replay", new { run_id = RunId, speed = 4f }, null, e => Debug.LogWarning("[ML] Replay failed: " + e));
                return;
            }
            RestartMock();
        }
        void RestartMock() { EpochIndex = -1; Done = Overfit = false; OverfitReason = null; RunStatus = "idle"; nextEvent = 0; replayStarted = Time.time; nextTick = Time.time + secondsPerEpoch; Changed?.Invoke(); }
        public void SelectPair(int truth, int predicted) => PairSelected?.Invoke(truth, predicted);
        // Live: the hub writes labels_override.json and echoes ml.sample_updated, which applies the change.
        public void Relabel(MLSample sample, int label)
        {
            if (HubData && IsLive) { ForgeNet.Post($"/ml/samples/{sample.id}/relabel", new { label_idx = label }, null, e => Debug.LogWarning("[ML] Relabel failed: " + e)); return; }
            if (!replay) { RelabelRequested?.Invoke(sample.id, label); return; }
            ApplySampleUpdate(sample.id, label, false);
        }
        public void Remove(MLSample sample)
        {
            if (HubData && IsLive) { ForgeNet.Post($"/ml/samples/{sample.id}/remove", null, null, e => Debug.LogWarning("[ML] Remove failed: " + e)); return; }
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
            if (!useLiveEvents || status != ForgeNetStatus.Live) return;
            replay = false; LoadFromHub(null);
        }
        void OnLiveRunState(global::MlRunState state)
        {
            if (!IsLive || state == null) return;
            replay = false;
            bool newRun = !string.IsNullOrEmpty(state.runId) && state.runId != RunId;
            if (newRun || (state.status == "training" && state.epoch <= 0))
            {
                // A new run, or a replay of the current one: start the curves over.
                if (newRun) RunId = state.runId;
                Data.metrics = new MLMetrics[0]; EpochIndex = -1; Done = Overfit = false; OverfitReason = null;
            }
            SetRunState(state.status, state.totalEpochs);
        }
        // ml.epoch_preds {run_id, epoch, pred_idx[]} in /ml/dataset order: red dots turn green as it learns.
        // Probabilities arrive only with the final results, so the stale ones are dropped until then.
        void OnLiveEpochPreds(Newtonsoft.Json.Linq.JToken payload)
        {
            if (!IsLive || !HubData || payload == null || !(payload["pred_idx"] is Newtonsoft.Json.Linq.JArray preds)) return;
            string runId = payload["run_id"]?.ToObject<string>();
            if (!string.IsNullOrEmpty(runId) && !string.IsNullOrEmpty(RunId) && runId != RunId) return;
            int epoch = payload["epoch"] != null ? payload["epoch"].ToObject<int>() : Mathf.Max(1, EpochIndex + 1);
            ApplyEpochPredictions(preds.ToObject<int[]>(), epoch);
        }
        void OnLiveClassAccuracy(Newtonsoft.Json.Linq.JToken payload)
        {
            if (!IsLive || !HubData || payload == null) return;
            var accuracy = payload.ToObject<MLClassAccuracyPayload>();
            if (accuracy == null || !MatchesRun(accuracy.run_id)) return;
            ApplyClassAccuracy(accuracy);
        }
        bool MatchesRun(string runId) => string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(RunId) || runId == RunId;
        public void ApplyClassAccuracy(MLClassAccuracyPayload payload)
        {
            if (payload == null || !MatchesRun(payload.run_id) || Data?.classes == null) return;
            var values = new float[Data.classes.Length];
            for (int i = 0; i < values.Length; i++) values[i] = -1f;
            if (payload.per_class != null)
                for (int i = 0; i < Mathf.Min(values.Length, payload.per_class.Length); i++)
                    if (payload.per_class[i].HasValue) values[i] = Mathf.Clamp01(payload.per_class[i].Value);
            ClassAccuracyUpdated?.Invoke(payload.epoch, values);
            Changed?.Invoke();
        }
        public void ApplyEpochPredictions(int[] predictions, int epoch)
        {
            if (predictions == null || Data?.samples == null || Data.classes == null) return;
            int n = Mathf.Min(predictions.Length, Data.samples.Length);
            var seen = new int[Data.classes.Length]; var correct = new int[Data.classes.Length];
            for (int i = 0; i < n; i++)
            {
                var sample = Data.samples[i]; int predicted = predictions[i];
                sample.pred_idx = predicted; sample.probs = null; sample.correct = predicted == sample.true_idx;
                if (sample.removed || sample.true_idx < 0 || sample.true_idx >= seen.Length) continue;
                seen[sample.true_idx]++;
                if (sample.correct) correct[sample.true_idx]++;
            }
            var accuracy = new float[seen.Length];
            for (int c = 0; c < accuracy.Length; c++) accuracy[c] = seen[c] == 0 ? -1f : correct[c] / (float)seen[c];
            ClassAccuracyUpdated?.Invoke(epoch, accuracy);
            SamplesChanged?.Invoke(); Changed?.Invoke();
        }
        void OnLiveProbe(Newtonsoft.Json.Linq.JToken payload)
        {
            if (!IsLive || !HubData || payload == null) return;
            var probe = payload.ToObject<MLProbePayload>();
            if (probe == null || !MatchesRun(probe.run_id)) return;
            ProbeUpdated?.Invoke(probe); Changed?.Invoke();
        }
        void OnLiveResultsReady(global::MlResultsReady ready) { if (IsLive && ready != null) LoadFromHub(ready.runId); }
        void OnLiveGraphReady(global::MlGraphReady _) { if (IsLive) LoadFromHub(RunId); }

        // Fetches the hub's classes, dataset, atlas, graph and the results of runId (or the latest finished run),
        // then swaps them in. On any failure the current data stays.
        public void LoadFromHub(string runId)
        {
            if (loading) { reloadQueued = true; return; }
            StartCoroutine(LoadRoutine(runId));
        }
        System.Collections.IEnumerator LoadRoutine(string runId)
        {
            loading = true;
            string error = null;
            Action<string> fail = e => error ??= e;
            string[] classes = null; MLDatasetEntry[] entries = null; MLAtlasMetadata meta = null; MLGraph graph = null;
            MLRunInfo[] runs = null; MLResults results = null; MLConfusion confusion = null; MLMetricsHistory history = null; byte[] png = null;
            yield return ForgeNet.Get<string[]>("/ml/classes", v => classes = v, fail);
            yield return ForgeNet.Get<MLDatasetEntry[]>("/ml/dataset?split=val", v => entries = v, fail);
            yield return ForgeNet.Get<MLAtlasMetadata>("/ml/atlas?split=val", v => meta = v, fail);
            yield return ForgeNet.Get<MLGraph>("/ml/graph", v => graph = v, _ => { });  // optional: keep the current model
            if (string.IsNullOrEmpty(runId))
            {
                yield return ForgeNet.Get<MLRunInfo[]>("/ml/runs", v => runs = v, _ => { });
                if (runs != null) foreach (var run in runs) if (run.status == "done") { runId = run.run_id; break; }
            }
            if (!string.IsNullOrEmpty(runId))
            {
                yield return ForgeNet.Get<MLResults>($"/ml/results/{runId}", v => results = v, _ => { });
                yield return ForgeNet.Get<MLConfusion>($"/ml/confusion/{runId}", v => confusion = v, _ => { });
                yield return ForgeNet.Get<MLMetricsHistory>($"/ml/metrics/{runId}", v => history = v, _ => { });
            }
            if (error == null && meta != null && meta.atlas_url != atlasUrl) yield return ForgeNet.GetBytes(meta.atlas_url, v => png = v, fail);
            loading = false;
            if (error != null || classes == null || entries == null || meta == null || entries.Length == 0)
                Debug.LogWarning("[ML] Hub data unavailable, keeping the current data: " + (error ?? "empty dataset"));
            else ApplyHubData(classes, entries, meta, graph, runId, results, confusion, history, png);
            if (reloadQueued) { reloadQueued = false; LoadFromHub(RunId); }
        }
        void ApplyHubData(string[] classes, MLDatasetEntry[] entries, MLAtlasMetadata meta, MLGraph graph, string runId,
                          MLResults results, MLConfusion confusion, MLMetricsHistory history, byte[] png)
        {
            if (png != null)
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Hub image atlas", filterMode = FilterMode.Point };
                if (!texture.LoadImage(png, true)) { Destroy(texture); Debug.LogWarning("[ML] Hub atlas is not a valid PNG."); return; }
                if (ownsAtlas && Atlas != null) Destroy(Atlas);
                Atlas = texture; ownsAtlas = true; atlasUrl = meta.atlas_url;
            }
            var byId = new System.Collections.Generic.Dictionary<string, MLSample>();
            if (results?.samples != null) foreach (var s in results.samples) byId[s.id] = s;
            var samples = new MLSample[entries.Length]; var cells = new int[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                // No results yet: the sample shows as its own label with no prediction.
                var sample = byId.TryGetValue(entry.id, out var r) ? r : new MLSample { id = entry.id, true_idx = entry.label_idx, pred_idx = -1 };
                sample.label_idx = sample.true_idx = entry.label_idx;
                sample.removed = entry.removed;
                sample.correct = sample.pred_idx == entry.label_idx;
                sample.name = string.IsNullOrEmpty(entry.name) ? entry.id : entry.name;
                sample.path = entry.path; sample.split = string.IsNullOrEmpty(entry.split) ? "val" : entry.split;
                sample.width = entry.width > 0 ? entry.width : 32; sample.height = entry.height > 0 ? entry.height : 32;
                sample.channels = entry.channels > 0 ? entry.channels : 3; sample.file_size = entry.file_size;
                samples[i] = sample; cells[i] = meta.index != null && meta.index.TryGetValue(entry.id, out int cell) ? cell : 0;
            }
            if (runId != RunId) { RunId = runId; Data.metrics = new MLMetrics[0]; EpochIndex = -1; Overfit = false; OverfitReason = null; }
            // A finished run with no live ticks seen: take its curves from the hub's history.
            if (results != null && history?.metrics != null && history.metrics.Length > 0 && Data.metrics.Length == 0)
            {
                Data.metrics = history.metrics; EpochIndex = history.metrics.Length - 1;
                TotalEpochs = Mathf.Max(history.total_epochs, history.metrics.Length);
                if (history.overfit != null) { Overfit = true; OverfitReason = history.overfit.reason; }
            }
            Data = new MLFixture
            {
                classes = classes, samples = samples, layers = graph?.layers ?? Data.layers, metrics = Data.metrics,
                confusion = confusion?.matrix ?? Data.confusion
            };
            atlasMetadata = meta; atlasCells = cells; timeline = null; HubData = true;
            if (results != null) { Done = true; RunStatus = "done"; }
            Debug.Log($"[ML] Loaded {samples.Length} samples from the hub" + (runId != null ? $", results of run {runId}" : ", no results yet"));
            Reloaded?.Invoke();
            if (history?.class_acc != null)
                foreach (var classAccuracy in history.class_acc) ApplyClassAccuracy(classAccuracy);
            if (history?.probe != null) ProbeUpdated?.Invoke(history.probe);
            SamplesChanged?.Invoke(); Changed?.Invoke();
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
