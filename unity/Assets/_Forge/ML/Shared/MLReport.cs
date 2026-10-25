using System;
using System.Collections.Generic;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// How well a run did, from the session's samples (predictions and probabilities) and metrics: accuracy, macro
    /// precision / recall / F1, top-3, per-class scores, the most confused pairs, confidence and calibration, the
    /// images most likely mislabelled, and a fit verdict (good fit, overfitting, underfitting). Pure numbers; the
    /// Results panel draws them. Removed images are left out; images without a prediction count as pending.
    /// </summary>
    public sealed class MLReport
    {
        public const int Bins = 10;
        public struct Pair { public int truth, predicted, count; }

        public int classes, evaluated, correct, removed, pending, confidentWrong, inTop3;
        public float accuracy, top3, macroPrecision, macroRecall, macroF1, meanConfidence, confidenceRight, confidenceWrong, ece;
        public float overconfidence, underconfidence;   // the parts of the ECE above and below the diagonal
        public int[,] confusion;
        public int[] support, predictedCount, byF1;
        public float[] precision, recall, f1;
        public readonly int[] histRight = new int[Bins], histWrong = new int[Bins], binCount = new int[Bins];
        public readonly float[] binConfidence = new float[Bins], binAccuracy = new float[Bins];
        public readonly List<Pair> pairs = new List<Pair>();
        public readonly List<int> suspects = new List<int>();   // wrong images, most likely mislabelled first

        // Fit, from the metrics up to the current epoch.
        public enum Fit { Waiting, Training, Good, Overfitting, Underfitting, Evaluated }
        public Fit fit;
        public string verdict, reason;
        public int bestEpoch = -1, epochs;
        public float trainAccuracy = -1, valAccuracy = -1, gap, bestValAccuracy, bestValLoss, finalValLoss;

        public enum Calibration { Good, Mixed, Over, Under }
        public Calibration CalibrationKind => ece < 0.05f ? Calibration.Good : overconfidence > 0.02f && underconfidence > 0.02f ? Calibration.Mixed :
            overconfidence > underconfidence ? Calibration.Over : Calibration.Under;
        public string CalibrationWord => CalibrationKind == Calibration.Good ? "well calibrated" : CalibrationKind == Calibration.Mixed ? "over and under in places" :
            CalibrationKind == Calibration.Over ? "overconfident" : "underconfident";

        public Color VerdictColor => fit == Fit.Good ? MLUI.Good : fit == Fit.Overfitting ? MLUI.Warn : fit == Fit.Underfitting ? MLUI.Bad :
            fit == Fit.Training ? MLUI.Accent : MLUI.TextColor;

        /// <summary>Index of the epoch with the lowest validation loss among the first upTo + 1, or -1.</summary>
        public static int BestEpoch(MLMetrics[] metrics, int upTo)
        {
            int best = -1;
            if (metrics == null) return best;
            // An epoch whose tick never arrived (null) or a run without val loss (NaN, e.g. a backtest) can't be best.
            for (int i = 0; i <= upTo && i < metrics.Length; i++)
                if (metrics[i] != null && !float.IsNaN(metrics[i].val_loss) && (best < 0 || metrics[i].val_loss < metrics[best].val_loss)) best = i;
            return best;
        }

        public static MLReport Build(MLSession session)
        {
            var r = new MLReport();
            var data = session.Data;
            int n = r.classes = data.classes.Length;
            r.confusion = new int[n, n]; r.support = new int[n]; r.predictedCount = new int[n];
            r.precision = new float[n]; r.recall = new float[n]; r.f1 = new float[n];
            float confidenceSum = 0, rightSum = 0, wrongSum = 0;
            var binConfidenceSum = new float[Bins]; var binCorrect = new int[Bins];
            var loss = new float[data.samples.Length];
            for (int i = 0; i < data.samples.Length; i++)
            {
                var s = data.samples[i];
                if (s.removed) { r.removed++; continue; }
                if (s.pred_idx < 0 || s.pred_idx >= n || s.true_idx < 0 || s.true_idx >= n) { r.pending++; continue; }
                r.evaluated++;
                r.confusion[s.true_idx, s.pred_idx]++;
                float confidence = s.probs != null && s.pred_idx < s.probs.Length ? s.probs[s.pred_idx] : 0f;
                confidenceSum += confidence;
                int bin = Mathf.Clamp((int)(confidence * Bins), 0, Bins - 1);
                r.binCount[bin]++; binConfidenceSum[bin] += confidence;
                if (s.correct) { r.correct++; rightSum += confidence; r.histRight[bin]++; binCorrect[bin]++; }
                else
                {
                    wrongSum += confidence; r.histWrong[bin]++;
                    if (confidence >= 0.9f) r.confidentWrong++;
                    float truth = s.probs != null && s.true_idx < s.probs.Length ? s.probs[s.true_idx] : 0f;
                    loss[i] = -Mathf.Log(Mathf.Max(truth, 1e-6f));
                    r.suspects.Add(i);
                }
                if (s.probs != null && s.true_idx < s.probs.Length)
                {
                    int higher = 0; float truth = s.probs[s.true_idx];
                    for (int c = 0; c < s.probs.Length; c++) if (s.probs[c] > truth) higher++;
                    if (higher < 3) r.inTop3++;
                }
            }
            r.suspects.Sort((a, b) => loss[b].CompareTo(loss[a]));

            if (r.evaluated > 0)
            {
                r.accuracy = r.correct / (float)r.evaluated;
                r.top3 = r.inTop3 / (float)r.evaluated;
                r.meanConfidence = confidenceSum / r.evaluated;
            }
            int wrong = r.evaluated - r.correct;
            r.confidenceRight = r.correct > 0 ? rightSum / r.correct : 0f;
            r.confidenceWrong = wrong > 0 ? wrongSum / wrong : 0f;
            for (int b = 0; b < Bins; b++)
            {
                if (r.binCount[b] == 0) { r.binConfidence[b] = r.binAccuracy[b] = -1; continue; }
                r.binConfidence[b] = binConfidenceSum[b] / r.binCount[b];
                r.binAccuracy[b] = binCorrect[b] / (float)r.binCount[b];
                float weight = r.binCount[b] / (float)Mathf.Max(1, r.evaluated), off = r.binConfidence[b] - r.binAccuracy[b];
                r.ece += weight * Mathf.Abs(off);
                if (off > 0) r.overconfidence += weight * off; else r.underconfidence -= weight * off;
            }

            int scored = 0;
            for (int c = 0; c < n; c++)
            {
                for (int k = 0; k < n; k++) { r.support[c] += r.confusion[c, k]; r.predictedCount[c] += r.confusion[k, c]; }
                int tp = r.confusion[c, c];
                r.precision[c] = r.predictedCount[c] > 0 ? tp / (float)r.predictedCount[c] : 0f;
                r.recall[c] = r.support[c] > 0 ? tp / (float)r.support[c] : -1f;
                float p = r.precision[c], q = Mathf.Max(0f, r.recall[c]);
                r.f1[c] = r.support[c] == 0 ? -1f : p + q > 0 ? 2f * p * q / (p + q) : 0f;
                if (r.support[c] == 0) continue;
                scored++; r.macroPrecision += p; r.macroRecall += q; r.macroF1 += r.f1[c];
                for (int k = 0; k < n; k++) if (k != c && r.confusion[c, k] > 0) r.pairs.Add(new Pair { truth = c, predicted = k, count = r.confusion[c, k] });
            }
            if (scored > 0) { r.macroPrecision /= scored; r.macroRecall /= scored; r.macroF1 /= scored; }
            r.pairs.Sort((a, b) => b.count.CompareTo(a.count));
            r.byF1 = new int[n];
            for (int c = 0; c < n; c++) r.byF1[c] = c;
            var f1 = r.f1;
            Array.Sort(r.byF1, (a, b) => f1[b].CompareTo(f1[a]));

            r.JudgeFit(session);
            return r;
        }

        void JudgeFit(MLSession session)
        {
            var metrics = session.Data.metrics;
            int last = Mathf.Min(session.EpochIndex, metrics == null ? -1 : metrics.Length - 1);
            epochs = session.TotalEpochs;
            bestEpoch = BestEpoch(metrics, last);
            if (last >= 0 && metrics[last] != null)
            {
                var m = metrics[last];
                trainAccuracy = m.train_acc; valAccuracy = m.val_acc; gap = m.train_acc - m.val_acc; finalValLoss = m.val_loss;
                if (bestEpoch >= 0) { bestValLoss = metrics[bestEpoch].val_loss; bestValAccuracy = metrics[bestEpoch].val_acc; }
            }
            if (evaluated == 0 && last < 0) { fit = Fit.Waiting; verdict = "Waiting"; reason = "No predictions yet. Train or replay a run."; return; }
            if (!session.Done && last >= 0)
            {
                fit = Fit.Training; verdict = "Training";
                reason = $"Epoch {last + 1} of {Mathf.Max(epochs, last + 1)}. The verdict lands when the run finishes.";
                return;
            }
            if (last < 0) { fit = Fit.Evaluated; verdict = "Evaluated"; reason = "Scored on the validation set; no training history for this run."; return; }
            float rise = bestValLoss > 0 ? finalValLoss / bestValLoss - 1f : 0f;
            if (session.Overfit || (gap > 0.12f && rise > 0.05f))
            {
                fit = Fit.Overfitting; verdict = "Overfitting";
                reason = bestEpoch < last
                    ? $"Val loss bottomed at epoch {bestEpoch + 1} and rose {rise:0%} after, while train kept improving."
                    : $"Train accuracy is {gap * 100f:0} points above val.";
            }
            else if (trainAccuracy < 0.6f || valAccuracy < 0.5f)
            {
                fit = Fit.Underfitting; verdict = "Underfitting";
                reason = "Train and val accuracy are both low: more epochs or a bigger model should help.";
            }
            else
            {
                fit = Fit.Good; verdict = "Good fit";
                reason = $"Train and val are within {Mathf.Abs(gap) * 100f:0} points and val loss is at or near its best.";
            }
        }
    }
}
