using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Forge.ML
{
    [Serializable] public class MLSample
    {
        public string id, name, path, split, text;  // text: a text classifier's sample (GET /ml/dataset kind "text")
        public int label_idx, true_idx, pred_idx, width, height, channels;
        public long file_size;
        public float[] probs;
        public bool correct;
        [NonSerialized] public bool removed;
    }
    [Serializable] public class MLLayer { public string name, type; public int[] in_shape, out_shape; public int @params; }
    // A metric the run didn't log arrives as null (a backtest has no loss, most logs no lr): it becomes NaN, so one
    // missing value doesn't drop the whole tick. `extra` holds the run's other numbers (val_sharpe, r2, ...).
    [Serializable] public class MLMetrics
    {
        public string run_id; public int epoch, step;
        [JsonConverter(typeof(MLNullAsNaN))] public float train_loss, train_acc, val_loss, val_acc, lr;
        public Dictionary<string, float?> extra;  // JsonUtility skips it; Newtonsoft (hub, mocks) fills it
        public bool HasLoss => !float.IsNaN(train_loss) || !float.IsNaN(val_loss);
        public bool HasAccuracy => !float.IsNaN(train_acc) || !float.IsNaN(val_acc);
    }
    public class MLNullAsNaN : JsonConverter<float>
    {
        public override float ReadJson(JsonReader reader, Type type, float existing, bool hasExisting, JsonSerializer serializer) =>
            reader.TokenType == JsonToken.Null ? float.NaN : Convert.ToSingle(reader.Value);
        public override void WriteJson(JsonWriter writer, float value, JsonSerializer serializer)
        {
            if (float.IsNaN(value)) writer.WriteNull(); else writer.WriteValue(value);
        }
    }
    [Serializable] public class MLFixture { public string[] classes; public MLSample[] samples; public MLLayer[] layers; public MLMetrics[] metrics; [NonSerialized] public int[][] confusion; }
    [Serializable] public class MLProbeSample { public string id; public int label_idx = -1, pred_idx = -1; public float[] probs; public float y_pred; }
    [Serializable] public class MLProbePayload { public string run_id; public int epoch; public MLProbeSample[] samples; }
    [Serializable] public class MLClassAccuracyPayload { public string run_id; public int epoch; public float?[] per_class; }
    public class MLRunInfo { public string run_id, status; public int? epochs; }
    [Serializable] public class MLTrainResponse { public string run_id; }
    [Serializable] public class MLGitHubImport
    {
        public string import_id, repository, url, @ref, format, model, dataset, reason;
        public string mode, path, runs;  // mode "preset": trains the checked-in CIFAR-10 copy; "clone": the repo's own code
        public bool supported;
        public string[] files;
    }
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
        public string GitHubImportId { get; private set; }
        // The open project's setup, from ml.project_state: needs_approval and confirm_type wait for the user
        // (Accept / Skip, or a type), shown on the Training panel. Source says whose code a Train press runs.
        public string ProjectStatus { get; private set; }
        public string ProjectRepository { get; private set; }
        public string[] ProjectCandidates { get; private set; }
        public string ProjectSource { get; private set; }
        // Setup is working, or waiting on the user (Accept / Skip, a type): no new import and no Train until it's done.
        public bool ProjectSettingUp => ProjectStatus == "scanning" || ProjectStatus == "classifying" || ProjectStatus == "confirm_type"
            || ProjectStatus == "adapting" || ProjectStatus == "checking" || ProjectStatus == "needs_approval";
        // One line for the Training panel: why there's no live data, or what Train is doing.
        public string Notice { get; private set; }
        // POST /ml/train sent, no answer yet. Train ignores presses until it clears, or while a run trains.
        public bool TrainPending { get; private set; }
        public bool TrainBusy => TrainPending || (IsLive && RunStatus == "training");
        // Live hub with ML, but the open repo has no image dataset (no classes/atlas): the image panels sit out.
        public bool NoImageData { get; private set; }
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
        bool ownsAtlas, loading, reloadQueued, importPending;  // importPending: POST /ml/github/import sent, no answer yet
        string atlasUrl;
        IDisposable epochPreds, metricsEvents;
        IDisposable classAccEvents;
        IDisposable probeEvents, projectEvents;
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
            // By name, not ForgeEvents.OnMlMetrics: Core's MlMetrics can't hold a null metric or `extra`, and drops the tick.
            metricsEvents = ForgeNet.On("ml.metrics", OnLiveMetrics);
            ForgeEvents.OnMlOverfitAlert += OnLiveOverfit;
            ForgeEvents.OnMlSampleUpdated += OnLiveSampleUpdated;
            ForgeEvents.OnMlResultsReady += OnLiveResultsReady;
            ForgeEvents.OnMlGraphReady += OnLiveGraphReady;
            ForgeNet.StatusChanged += OnNetStatus;
            // ML_PLAN 7.5 live dot wall; not a Core ForgeEvents type, so subscribed by name.
            epochPreds = ForgeNet.On("ml.epoch_preds", OnLiveEpochPreds);
            classAccEvents = ForgeNet.On("ml.class_acc", OnLiveClassAccuracy);
            probeEvents = ForgeNet.On("ml.probe", OnLiveProbe);
            projectEvents = ForgeNet.On("ml.project_state", OnProjectState);
        }
        void Start() { if (IsLive) OnNetStatus(ForgeNetStatus.Live); }
        void OnDisable()
        {
            ForgeEvents.OnMlRunState -= OnLiveRunState;
            metricsEvents?.Dispose(); metricsEvents = null;
            ForgeEvents.OnMlOverfitAlert -= OnLiveOverfit;
            ForgeEvents.OnMlSampleUpdated -= OnLiveSampleUpdated;
            ForgeEvents.OnMlResultsReady -= OnLiveResultsReady;
            ForgeEvents.OnMlGraphReady -= OnLiveGraphReady;
            ForgeNet.StatusChanged -= OnNetStatus;
            epochPreds?.Dispose(); epochPreds = null;
            classAccEvents?.Dispose(); classAccEvents = null;
            probeEvents?.Dispose(); probeEvents = null;
            projectEvents?.Dispose(); projectEvents = null;
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
            replayStarted = Time.unscaledTime;
            nextTick = Time.unscaledTime + secondsPerEpoch;
        }
        void Update()
        {
            if (!replay || Done) return;
            if (timeline != null)
            {
                float elapsed = (Time.unscaledTime - replayStarted) * Mathf.Max(.01f, replaySpeed);
                while (nextEvent < timeline.Length && timeline[nextEvent].t <= elapsed) ApplyReplayEvent(timeline[nextEvent++]);
                return;
            }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + Mathf.Max(.1f, secondsPerEpoch);
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
        void RestartMock() { EpochIndex = -1; Done = Overfit = false; OverfitReason = null; RunStatus = "idle"; nextEvent = 0; replayStarted = Time.unscaledTime; nextTick = Time.unscaledTime + secondsPerEpoch; Changed?.Invoke(); }
        // Live hub: start a real training run on the laptop (ML_PLAN 7.4 POST /ml/train). Its events come back
        // over /ws, so the curves, dot wall and live graphs fill in as it trains.
        public void Train(int epochs)
        {
            if (!IsLive)
            {
                Notice = "Training needs the hub: start it on the laptop and set ForgeNet's Hub Url.";
                Changed?.Invoke();
                return;
            }
            if (TrainBusy) return;  // a second POST only gets the hub's 409 "still training"
            if (ProjectSettingUp)  // the hub refuses too: Train before Accept would run the script without its forge calls
            {
                Notice = ProjectStatus == "needs_approval" ? "Accept or Skip the patch first, then Train." : $"{ProjectRepository} is still being set up.";
                Changed?.Invoke(); return;
            }
            TrainPending = true; Notice = "Starting training..."; Changed?.Invoke();
            if (!string.IsNullOrEmpty(GitHubImportId))
            {
                ForgeNet.Post<MLTrainResponse>("/ml/github/train", new { import_id = GitHubImportId, epochs }, response =>
                {
                    TrainPending = false; Changed?.Invoke();
                    if (response != null) { RunId = response.run_id; LoadFromHub(RunId); }
                }, e => { TrainPending = false; Notice = "Train failed: " + HubDetail(e); Changed?.Invoke(); Debug.LogWarning("[ML] Train failed: " + e); });
                return;
            }
            ForgeNet.Post("/ml/train", new { epochs }, _ => { TrainPending = false; Changed?.Invoke(); },
                e => { TrainPending = false; Notice = "Training didn't start: " + HubDetail(e); Changed?.Invoke(); Debug.LogWarning("[ML] Train failed: " + e); });
        }
        public void ImportGitHub(string url)
        {
            if (!IsLive)
            {
                Notice = "GitHub import needs the hub: start it on the laptop and set ForgeNet's Hub Url.";
                Changed?.Invoke(); return;
            }
            if (string.IsNullOrWhiteSpace(url)) { Notice = "Paste a public GitHub repository URL first."; Changed?.Invoke(); return; }
            if (ProjectSettingUp)
            {
                Notice = ProjectStatus == "needs_approval" ? $"{ProjectRepository} is waiting for Accept or Skip first."
                    : $"Still setting up {ProjectRepository}: one import at a time.";
                Changed?.Invoke(); return;
            }
            if (importPending) return;  // a second press before the hub answered the first
            importPending = true;
            Notice = "Reading the public GitHub repository..."; Changed?.Invoke();
            ForgeNet.Post<MLGitHubImport>("/ml/github/import", new { url = url.Trim() }, result =>
            {
                importPending = false;
                GitHubImportId = result != null && result.supported ? result.import_id : null;
                if (result == null) Notice = "GitHub import returned no result.";
                else if (!result.supported) Notice = $"Unsupported repository: {result.reason}";
                else if (result.mode == "clone")
                {
                    // Setup follows on ml.project_state (OnProjectState); Train waits until it's ready.
                    ProjectRepository = result.repository; ProjectSource = $"Runs the code from {result.repository}";
                    Notice = $"Cloned {result.repository}. Setting it up...";
                }
                else
                {
                    ProjectRepository = result.repository; ProjectSource = "Runs the checked-in CIFAR-10 copy";
                    Notice = $"{result.repository} has the preset CIFAR-10 layout: Train runs our checked-in copy of it.";
                    LoadFromHub(null);
                }
                Changed?.Invoke();
            }, error =>
            {
                importPending = false;
                // 409: something else is running (setup, training); the import already there stays the one Train uses.
                if (error == null || !error.StartsWith("409")) GitHubImportId = null;
                Notice = "GitHub import failed: " + HubDetail(error); Changed?.Invoke();
            });
        }
        // ml.project_state {path, status, archetype?, candidates?, summary?, error?, repository?}: one line per setup step.
        void OnProjectState(Newtonsoft.Json.Linq.JToken p)
        {
            if (!IsLive || p == null) return;
            string status = p.Value<string>("status"), path = p.Value<string>("path") ?? "";
            string name = p.Value<string>("repository") ?? System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
            ProjectStatus = status; ProjectRepository = name;
            ProjectSource = p["repository"] != null ? $"Runs the code from {name}" : $"Runs {name}";
            ProjectCandidates = status == "confirm_type" ? p["candidates"]?.ToObject<string[]>() : null;
            switch (status)
            {
                case "scanning": Notice = $"Setting up {name}: reading the code..."; break;
                case "classifying": Notice = $"Setting up {name}: working out what kind of project it is..."; break;
                case "confirm_type": Notice = $"Not sure what kind of project {name} is: pick its type."; break;
                case "adapting": Notice = $"Setting up {name}: writing the glue code..."; break;
                case "checking": Notice = $"Setting up {name}: fixing the glue code (try {p.Value<int?>("round") ?? 2})..."; break;
                case "needs_approval": Notice = $"{name} needs a small patch so training streams live: look at it, then Accept or Skip."; break;
                case "ready": Notice = $"{name} is ready to train."; LoadFromHub(null); break;
                case "failed": Notice = $"Setting up {name} failed: {p.Value<string>("error")}"; break;
            }
            Changed?.Invoke();
        }
        public void ConfirmProjectType(string archetype) =>
            ForgeNet.Post("/ml/project/type", new { archetype }, null, e => { Notice = "Couldn't set the type: " + HubDetail(e); Changed?.Invoke(); });
        // The proposed patch in the laptop's VS Code (the CODE screen), or a summary when VS Code isn't there.
        public void ShowPatch() =>
            ForgeNet.Post("/ml/project/diff", null, text =>
            {
                bool opened = false; int files = 0;
                try { var o = Newtonsoft.Json.Linq.JObject.Parse(text); opened = o.Value<bool>("opened"); files = (o["files"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0; }
                catch (Exception) { }
                Notice = opened ? "The patch is open in VS Code on the laptop (the CODE screen)." : $"No VS Code on the laptop: the patch adds forge calls to {files} file(s).";
                Changed?.Invoke();
            }, e => { Notice = "Couldn't open the patch: " + HubDetail(e); Changed?.Invoke(); });
        // The user's confirmation step (PROJECT_CONTEXT 12): only Accept writes the patch into the repo.
        public void ApprovePatch(bool accept) =>
            ForgeNet.Post("/ml/project/patch", new { accept }, null, e => { Notice = "The patch wasn't applied: " + HubDetail(e); Changed?.Invoke(); });
        // "422 HTTP/1.1 422 Unprocessable Entity {"detail":"..."}" -> the hub's own sentence, for the panel's banner.
        static string HubDetail(string error)
        {
            int brace = error == null ? -1 : error.IndexOf('{');
            if (brace < 0) return error;
            try { return Newtonsoft.Json.Linq.JObject.Parse(error.Substring(brace)).Value<string>("detail") ?? error; }
            catch (Exception) { return error; }
        }
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
                Data.metrics = new MLMetrics[0]; EpochIndex = -1; Done = Overfit = false; OverfitReason = null; Notice = null;
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
        // ForgeNet.Get<T> parses inside its callback: a payload that doesn't fit T (a regression row's null label)
        // throws there, the request coroutine dies, and LoadRoutine waiting on it never resumes, so `loading`
        // stays set and every later reload is lost. Parse here and turn the error into an ordinary failure.
        static Coroutine Fetch<T>(string path, Action<T> ok, Action<string> fail) =>
            ForgeNet.Get(path, text =>
            {
                T value;
                try { value = JsonConvert.DeserializeObject<T>(text); }
                catch (Exception e) { fail?.Invoke($"{path}: {e.Message}"); return; }
                ok(value);
            }, fail);
        System.Collections.IEnumerator LoadRoutine(string runId)
        {
            loading = true;
            string error = null;
            Action<string> fail = e => error ??= e;
            string[] classes = null; MLDatasetEntry[] entries = null; MLAtlasMetadata meta = null; MLGraph graph = null;
            MLRunInfo[] runs = null; MLResults results = null; MLConfusion confusion = null; MLMetricsHistory history = null; byte[] png = null;
            yield return Fetch<string[]>("/ml/classes", v => classes = v, fail);
            yield return Fetch<MLDatasetEntry[]>("/ml/dataset?split=val", v => entries = v, fail);
            yield return Fetch<MLAtlasMetadata>("/ml/atlas?split=val", v => meta = v, fail);
            yield return Fetch<MLGraph>("/ml/graph", v => graph = v, _ => { });  // optional: keep the current model
            if (string.IsNullOrEmpty(runId))
            {
                yield return Fetch<MLRunInfo[]>("/ml/runs", v => runs = v, _ => { });
                if (runs != null) foreach (var run in runs) if (run.status == "done") { runId = run.run_id; break; }
            }
            if (!string.IsNullOrEmpty(runId))
            {
                yield return Fetch<MLResults>($"/ml/results/{runId}", v => results = v, _ => { });
                yield return Fetch<MLConfusion>($"/ml/confusion/{runId}", v => confusion = v, _ => { });
                yield return Fetch<MLMetricsHistory>($"/ml/metrics/{runId}", v => history = v, _ => { });
            }
            if (error == null && meta != null && meta.atlas_url != atlasUrl) yield return ForgeNet.GetBytes(meta.atlas_url, v => png = v, fail);
            bool noImages = error != null || classes == null || entries == null || meta == null || entries.Length == 0;
            bool hubHasMl = false;
            if (noImages) yield return ForgeNet.Get("/ml/project", _ => hubHasMl = true, _ => { });
            loading = false;
            if (noImages && hubHasMl)
            {
                // The hub serves ML, but this repo isn't an image dataset (regression, a backtest, ...): no image
                // demo; its runs show their own metrics, and the layout brings the panels that fit it.
                NoImageData = true; Notice = null; replay = false; Changed?.Invoke();
            }
            else if (noImages)
            {
                // Connected to a hub without the ML router (or an empty repo): play the demo run so the panels
                // aren't blank, and say why on the Training panel.
                Debug.LogWarning("[ML] Hub data unavailable, keeping the current data: " + (error ?? "empty dataset"));
                Notice = (error != null ? "No ML data from the hub." : "The hub's dataset is empty.") + " Showing the demo run.";  // raw error: logged above
                if (!HubData) { replay = true; RestartMock(); }
                else Changed?.Invoke();
            }
            else
            {
                if (Notice != null && Notice.StartsWith("No ML data")) Notice = null;
                NoImageData = false;
                ApplyHubData(classes, entries, meta, graph, runId, results, confusion, history, png);
            }
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
                sample.path = entry.path; sample.text = entry.text; sample.split = string.IsNullOrEmpty(entry.split) ? "val" : entry.split;
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
            Raise(Reloaded);
            if (history?.class_acc != null)
                foreach (var classAccuracy in history.class_acc)
                    try { ApplyClassAccuracy(classAccuracy); } catch (Exception e) { Debug.LogException(e); }
            if (history?.probe != null) Raise(ProbeUpdated, history.probe);
            Raise(SamplesChanged); Raise(Changed);
        }
        // A hub load must reach every panel even if one handler throws. ponytail: GetInvocationList allocates, so
        // only for this one-off load, not the per-frame events.
        static void Raise(Action handlers)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception e) { Debug.LogException(e); }
        }
        static void Raise<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
                try { handler(value); } catch (Exception e) { Debug.LogException(e); }
        }
        void OnLiveMetrics(Newtonsoft.Json.Linq.JToken payload)
        {
            if (!useLiveEvents || ForgeNet.Status != ForgeNetStatus.Live || payload == null) return;
            MLMetrics tick;
            try { tick = ReadMetrics(payload); }
            catch (Exception e) { Debug.LogWarning("[ML] Unreadable ml.metrics: " + e.Message); return; }
            replay = false;
            ApplyMetrics(tick);
        }
        public static MLMetrics ReadMetrics(Newtonsoft.Json.Linq.JToken payload) => payload.ToObject<MLMetrics>();
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
