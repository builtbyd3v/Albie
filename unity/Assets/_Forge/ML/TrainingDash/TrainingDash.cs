using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class TrainingDash : MonoBehaviour
    {
        public MLSession session;
        [Tooltip("Epochs for the Train button (POST /ml/train).")] public int trainEpochs = 10;
        TMP_Text status, warning, lossLabel, accuracyLabel, runMetrics;
        Image progress;
        MLCurve loss, accuracy;
        RectTransform summary;
        readonly List<float[]> classHistory = new List<float[]>();
        readonly List<MLProbeSample> probes = new List<MLProbeSample>();
        bool showLiveGraphs = true;
        bool wasDone;
        int firstHistoryEpoch = 1;
        public int AccuracyEpochCount => classHistory.Count;
        public int ProbeCount => probes.Count;
        void Start()
        {
            session = MLSession.Resolve(session); var root = GetComponent<ForgePanel>().Content;
            status = MLUI.Text(root, "", 8, 0, 750, 25);
            MLUI.Fill(root, 8, 29, 740, 6, new Color(.2f,.25f,.3f)); progress = MLUI.Fill(root, 8, 29, 0, 6, Color.cyan);
            lossLabel = MLUI.Text(root, "LOSS", 8, 42, 360, 24, 13); lossLabel.textWrappingMode = TextWrappingModes.NoWrap;
            accuracyLabel = MLUI.Text(root, "ACCURACY", 380, 42, 368, 24, 13); accuracyLabel.textWrappingMode = TextWrappingModes.NoWrap;
            loss = Curve("Loss curves", root, 8, false); accuracy = Curve("Accuracy curves", root, 380, true);
            // A run that logs neither loss nor accuracy (a backtest, a regression with only r2) shows its own numbers here.
            runMetrics = MLUI.Text(root, "", 8, 70, 740, 95, 15);
            warning = MLUI.Text(root, "", 8, 175, 530, 28, 14); warning.color = new Color(1,.6f,.25f);
            MLUI.Button(root, "Train", 550, 172, 90, 28, () => session.Train(trainEpochs));
            MLUI.Button(root, "Replay", 650, 172, 90, 28, session.Restart);
            summary = MLUI.Rect("Evaluation", root, 8, 210, 740, 300);
            session.Changed += Refresh;
            session.Reloaded += OnSessionReloaded;
            session.Metrics += OnMetrics;
            session.ClassAccuracyUpdated += OnClassAccuracy;
            session.ProbeUpdated += OnProbe;
            if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) BuildMockProbes();
            Refresh();
        }
        MLCurve Curve(string name, Transform root, float x, bool acc)
        {
            // The CanvasRenderer first: added at runtime, the Graphic doesn't always bring one and then draws nothing.
            var go = MLUI.Rect(name, root, x, 70, 330, 95).gameObject; go.AddComponent<CanvasRenderer>();
            var curve = go.AddComponent<MLCurve>(); curve.session = session; curve.accuracy = acc; curve.raycastTarget = false; return curve;
        }
        void Refresh()
        {
            int e = session.EpochIndex;
            var tick = e >= 0 ? session.Data.metrics[e] : null;
            status.text = tick == null ? (session.Done ? $"DONE   run {session.RunId}" : session.HubData ? "Waiting for training..." : session.NoImageData ? "Ready: press Train" : "Waiting for mock training...")
                : $"{(session.Done ? "DONE" : "TRAINING")}   Epoch {e+1}/{session.TotalEpochs}" +
                  (float.IsNaN(tick.lr) ? "" : $"   LR {tick.lr:G3}") + (float.IsNaN(tick.val_acc) ? "" : $"   val acc {tick.val_acc:P1}");
            ShowRunMetrics(tick);
            progress.rectTransform.sizeDelta = new Vector2(740f * (e+1) / Mathf.Max(1,session.TotalEpochs), 6);
            warning.text = session.Overfit ? "OVERFIT: " + (string.IsNullOrEmpty(session.OverfitReason) ? "validation loss rose twice while training loss fell" : session.OverfitReason) : session.Notice ?? "";
            loss.SetVerticesDirty(); accuracy.SetVerticesDirty(); MLUI.Clear(summary);
            if (session.NoImageData) return;  // per-class heatmap, probe strip and confusion grid are image-classifier views
            // Choose the default view only on a run-state transition. A refresh must preserve the user's choice.
            if (session.Done != wasDone) showLiveGraphs = !session.Done;
            wasDone = session.Done;
            if (showLiveGraphs) { DrawLiveGraphs(); return; }
            MLUI.Button(summary, "Live graphs", 636, 0, 100, 22, () => { showLiveGraphs = true; Refresh(); });
            DrawEvaluation();
        }
        void ShowRunMetrics(MLMetrics tick)
        {
            bool curves = tick == null || tick.HasLoss || tick.HasAccuracy || tick.extra == null || tick.extra.Count == 0;
            lossLabel.text = curves ? "LOSS" + Pair(tick == null ? float.NaN : tick.train_loss, tick == null ? float.NaN : tick.val_loss, false) : "RUN METRICS";
            accuracyLabel.text = curves ? "ACCURACY" + Pair(tick == null ? float.NaN : tick.train_acc, tick == null ? float.NaN : tick.val_acc, true) + BestVal() : "";
            if (curves) { runMetrics.text = ""; return; }
            var lines = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var pair in tick.extra)
            {
                if (pair.Value == null || shown >= 8) continue;
                bool left = shown++ % 2 == 0;  // two columns: name, then value, via TMP <pos> tabs
                lines.Append(left ? "" : "<pos=50%>").Append(Pretty(pair.Key))
                     .Append(left ? "<pos=30%>" : "<pos=80%>").Append(pair.Value.Value.ToString("0.###"));
                if (!left) lines.Append('\n');
            }
            runMetrics.text = lines.ToString();
        }
        // The latest epoch's numbers in the curves' colours (blue train, orange val); the legend until the first tick.
        static string Pair(float train, float val, bool percent)
        {
            if (float.IsNaN(train) && float.IsNaN(val)) return "   <color=#4DCCFF>train</color> / <color=#FFA640>val</color>";
            string F(float v) => float.IsNaN(v) ? "–" : percent ? (v * 100f).ToString("0.0") + "%" : v.ToString("0.000");
            return $"  <color=#4DCCFF>train {F(train)}</color>  <color=#FFA640>val {F(val)}</color>";
        }
        string BestVal()
        {
            float best = float.NaN;
            for (int i = 0; i <= session.EpochIndex; i++)
            {
                var m = session.Data.metrics[i];
                if (m != null && !float.IsNaN(m.val_acc) && !(m.val_acc <= best)) best = m.val_acc;
            }
            return float.IsNaN(best) ? "" : $"  best {best * 100f:0.0}%";
        }
        // val_max_drawdown -> "val max drawdown"
        static string Pretty(string name) => name.Replace('_', ' ');
        void DrawEvaluation()
        {
            int n = session.Data.classes.Length; var matrix = new int[n,n];
            foreach (var s in session.Data.samples) if (!s.removed && s.pred_idx >= 0) matrix[s.true_idx,s.pred_idx]++;
            int max = 1; foreach (int v in matrix) max = Mathf.Max(max,v);
            MLUI.Text(summary, "CONFUSION  rows: true / columns: predicted", 0, 0, 470, 25, 13);
            for (int r = 0; r < n; r++)
            {
                MLUI.Text(summary, session.Data.classes[r], 0, 30+r*23, 88, 23, 11);
                MLUI.Text(summary, r.ToString(), 90+r*25, 12, 24, 18, 10);
                int count = 0;
                for (int c = 0; c < n; c++)
                {
                    int rr = r, cc = c; count += matrix[r,c];
                    var b = MLUI.Button(summary, matrix[r,c].ToString(), 90+c*25, 30+r*23, 23, 21, () => session.SelectPair(rr,cc));
                    b.name = $"Confusion {r},{c}";
                    b.image.color = Color.Lerp(new Color(.12f,.17f,.24f), r == c ? new Color(.1f,.65f,.4f) : new Color(.85f,.25f,.2f), matrix[r,c]/(float)max);
                }
                MLUI.Text(summary, $"{r}: {session.Data.classes[r]}  {(count == 0 ? "—" : (matrix[r,r]/(float)count).ToString("P0"))}", 370, 30+r*23, 330, 23, 13);
            }
        }
        void DrawLiveGraphs()
        {
            MLUI.Text(summary, session.HubData ? "PER-CLASS ACCURACY · LIVE" : "PER-CLASS ACCURACY · DEMO REPLAY", 0, 0, 520, 20, 12);
            MLUI.Button(summary, "Results", 636, 0, 100, 22, () => { showLiveGraphs = false; Refresh(); });
            int classes = session.Data.classes.Length;
            int columns = classHistory.Count;
            const float labelWidth = 82, top = 24, rowHeight = 12;
            float cellWidth = (summary.rect.width - labelWidth - 4) / Mathf.Max(1, columns);
            for (int c = 0; c < classes; c++)
            {
                MLUI.Text(summary, ShortName(session.Data.classes[c]), 0, top + c * rowHeight, labelWidth - 4, rowHeight, 9);
                for (int e = 0; e < columns; e++)
                {
                    float value = classHistory[e].Length > c ? classHistory[e][c] : -1;
                    var fill = MLUI.Fill(summary, labelWidth + e * cellWidth, top + c * rowHeight,
                        Mathf.Max(2, cellWidth - 2), rowHeight - 1, HeatColor(value));
                    fill.gameObject.name = $"Accuracy epoch {firstHistoryEpoch + e} class {c}";
                }
            }
            for (int e = 0; e < columns; e++)
            {
                int epoch = firstHistoryEpoch + e;
                if (e == columns - 1 || epoch % 5 == 0)
                    MLUI.Text(summary, epoch.ToString(), labelWidth + e * cellWidth, top + classes * rowHeight,
                        Mathf.Max(16, cellWidth), 11, 8);
            }

            const float probeTitleY = 160, imageY = 184, captionY = 224, barY = 243;
            MLUI.Text(summary, "PROBE STRIP · 16 FIXED VALIDATION SAMPLES", 0, probeTitleY, 530, 20, 12);
            if (probes.Count == 0) MLUI.Text(summary, "Waiting for ml.probe", 0, imageY, 220, 40, 11);
            float slotWidth = summary.rect.width / 16f;
            for (int i = 0; i < Mathf.Min(16, probes.Count); i++)
            {
                var probe = probes[i];
                int sampleIndex = FindSampleIndex(probe.id);
                if (sampleIndex < 0) continue;
                var sample = session.Data.samples[sampleIndex];
                var image = MLUI.Rect("Probe " + probe.id, summary, i * slotWidth + 3, imageY, 34, 34)
                    .gameObject.AddComponent<RawImage>();
                image.texture = session.Atlas; image.uvRect = session.UV(sampleIndex); image.raycastTarget = false;
                int predicted = probe.pred_idx;
                string caption = predicted >= 0 && predicted < session.Data.classes.Length
                    ? $"{ShortName(session.Data.classes[sample.true_idx])}->{ShortName(session.Data.classes[predicted])}" : ShortName(session.Data.classes[sample.true_idx]);
                var text = MLUI.Text(summary, caption, i * slotWidth, captionY, slotWidth - 2, 16, 8);
                text.color = predicted == sample.true_idx ? new Color(.45f, 1f, .68f) : new Color(1f, .55f, .36f);
                MLUI.Fill(summary, i * slotWidth + 3, barY, slotWidth - 7, 5, new Color(.14f, .2f, .28f));
                float confidence = Confidence(probe);
                MLUI.Fill(summary, i * slotWidth + 3, barY, (slotWidth - 7) * confidence, 5,
                    predicted == sample.true_idx ? new Color(.25f, .85f, .52f) : new Color(1f, .43f, .25f));
                MLUI.Text(summary, confidence.ToString("P0"), i * slotWidth, barY + 7, slotWidth - 2, 12, 8);
            }
        }
        void OnMetrics(MLMetrics tick)
        {
            if (tick == null) return;
            if (tick.epoch <= 1)
            {
                classHistory.Clear(); firstHistoryEpoch = 1;
                if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) BuildMockProbes();
            }
            if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) AdvanceMockGraphs(tick.epoch);
        }
        void OnClassAccuracy(int epoch, float[] values)
        {
            if (epoch <= 1) { classHistory.Clear(); firstHistoryEpoch = 1; }
            StoreClassAccuracy(epoch, values);
        }
        void OnProbe(MLProbePayload update)
        {
            if (update?.samples == null) return;
            probes.Clear();
            foreach (var sample in update.samples) if (sample != null && FindSampleIndex(sample.id) >= 0) probes.Add(sample);
            if (probes.Count > 16) probes.RemoveRange(16, probes.Count - 16);
        }
        void OnSessionReloaded()
        {
            classHistory.Clear(); probes.Clear(); firstHistoryEpoch = 1;
        }
        void StoreClassAccuracy(int epoch, float[] values)
        {
            if (values == null || values.Length == 0) return;
            int index = Mathf.Max(0, epoch - firstHistoryEpoch);
            while (classHistory.Count <= index)
            {
                var emptyEpoch = new float[session.Data.classes.Length];
                for (int i = 0; i < emptyEpoch.Length; i++) emptyEpoch[i] = -1f;
                classHistory.Add(emptyEpoch);
            }
            classHistory[index] = values;
            while (classHistory.Count > 24) { classHistory.RemoveAt(0); firstHistoryEpoch++; }
        }
        void BuildMockProbes()
        {
            probes.Clear();
            var samples = session.Data.samples;
            var selected = new List<MLSample>(16);
            foreach (var sample in samples) if (!sample.correct && selected.Count < 3) selected.Add(sample);
            foreach (var sample in samples) if (sample.correct && selected.Count < 16) selected.Add(sample);
            foreach (var sample in samples) if (selected.Count < 16 && !selected.Contains(sample)) selected.Add(sample);
            foreach (var sample in selected) probes.Add(new MLProbeSample { id = sample.id, pred_idx = -1 });
        }
        void AdvanceMockGraphs(int epoch)
        {
            int classCount = session.Data.classes.Length;
            var seen = new int[classCount]; var finalCorrect = new int[classCount];
            foreach (var sample in session.Data.samples)
                if (!sample.removed && sample.true_idx >= 0 && sample.true_idx < classCount)
                {
                    seen[sample.true_idx]++;
                    if (sample.correct) finalCorrect[sample.true_idx]++;
                }
            float progress = Mathf.Clamp01(epoch / (float)Mathf.Max(1, session.TotalEpochs));
            var values = new float[classCount];
            for (int c = 0; c < classCount; c++)
            {
                float final = seen[c] == 0 ? 0 : finalCorrect[c] / (float)seen[c];
                float curve = Mathf.Pow(progress, .7f + (c % 4) * .12f);
                values[c] = Mathf.Lerp(.08f, final, curve);
            }
            StoreClassAccuracy(epoch, values);

            for (int i = 0; i < probes.Count; i++)
            {
                var probe = probes[i]; int index = FindSampleIndex(probe.id); if (index < 0) continue;
                var sample = session.Data.samples[index]; bool finalIsCorrect = sample.pred_idx == sample.true_idx;
                int initialWrong = (sample.true_idx + 1 + i % Mathf.Max(1, classCount - 1)) % classCount;
                float threshold = .18f + ((i * 37) % 67) / 100f;
                int predicted = finalIsCorrect && progress >= threshold ? sample.true_idx :
                    (finalIsCorrect ? initialWrong : sample.pred_idx);
                float confidence = finalIsCorrect ? Mathf.Lerp(.42f, .96f, progress) : Mathf.Lerp(.7f, .96f, progress);
                var probs = new float[classCount];
                for (int c = 0; c < classCount; c++) probs[c] = c == predicted ? confidence : (1 - confidence) / Mathf.Max(1, classCount - 1);
                probe.pred_idx = predicted; probe.probs = probs;
            }
        }
        int FindSampleIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < session.Data.samples.Length; i++) if (session.Data.samples[i].id == id) return i;
            return -1;
        }
        static string ShortName(string value) => string.IsNullOrEmpty(value) ? "?" : value.Length <= 9 ? value : value.Substring(0, 6) + "...";
        static float Confidence(MLProbeSample probe)
        {
            if (probe.probs == null || probe.probs.Length == 0) return .5f;
            float max = 0; foreach (float value in probe.probs) max = Mathf.Max(max, value);
            return Mathf.Clamp01(max);
        }
        static Color HeatColor(float value)
        {
            if (value < 0) return new Color(.12f, .16f, .22f);
            return Color.Lerp(new Color(.84f, .24f, .2f), new Color(.15f, .78f, .42f), Mathf.Clamp01(value));
        }
        void OnDestroy()
        {
            if (session == null) return;
            session.Changed -= Refresh; session.Reloaded -= OnSessionReloaded; session.Metrics -= OnMetrics;
            session.ClassAccuracyUpdated -= OnClassAccuracy; session.ProbeUpdated -= OnProbe;
        }
    }
}
