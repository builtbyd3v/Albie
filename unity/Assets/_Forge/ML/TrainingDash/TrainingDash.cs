using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// Training as it happens: epoch, losses, accuracy and learning rate as big numbers, loss and accuracy curves (the
    /// best epoch marked once known), a banner for overfitting, the hub or the finished run, then per-class accuracy
    /// over the epochs and the probe strip. The evaluation (confusion, per-class scores) lives on the Results panel.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public class TrainingDash : MonoBehaviour
    {
        public MLSession session;
        [Tooltip("Epochs for the Train button (POST /ml/train).")] public int trainEpochs = 10;
        const float Kpi = 0, ProgressY = 52, ChartY = 64, ChartHeight = 132, BannerY = 204, SectionY = 242, SummaryY = 260;
        TMP_Text epochValue, trainLossValue, valLossValue, valAccValue, lrValue, statusText, bannerLabel, bannerText, sectionTitle, lossMax, runMetrics;
        ForgeSurface progress, statusChip, banner, bannerDot;
        MLCurve loss, accuracy;
        RectTransform root, summary;
        float width;
        readonly List<float[]> classHistory = new List<float[]>();
        readonly List<MLProbeSample> probes = new List<MLProbeSample>();
        int firstHistoryEpoch = 1;
        public int AccuracyEpochCount => classHistory.Count;
        public int ProbeCount => probes.Count;
        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content; width = root.rect.width;
            epochValue = MLUI.Stat(root, "Epoch", "—", 2, Kpi, 86, MLUI.TextColor);
            trainLossValue = MLUI.Stat(root, "Train loss", "—", 92, Kpi, 86, MLUI.Train);
            valLossValue = MLUI.Stat(root, "Val loss", "—", 182, Kpi, 86, MLUI.Val);
            valAccValue = MLUI.Stat(root, "Val accuracy", "—", 272, Kpi, 100, MLUI.Good);
            lrValue = MLUI.Stat(root, "LR", "—", 376, Kpi, 70, MLUI.TextColor);
            foreach (var stat in new[] { epochValue, trainLossValue, valLossValue, valAccValue, lrValue }) stat.fontSize = 21;
            statusChip = MLUI.Surface(root, width - 292, 4, 88, 22, new Color(1, 1, 1, 0.08f), 11);
            statusText = MLUI.Caps(root, "", width - 292, 8, 88, 14, 9); statusText.alignment = TextAlignmentOptions.Top;
            MLUI.Pill(root, "Train", width - 196, 1, 92, 28, () => session.Train(trainEpochs)).On = true;
            MLUI.Button(root, "Replay", width - 98, 1, 98, 28, session.Restart);
            MLUI.Surface(root, 0, ProgressY, width, 4, new Color(1, 1, 1, 0.07f), 2);
            progress = MLUI.Surface(root, 0, ProgressY, 0, 4, MLUI.Accent, 2); progress.Glow = 0.5f;

            float chart = (width - 12) * 0.5f;
            loss = Chart("Loss", 0, chart, false);
            accuracy = Chart("Accuracy", chart + 12, chart, true);
            lossMax = MLUI.Text(root, "", 12, ChartY + 24, 40, 12, 8.5f); lossMax.color = MLUI.Faint;
            var top = MLUI.Text(root, "100%", chart + 24, ChartY + 24, 40, 12, 8.5f); top.color = MLUI.Faint;
            // A run that logs neither loss nor accuracy (a backtest, a regression with only r2) shows its own numbers here.
            runMetrics = MLUI.Text(root, "", 12, ChartY + 34, width - 24, ChartHeight - 40, 13);

            // One banner line: overfitting, a hub notice, or the finished run.
            banner = MLUI.Surface(root, 0, BannerY, width, 28, Color.clear, 10, 0.5f);
            bannerDot = MLUI.Dot(banner.transform, 12, 10, 8, MLUI.Warn);
            bannerLabel = MLUI.Caps(banner.transform, "", 28, 8, 110, 14, 9.5f);
            bannerText = MLUI.Text(banner.transform, "", 128, 6, width - 140, 16, 11); bannerText.textWrappingMode = TextWrappingModes.NoWrap;
            bannerText.overflowMode = TextOverflowModes.Truncate;

            sectionTitle = MLUI.Caps(root, "", 2, SectionY, 560, 14, 9.5f);
            summary = MLUI.Rect("Live graphs", root, 0, SummaryY, width, root.rect.height - SummaryY);
            session.Changed += Refresh;
            session.Reloaded += OnSessionReloaded;
            session.Metrics += OnMetrics;
            session.ClassAccuracyUpdated += OnClassAccuracy;
            session.ProbeUpdated += OnProbe;
            if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) BuildMockProbes();
            Refresh();
        }
        MLCurve Chart(string title, float x, float w, bool acc)
        {
            MLUI.Well(root, x, ChartY, w, ChartHeight);
            MLUI.Caps(root, title, x + 12, ChartY + 9, 100);
            float key = x + w - 112;
            MLUI.Dot(root, key, ChartY + 12, 7, MLUI.Train); MLUI.Text(root, "Train", key + 10, ChartY + 8, 40, 14, 9.5f).color = MLUI.Sub;
            MLUI.Dot(root, key + 54, ChartY + 12, 7, MLUI.Val); MLUI.Text(root, "Val", key + 64, ChartY + 8, 40, 14, 9.5f).color = MLUI.Sub;
            // The CanvasRenderer first: added at runtime, the Graphic doesn't always bring one and then draws nothing.
            var go = MLUI.Rect(title + " curves", root, x + 12, ChartY + 36, w - 24, ChartHeight - 46).gameObject; go.AddComponent<CanvasRenderer>();
            var curve = go.AddComponent<MLCurve>(); curve.session = session; curve.accuracy = acc; curve.raycastTarget = false; return curve;
        }
        void Refresh()
        {
            int e = session.EpochIndex;
            var m = e >= 0 && e < session.Data.metrics.Length ? session.Data.metrics[e] : null;
            epochValue.text = m == null ? "—" : $"{e + 1}<size=60%><color=#FFFFFF80> / {session.TotalEpochs}</color></size>";
            // A metric the run didn't log is NaN (MLNullAsNaN): "—", like an epoch that hasn't happened yet.
            trainLossValue.text = Num(m?.train_loss, "0.000");
            valLossValue.text = Num(m?.val_loss, "0.000");
            valAccValue.text = Num(m?.val_acc, "0.0%");
            lrValue.text = Num(m?.lr, "G3");
            ShowRunMetrics(m);
            string state = session.Done ? "Done" : e >= 0 ? "Training" : session.NoImageData ? "Ready" : "Waiting";
            Color stateColor = session.Done ? MLUI.Good : e >= 0 ? MLUI.Accent : MLUI.Faint;
            statusText.text = state.ToUpperInvariant(); statusText.color = Color.Lerp(stateColor, Color.white, 0.2f);
            statusChip.color = new Color(stateColor.r, stateColor.g, stateColor.b, 0.16f);
            progress.rectTransform.sizeDelta = new Vector2(width * (e + 1) / Mathf.Max(1, session.TotalEpochs), 4);
            progress.color = session.Done ? MLUI.Good : MLUI.Accent;

            int best = MLReport.BestEpoch(session.Data.metrics, e);
            loss.marker = accuracy.marker = session.Done || session.Overfit ? best : -1;
            if (session.Overfit)
                Banner(MLUI.Warn, "Overfitting", (string.IsNullOrEmpty(session.OverfitReason) ? "validation loss rose twice while training loss fell" : session.OverfitReason)
                    + (best >= 0 ? $"  ·  best epoch {best + 1}" : ""));
            else if (!string.IsNullOrEmpty(session.Notice)) Banner(MLUI.Accent, "Hub", session.Notice);
            else if (session.Done && best >= 0 && !float.IsNaN(session.Data.metrics[best].val_acc))
                Banner(MLUI.Good, "Finished", $"best val accuracy {session.Data.metrics[best].val_acc:0.0%} at epoch {best + 1}  ·  full evaluation on the Results panel");
            else banner.gameObject.SetActive(false);

            float max = 1;
            for (int i = 0; i <= e && i < session.Data.metrics.Length; i++)
            {
                var t = session.Data.metrics[i];
                if (t == null) continue;
                if (!float.IsNaN(t.train_loss)) max = Mathf.Max(max, t.train_loss);
                if (!float.IsNaN(t.val_loss)) max = Mathf.Max(max, t.val_loss);
            }
            lossMax.text = max.ToString("0.0");
            loss.SetVerticesDirty(); accuracy.SetVerticesDirty(); MLUI.Clear(summary);
            if (session.NoImageData) { sectionTitle.text = ""; return; }  // per-class accuracy and probes are image-classifier views
            DrawLiveGraphs();
        }
        static string Num(float? v, string format) => v == null || float.IsNaN(v.Value) ? "—" : v.Value.ToString(format);
        void ShowRunMetrics(MLMetrics tick)
        {
            bool curves = tick == null || tick.HasLoss || tick.HasAccuracy || tick.extra == null || tick.extra.Count == 0;
            if (curves) { runMetrics.text = ""; return; }
            var lines = new System.Text.StringBuilder("<color=#FFFFFF99>RUN METRICS</color>\n");
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
        // val_max_drawdown -> "val max drawdown"
        static string Pretty(string name) => name.Replace('_', ' ');
        void Banner(Color tone, string label, string text)
        {
            banner.gameObject.SetActive(true);
            banner.color = new Color(tone.r, tone.g, tone.b, 0.12f);
            bannerDot.color = tone; bannerLabel.text = label.ToUpperInvariant(); bannerLabel.color = tone; bannerText.text = text;
        }
        void DrawLiveGraphs()
        {
            sectionTitle.text = session.HubData ? "PER-CLASS ACCURACY  ·  LIVE" : "PER-CLASS ACCURACY  ·  DEMO REPLAY";
            int classes = session.Data.classes.Length;
            int columns = classHistory.Count;
            float heatHeight = Mathf.Min(150f, summary.rect.height - 100f);
            const float labelWidth = 82;
            float top = 10, rowHeight = (heatHeight - top - 18) / Mathf.Max(1, classes);
            MLUI.Well(summary, 0, 0, width, heatHeight);
            float cellWidth = Mathf.Min(40f, (width - labelWidth - 14) / Mathf.Max(1, columns));
            for (int c = 0; c < classes; c++)
            {
                var name = MLUI.Text(summary, ShortName(session.Data.classes[c]), 10, top + c * rowHeight + rowHeight * 0.5f - 6, labelWidth - 16, 12, 8.5f);
                name.color = MLUI.Sub; name.alignment = TextAlignmentOptions.TopRight;
                for (int e = 0; e < columns; e++)
                {
                    float value = classHistory[e].Length > c ? classHistory[e][c] : -1;
                    var fill = MLUI.Surface(summary, labelWidth + e * cellWidth, top + c * rowHeight,
                        Mathf.Max(2, cellWidth - 2), rowHeight - 2, HeatColor(value), 2);
                    fill.gameObject.name = $"Accuracy epoch {firstHistoryEpoch + e} class {c}";
                }
            }
            for (int e = 0; e < columns; e++)
            {
                int epoch = firstHistoryEpoch + e;
                if (e == columns - 1 || epoch % 5 == 0)
                {
                    var label = MLUI.Text(summary, epoch.ToString(), labelWidth + e * cellWidth, top + classes * rowHeight + 2, Mathf.Max(16, cellWidth), 11, 8);
                    label.color = MLUI.Faint;
                }
            }
            if (columns == 0) { var wait = MLUI.Text(summary, "Waiting for the first epoch", labelWidth, heatHeight * 0.5f - 8, 300, 16, 11); wait.color = MLUI.Faint; }

            float probeY = heatHeight + 8, probeHeight = summary.rect.height - probeY;
            MLUI.Well(summary, 0, probeY, width, probeHeight);
            MLUI.Caps(summary, "Probe strip  ·  16 fixed validation images", 12, probeY + 8, 400);
            if (probes.Count == 0) { var wait = MLUI.Text(summary, "Waiting for ml.probe", 12, probeY + 30, 220, 16, 11); wait.color = MLUI.Faint; }
            float slotWidth = (width - 16) / 16f, imageY = probeY + 26, photo = Mathf.Min(34f, slotWidth - 8);
            for (int i = 0; i < Mathf.Min(16, probes.Count); i++)
            {
                var probe = probes[i];
                int sampleIndex = FindSampleIndex(probe.id);
                if (sampleIndex < 0) continue;
                var sample = session.Data.samples[sampleIndex];
                float x = 8 + i * slotWidth;
                int predicted = probe.pred_idx;
                bool right = predicted == sample.true_idx;
                Color tone = predicted < 0 ? MLUI.Faint : right ? MLUI.Good : MLUI.Bad;
                MLUI.Surface(summary, x + (slotWidth - photo) * 0.5f - 2, imageY - 2, photo + 4, photo + 4, new Color(tone.r, tone.g, tone.b, 0.35f), 5);
                var image = MLUI.Rect("Probe " + probe.id, summary, x + (slotWidth - photo) * 0.5f, imageY, photo, photo)
                    .gameObject.AddComponent<RawImage>();
                image.texture = session.Atlas; image.uvRect = session.UV(sampleIndex); image.raycastTarget = false;
                string caption = predicted >= 0 && predicted < session.Data.classes.Length
                    ? (right ? ShortName(session.Data.classes[predicted]) : "→ " + ShortName(session.Data.classes[predicted])) : ShortName(session.Data.classes[sample.true_idx]);
                var text = MLUI.Text(summary, caption, x, imageY + photo + 4, slotWidth, 12, 8);
                text.color = tone; text.alignment = TextAlignmentOptions.Top; text.textWrappingMode = TextWrappingModes.NoWrap;
                float confidence = Confidence(probe), barWidth = slotWidth - 12;
                MLUI.Surface(summary, x + 6, imageY + photo + 18, barWidth, 3, new Color(1, 1, 1, 0.08f), 1.5f);
                MLUI.Surface(summary, x + 6, imageY + photo + 18, Mathf.Max(3, barWidth * confidence), 3, tone, 1.5f);
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
        static string ShortName(string value) => string.IsNullOrEmpty(value) ? "?" : value.Length <= 9 ? value : value.Substring(0, 8) + "…";
        static float Confidence(MLProbeSample probe)
        {
            if (probe.probs == null || probe.probs.Length == 0) return .5f;
            float max = 0; foreach (float value in probe.probs) max = Mathf.Max(max, value);
            return Mathf.Clamp01(max);
        }
        // Red through amber to green, so the middle doesn't go muddy; unavailable is a faint empty cell.
        static Color HeatColor(float value)
        {
            if (value < 0) return new Color(1, 1, 1, 0.04f);
            value = Mathf.Clamp01(value);
            return value < 0.5f ? Color.Lerp(MLUI.Bad, MLUI.Warn, value * 2f) : Color.Lerp(MLUI.Warn, MLUI.Good, value * 2f - 1f);
        }
        void OnDestroy()
        {
            if (session == null) return;
            session.Changed -= Refresh; session.Reloaded -= OnSessionReloaded; session.Metrics -= OnMetrics;
            session.ClassAccuracyUpdated -= OnClassAccuracy; session.ProbeUpdated -= OnProbe;
        }
    }
}
