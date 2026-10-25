using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// The Neural Net Lab's network, data and training loop (TensorFlow Playground style), one per scene. It lives
    /// outside the panel so Albie can drive it with the panel folded: the hub sends <c>ml.nn_command</c> (proposed,
    /// ML_PLAN 7.4) and this posts its state to <c>POST /ml/nn/state</c> after each command and once a second while it
    /// changes, with each neuron's importance (test accuracy if it were switched the other way) so Albie can say why.
    /// Training is one epoch per frame while playing: 250 points through a network of a few dozen weights.
    /// </summary>
    public sealed class NeuralLabSim : MonoBehaviour
    {
        public const int MaxLayers = 6, MaxNeurons = 8, PointCount = 500, BatchSize = 10;
        // Paced so people watch it learn: the circle settles in about 5 s instead of a blink at 140 fps.
        public const float EpochsPerSecond = 30f;
        // Play stops by itself once test accuracy reaches this (pressing Play again trains on past it).
        public const float AutoPauseAccuracy = 0.92f;
        // ...or once it's stuck: no 1% gain in test accuracy for this many epochs (5 s). The demo starts here: the
        // default spiral on a small x, y network stalls around 55-60%, and Albie offers to fix it. He offers after an
        // auto-pause below 98% too: the circle and XOR on x, y stop at 92%, and his fix takes them to 100%.
        public const int StallEpochs = 150;
        public static readonly float[] Rates = { 0.0001f, 0.0003f, 0.001f, 0.003f, 0.01f, 0.03f, 0.1f, 0.3f, 1f };

        public NNDataset Dataset { get; private set; } = NNDataset.Spiral;
        public NNActivation Activation { get; private set; } = NNActivation.Tanh;
        public float LearningRate { get; private set; } = 0.03f;
        public float Noise { get; private set; }
        public readonly List<int> Hidden = new List<int> { 4, 2 };
        public readonly bool[] Features = new bool[NNData.AllFeatures.Length];
        public bool Playing { get; private set; }
        /// <summary>Epoch where Play stopped itself at <see cref="AutoPauseAccuracy"/>, or -1.</summary>
        public int AutoPausedAt { get; private set; } = -1;
        /// <summary>Epoch where Play stopped itself because test accuracy stopped improving, or -1.</summary>
        public int StalledAt { get; private set; } = -1;
        /// <summary>How many times Play stopped by itself (either way): the hub offers Albie's fix once per stop.</summary>
        public int StopCount { get; private set; }
        public int Epoch { get; private set; }
        public float TrainLoss { get; private set; }
        public float TestLoss { get; private set; }
        public float TrainAccuracy { get; private set; }
        public float TestAccuracy { get; private set; }
        public NNNetwork Net { get; private set; }
        public readonly List<NNPoint> Train = new List<NNPoint>(), Test = new List<NNPoint>();
        public readonly List<float> LossHistory = new List<float>(256);

        // The training point the 3D net and "How it learns" walk through together: a new one every PulseSeconds.
        public const float PulseSeconds = 4f;
        public int PulseIndex { get; private set; }
        public float PulseStart { get; private set; } = -10f;
        public int PulseCount { get; private set; }

        /// <summary>Something that happened while training (the "Training story" panel's markers).</summary>
        public struct StoryEvent { public int at; public string text; public bool albie; }
        /// <summary>Epochs trained since the Lab started, across rebuilds (the story's time axis).</summary>
        public int TotalEpochs { get; private set; }
        /// <summary>(total epoch, train accuracy, test accuracy), thinned out as it grows so it covers the whole session.</summary>
        public readonly List<Vector3> Story = new List<Vector3>(StoryMax);
        public readonly List<StoryEvent> Events = new List<StoryEvent>(MaxEvents);
        const int StoryMax = 240, MaxEvents = 12;
        int storyStride = 1;
        string[] lastConfig;  // what the last config event described, to say only what changed
        bool configPending, inCommand;

        /// <summary>The network was rebuilt (shape, features, dataset): panels redraw their graph.</summary>
        public event Action Rebuilt;
        /// <summary>Weights, losses or a neuron changed: panels refresh their numbers and pictures.</summary>
        public event Action Stepped;

        static NeuralLabSim instance;
        public static NeuralLabSim Instance
        {
            get
            {
                if (instance == null) instance = FindAnyObjectByType<NeuralLabSim>();
                if (instance == null) instance = new GameObject("Neural Lab").AddComponent<NeuralLabSim>();
                return instance;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<Workspace>() != null) _ = Instance;  // Main: Albie can drive it before the panel opens
        }

        float[] featureBuffer;
        int seed = 1, appliedSeq, lastPostedEpoch = -1;
        float nextPost, epochBudget;
        bool autoPause;  // armed by a Play that starts below the threshold; Albie's fix turns it off
        bool stallStop;  // armed by every Play (also one past 92%, so it still stops once stuck); Albie's fix turns it off
        float bestAccuracy; int bestEpoch;  // for the stall check
        bool dirty;
        IDisposable commands;
        readonly List<JObject> history = new List<JObject>();

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            EnsureReady();
            commands = ForgeNet.On("ml.nn_command", OnCommand);
            ForgeNet.StatusChanged += OnNetStatus;
        }
        void OnDestroy() { commands?.Dispose(); ForgeNet.StatusChanged -= OnNetStatus; if (instance == this) instance = null; }
        // A (re)connected hub knows nothing yet: report straight away so Albie's tools have a state.
        void OnNetStatus(ForgeNetStatus status) { if (status == ForgeNetStatus.Live) { dirty = true; nextPost = 0; lastPostedEpoch = -1; } }

        // Set up once; also a safety net when Awake's setup was skipped (a scene entering Play mid script reload).
        void EnsureReady()
        {
            if (Net != null) return;
            if (FeatureCount == 0) Features[(int)NNFeature.X] = Features[(int)NNFeature.Y] = true;
            Regenerate();
        }

        void Update()
        {
            EnsureReady();
            if (Train.Count > 0 && Time.unscaledTime - PulseStart >= PulseSeconds)
            {
                PulseIndex = (PulseIndex + 37) % Train.Count; PulseStart = Time.unscaledTime; PulseCount++;
            }
            if (configPending && !inCommand) FlushConfig(false);
            if (Playing)
            {
                epochBudget = Mathf.Min(epochBudget + Time.unscaledDeltaTime * EpochsPerSecond, 3f);
                if (epochBudget >= 1f)
                {
                    while (epochBudget >= 1f) { TrainEpoch(); epochBudget -= 1f; }
                    Evaluate(); RecordLoss(); dirty = true;
                    if (TestAccuracy >= bestAccuracy + 0.01f) { bestAccuracy = TestAccuracy; bestEpoch = Epoch; }
                    if (autoPause && TestAccuracy >= AutoPauseAccuracy)
                    {
                        Playing = autoPause = stallStop = false; AutoPausedAt = Epoch; StopCount++;
                        Remember("auto_pause", "", TestAccuracy);
                        Changed();
                    }
                    else if (stallStop && Epoch - bestEpoch >= StallEpochs)
                    {
                        Playing = autoPause = stallStop = false; StalledAt = Epoch; StopCount++;
                        Remember("stalled", "", TestAccuracy);  // the hub sees it and Albie offers his fix
                        Changed();
                    }
                    else Stepped?.Invoke();
                }
            }
            if (dirty && Time.unscaledTime >= nextPost && ForgeNet.Status == ForgeNetStatus.Live) PostState();
        }

        // ---- Controls (panel buttons and Albie's commands) ----------------------------------------------------

        public int FeatureCount { get { int n = 0; foreach (bool f in Features) if (f) n++; return n; } }
        public void SetPlaying(bool on, bool stopAtTarget = true)
        {
            Playing = on;
            if (on)
            {
                autoPause = stopAtTarget && TestAccuracy < AutoPauseAccuracy;
                stallStop = stopAtTarget;
                AutoPausedAt = StalledAt = -1; bestAccuracy = TestAccuracy; bestEpoch = Epoch;
            }
            Changed();
        }
        public void StepOnce() { Playing = false; TrainEpoch(); Evaluate(); RecordLoss(); Changed(); }
        public void ResetWeights() { seed++; Rebuild(); Mark("Weights reset to random", inCommand); }
        public void Regenerate()
        {
            var points = NNData.Generate(Dataset, PointCount, Noise, 7 + (int)Dataset);
            Train.Clear(); Test.Clear();
            for (int i = 0; i < points.Count; i++) (i % 2 == 0 ? Train : Test).Add(points[i]);
            Rebuild();
        }
        public void SetDataset(NNDataset d) { if (d == Dataset) return; Dataset = d; Regenerate(); }
        public void SetNoise(float noise) { Noise = Mathf.Clamp(noise, 0, 0.5f); Regenerate(); }
        public void SetActivation(NNActivation a) { Activation = a; Rebuild(); }
        public void SetLearningRate(float rate) { LearningRate = Mathf.Clamp(rate, 0.0001f, 3f); configPending = true; Changed(); }
        public void ShiftLearningRate(int step)
        {
            // The next rate up or down from the current one, also from one that isn't in the list (Albie's 0.005).
            float next = LearningRate;
            if (step > 0) { foreach (float r in Rates) if (r > LearningRate * 1.0001f) { next = r; break; } }
            else for (int i = Rates.Length - 1; i >= 0; i--) if (Rates[i] < LearningRate * 0.9999f) { next = Rates[i]; break; }
            SetLearningRate(next);
        }
        public void ToggleFeature(NNFeature f)
        {
            if (Features[(int)f] && FeatureCount == 1) return;  // keep at least one input
            Features[(int)f] = !Features[(int)f]; Rebuild();
        }
        public void SetHidden(IList<int> layers)
        {
            Hidden.Clear();
            for (int i = 0; i < layers.Count && i < MaxLayers; i++) Hidden.Add(Mathf.Clamp(layers[i], 1, MaxNeurons));
            Rebuild();
        }
        public void AddLayer() { if (Hidden.Count < MaxLayers) { Hidden.Add(2); Rebuild(); } }
        public void RemoveLayer() { if (Hidden.Count > 0) { Hidden.RemoveAt(Hidden.Count - 1); Rebuild(); } }
        public void ResizeLayer(int layer, int change)
        {
            int i = layer - 1; if (i < 0 || i >= Hidden.Count) return;
            int size = Mathf.Clamp(Hidden[i] + change, 1, MaxNeurons); if (size == Hidden[i]) return;
            Hidden[i] = size; Rebuild();
        }

        /// <summary>Switch a hidden neuron (layer 1.., index 1.. from the top) on or off. False when there is no such neuron.</summary>
        public bool SetNeuron(int layer, int index, bool enabled)
        {
            if (layer < 1 || layer > Hidden.Count || index < 1 || index > Hidden[layer - 1]) return false;
            float before = TestAccuracy;
            Net.disabled[layer][index - 1] = !enabled;
            Evaluate();
            Remember(enabled ? "neuron_on" : "neuron_off", NeuronId(layer, index), before);
            Changed();
            return true;
        }
        public void ToggleNeuron(int layer, int index) => SetNeuron(layer, index, Net.disabled[layer][index - 1]);
        public static string NeuronId(int layer, int index) => $"L{layer}N{index}";

        void Rebuild()
        {
            int inputs = FeatureCount;
            featureBuffer = new float[inputs];
            Net = new NNNetwork(inputs, Hidden.ToArray(), Activation, seed);
            Epoch = 0; LossHistory.Clear(); AutoPausedAt = StalledAt = -1;
            Evaluate();
            AddStoryPoint(); configPending = true;  // the new network starts from scratch: the curve drops here
            Rebuilt?.Invoke();
            Changed();
        }
        void Changed() { dirty = true; nextPost = 0; Stepped?.Invoke(); }

        // ---- Training --------------------------------------------------------------------------------------

        public float[] FeaturesOf(float x, float y)
        {
            int j = 0;
            for (int f = 0; f < Features.Length; f++) if (Features[f]) featureBuffer[j++] = NNData.Feature((NNFeature)f, x, y);
            return featureBuffer;
        }
        public float Predict(float x, float y) => Net.Forward(FeaturesOf(x, y));

        void TrainEpoch()
        {
            for (int i = 0; i < Train.Count; i++)
            {
                var p = Train[i];
                Net.Accumulate(FeaturesOf(p.x, p.y), p.label);
                if ((i + 1) % BatchSize == 0 || i == Train.Count - 1) Net.Apply(LearningRate);
            }
            Epoch++; TotalEpochs++;
        }

        void Evaluate()
        {
            Measure(Train, out float trainLoss, out float trainAcc);
            Measure(Test, out float testLoss, out float testAcc);
            TrainLoss = trainLoss; TrainAccuracy = trainAcc; TestLoss = testLoss; TestAccuracy = testAcc;
        }
        void RecordLoss()
        {
            if (LossHistory.Count == 256) LossHistory.RemoveAt(0);
            LossHistory.Add(TestLoss);
            if (TotalEpochs % storyStride == 0) AddStoryPoint();
        }
        void AddStoryPoint()
        {
            if (Story.Count == StoryMax)  // keep every other point and sample half as often from now on
            {
                for (int i = 0; i < StoryMax / 2; i++) Story[i] = Story[i * 2 + 1];
                Story.RemoveRange(StoryMax / 2, StoryMax / 2);
                storyStride *= 2;
            }
            Story.Add(new Vector3(TotalEpochs, TrainAccuracy, TestAccuracy));
        }

        void Mark(string text, bool albie)
        {
            if (Events.Count == MaxEvents) Events.RemoveAt(0);
            Events.Add(new StoryEvent { at = TotalEpochs, text = text, albie = albie });
        }

        // One event per change of setup, naming only what changed ("inputs x, y, x² · layers [8, 8]"); Albie's
        // commands flush right after they apply, the panel's buttons on the next frame.
        string[] Config() => new[]
        {
            Dataset == NNDataset.Xor ? "XOR" : Dataset.ToString().ToLowerInvariant(),
            "inputs " + FeatureList(), "layers [" + string.Join(", ", Hidden) + "]", Activation.ToString().ToLowerInvariant(),
            "learning rate " + LearningRate.ToString("0.####"), "noise " + Mathf.RoundToInt(Noise * 100) + "%",
        };
        public string FeatureList()
        {
            var names = new List<string>();
            for (int f = 0; f < Features.Length; f++) if (Features[f]) names.Add(NNData.Label((NNFeature)f));
            return string.Join(", ", names);
        }
        void FlushConfig(bool albie)
        {
            configPending = false;
            var now = Config();
            if (lastConfig == null) { lastConfig = now; return; }
            var changed = new List<string>();
            for (int i = 0; i < now.Length; i++) if (now[i] != lastConfig[i]) changed.Add(now[i]);
            lastConfig = now;
            if (changed.Count > 0) Mark(string.Join(" · ", changed), albie);
        }
        void Measure(List<NNPoint> points, out float loss, out float accuracy)
        {
            float sum = 0; int right = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i]; float o = Predict(p.x, p.y);
                sum += 0.5f * (o - p.label) * (o - p.label);
                if ((o >= 0 ? 1 : -1) == p.label) right++;
            }
            loss = points.Count == 0 ? 0 : sum / points.Count;
            accuracy = points.Count == 0 ? 0 : right / (float)points.Count;
        }
        float TestAccuracyNow() { Measure(Test, out _, out float acc); return acc; }
        /// <summary>Test accuracy if hidden neuron (layer 1.., neuron from 0) were switched the other way: how much it matters.</summary>
        public float AccuracyIfToggled(int layer, int neuron)
        {
            bool off = Net.disabled[layer][neuron];
            Net.disabled[layer][neuron] = !off;
            float acc = TestAccuracyNow();
            Net.disabled[layer][neuron] = off;
            return acc;
        }

        // ---- Albie: commands in, state out ----------------------------------------------------------------

        void OnCommand(JToken c)
        {
            EnsureReady();
            inCommand = true;
            try
            {
                string dataset = c.Value<string>("dataset");
                if (!string.IsNullOrEmpty(dataset) && Enum.TryParse(dataset, true, out NNDataset d) && d != Dataset)
                { float before = TestAccuracy; SetDataset(d); Remember("dataset", d.ToString().ToLowerInvariant(), before); }
                if (c["noise"] != null && c["noise"].Type != JTokenType.Null) SetNoise(c.Value<float>("noise"));
                string activation = c.Value<string>("activation");
                if (!string.IsNullOrEmpty(activation) && Enum.TryParse(activation, true, out NNActivation a)) SetActivation(a);
                if (c["learning_rate"] != null && c["learning_rate"].Type != JTokenType.Null) SetLearningRate(c.Value<float>("learning_rate"));
                if (c["features"] is JArray features && features.Count > 0)
                {
                    for (int f = 0; f < Features.Length; f++) Features[f] = false;
                    foreach (var id in features) for (int f = 0; f < Features.Length; f++) if (NNData.Id((NNFeature)f) == id.Value<string>()) Features[f] = true;
                    if (FeatureCount == 0) Features[(int)NNFeature.X] = Features[(int)NNFeature.Y] = true;
                    Rebuild();
                }
                if (c["hidden"] is JArray hidden) { var sizes = new List<int>(); foreach (var h in hidden) sizes.Add(h.Value<int>()); SetHidden(sizes); }
                string neuron = c.Value<string>("neuron");
                if (!string.IsNullOrEmpty(neuron) && c["enabled"] != null && TryParseNeuron(neuron, out int layer, out int index))
                    SetNeuron(layer, index, c.Value<bool>("enabled"));
                switch (c.Value<string>("action"))
                {
                    case "play": SetPlaying(true, c.Value<bool?>("auto_pause") ?? true); break;
                    case "pause": SetPlaying(false); break;
                    case "step": StepOnce(); break;
                    case "reset": ResetWeights(); break;
                    case "restore_all": RestoreAll(); break;
                }
                if (c.Value<bool?>("open") == true) OpenPanel();
                string view = c.Value<string>("view");  // Albie switching the ML desk: "lab" or "project"
                if (view == "lab") MLWorkspaceBridge.ShowLab(true);
                else if (view == "project") MLWorkspaceBridge.ShowLab(false);
            }
            catch (Exception e) { Debug.LogWarning("[NeuralLab] Bad ml.nn_command: " + e.Message); }
            inCommand = false;
            if (configPending) FlushConfig(true);
            appliedSeq = Math.Max(appliedSeq, c.Value<int?>("seq") ?? 0);
            PostState();
        }

        public void RestoreAll()
        {
            float before = TestAccuracy;
            for (int l = 1; l <= Hidden.Count; l++) Array.Clear(Net.disabled[l], 0, Net.disabled[l].Length);
            Evaluate(); Remember("restore_all", "", before); Changed();
        }

        static bool TryParseNeuron(string id, out int layer, out int index)
        {
            layer = index = 0;
            id = id.Trim().ToUpperInvariant();
            int n = id.IndexOf('N');
            return id.StartsWith("L") && n > 1 && int.TryParse(id.Substring(1, n - 1), out layer) && int.TryParse(id.Substring(n + 1), out index);
        }

        static void OpenPanel() => MLWorkspaceBridge.ShowLab(true);

        void Remember(string action, string target, float before)
        {
            switch (action)
            {
                case "auto_pause": Mark($"Auto-paused at {TestAccuracy:0%}", false); break;
                case "stalled": Mark($"Stuck at {TestAccuracy:0%}", false); break;
                case "neuron_off": Mark($"{target} off: {before:0%} → {TestAccuracy:0%}", inCommand); break;
                case "neuron_on": Mark($"{target} back on: {before:0%} → {TestAccuracy:0%}", inCommand); break;
                case "restore_all": Mark($"All neurons on: {before:0%} → {TestAccuracy:0%}", inCommand); break;
            }
            history.Add(new JObject { ["action"] = action, ["target"] = target, ["epoch"] = Epoch,
                ["test_accuracy_before"] = Round(before), ["test_accuracy_after"] = Round(TestAccuracy) });
            if (history.Count > 12) history.RemoveAt(0);
        }

        static float Round(float v) => Mathf.Round(v * 1000f) / 1000f;

        /// <summary>Everything Albie needs to talk about the network (POST /ml/nn/state).</summary>
        public JObject State()
        {
            EnsureReady();
            var features = new JArray(); for (int f = 0; f < Features.Length; f++) if (Features[f]) features.Add(NNData.Id((NNFeature)f));
            var neurons = new JArray();
            for (int l = 1; l <= Hidden.Count; l++)
                for (int i = 1; i <= Hidden[l - 1]; i++)
                {
                    bool off = Net.disabled[l][i - 1];
                    float flipped = AccuracyIfToggled(l, i - 1);
                    neurons.Add(new JObject { ["id"] = NeuronId(l, i), ["layer"] = l, ["index"] = i, ["enabled"] = !off,
                        ["test_accuracy_if_toggled"] = Round(flipped), ["detects"] = DetectsAsIfOn(l, i - 1),
                        ["outgoing_weight"] = Round(OutgoingWeight(l, i - 1)) });
                }
            Evaluate();
            var panel = FindAnyObjectByType<NeuralLab>();
            var workspace = FindAnyObjectByType<Workspace>();
            bool mlOpen = workspace != null && workspace.ActiveMode == "ml";
            return new JObject
            {
                ["applied_seq"] = appliedSeq, ["playing"] = Playing, ["epoch"] = Epoch,
                ["auto_paused_at_epoch"] = AutoPausedAt >= 0 ? AutoPausedAt : (JToken)null, ["auto_pause_accuracy"] = AutoPauseAccuracy,
                ["stalled_at_epoch"] = StalledAt >= 0 ? StalledAt : (JToken)null, ["stop_count"] = StopCount,
                ["dataset"] = Dataset.ToString().ToLowerInvariant(), ["noise"] = Round(Noise),
                ["activation"] = Activation.ToString().ToLowerInvariant(), ["learning_rate"] = LearningRate,
                ["features"] = features, ["hidden"] = new JArray(Hidden.ToArray()),
                ["train_loss"] = Round(TrainLoss), ["test_loss"] = Round(TestLoss),
                ["train_accuracy"] = Round(TrainAccuracy), ["test_accuracy"] = Round(TestAccuracy),
                ["neurons"] = neurons, ["history"] = new JArray(history.ToArray()),
                ["panel_open"] = panel != null && panel.isActiveAndEnabled,
                ["view"] = !mlOpen ? "closed" : MLWorkspaceBridge.LabOpen ? "lab" : "project",
            };
        }

        void PostState()
        {
            if (ForgeNet.Status != ForgeNetStatus.Live) return;  // stays dirty: OnNetStatus sends it once the hub is back
            dirty = false; nextPost = Time.unscaledTime + 1f;
            if (Playing && Epoch == lastPostedEpoch) return;
            lastPostedEpoch = Epoch;
            ForgeNet.Post("/ml/nn/state", State(), null, _ => { });
        }

        float OutgoingWeight(int layer, int neuron)
        {
            float sum = 0; var next = Net.weights[layer + 1];
            for (int j = 0; j < next.Length; j++) sum += Mathf.Abs(next[j][neuron]);
            return sum;
        }

        // A switched-off neuron outputs 0; what it detected is what it would output if it were on (the "why").
        public string DetectsAsIfOn(int layer, int neuron)
        {
            bool off = Net.disabled[layer][neuron];
            Net.disabled[layer][neuron] = false;
            string what = Detects(layer, neuron);
            Net.disabled[layer][neuron] = off;
            return off ? what + ", now switched off" : what;
        }

        // Which simple pattern the neuron's output follows over the test points: what Albie says it "looks at".
        string Detects(int layer, int neuron)
        {
            string[] names = { "left vs right (x)", "up vs down (y)", "distance from the center", "diagonal quadrants (x·y)" };
            float best = 0; int pick = -1;
            for (int s = 0; s < names.Length; s++)
            {
                float c = Mathf.Abs(Correlation(layer, neuron, s));
                if (c > best) { best = c; pick = s; }
            }
            return pick < 0 || best < 0.4f ? "a mix of patterns" : $"{names[pick]} (correlation {best:0.00})";
        }
        float Correlation(int layer, int neuron, int signal)
        {
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; int n = Test.Count;
            for (int i = 0; i < n; i++)
            {
                var p = Test[i];
                Predict(p.x, p.y);
                double a = Net.NeuronOutput(layer, neuron);
                double b = signal == 0 ? p.x : signal == 1 ? p.y : signal == 2 ? p.x * p.x + p.y * p.y : p.x * p.y;
                sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
            }
            double cov = sab / n - sa / n * sb / n, va = saa / n - sa / n * sa / n, vb = sbb / n - sb / n * sb / n;
            return va <= 1e-9 || vb <= 1e-9 ? 0 : (float)(cov / Math.Sqrt(va * vb));
        }
    }
}
