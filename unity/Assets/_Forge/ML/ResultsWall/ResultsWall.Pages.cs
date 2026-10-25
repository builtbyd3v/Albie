using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Forge.ML
{
    // The Overview, Classes, Confusion and Confidence tabs of the Results panel, drawn from the MLReport. Pages are
    // rebuilt when shown and when the samples or the run change; sizes are mm from the page's top-left.
    public partial class ResultsWall
    {
        static readonly Color Hairline = new Color(1, 1, 1, 0.07f);

        // ---- Overview: the verdict, the dial, the headline numbers, best and worst classes ----

        void DrawOverviewPage(RectTransform p, float w, float h)
        {
            var r = report;
            const float verdictWidth = 252;
            MLUI.Card(p, 0, 0, verdictWidth, h, 16);
            MLUI.Caps(p, "Model fit", 16, 16, 200);
            var verdict = MLUI.Text(p, r.verdict, 16, 30, verdictWidth - 32, 34, 26); verdict.fontStyle = FontStyles.Bold; verdict.color = r.VerdictColor;
            var reason = MLUI.Text(p, r.reason, 16, 66, verdictWidth - 32, 44, 10.5f); reason.color = MLUI.Sub;

            float accuracy = r.evaluated > 0 ? r.accuracy : r.valAccuracy;
            Color dial = accuracy < 0 ? MLUI.Faint : MLUI.Heat(accuracy);
            var gauge = Graphic<MLGauge>(p, "Accuracy dial", 36, 116, 180, 90); gauge.thickness = 9;
            var big = MLUI.Text(p, accuracy < 0 ? "—" : accuracy.ToString("0.0%"), 36, 164, 180, 32, 26);
            big.fontStyle = FontStyles.Bold; big.alignment = TextAlignmentOptions.Top;
            var caption = MLUI.Caps(p, "Validation accuracy", 36, 196, 180); caption.alignment = TextAlignmentOptions.Top;
            if (reveal && accuracy > 0)
            {
                // The run just finished: the dial and the number count up.
                reveal = false; gauge.Set(0, dial);
                StartCoroutine(ForgeTween.Run(0.9f, 0.1f, t =>
                {
                    if (gauge == null) return;
                    float e = ForgeTween.EaseOutCubic(t); gauge.Set(accuracy * e, dial); big.text = (accuracy * e).ToString("0.0%");
                }));
            }
            else gauge.Set(Mathf.Max(0, accuracy), dial);

            FitBar(p, "Train", r.trainAccuracy, MLUI.Train, 226, verdictWidth);
            FitBar(p, "Val", r.valAccuracy, MLUI.Val, 246, verdictWidth);
            if (r.trainAccuracy >= 0)
            {
                var gap = MLUI.Text(p, $"Train is {r.gap * 100f:0.0} points ahead of val", 16, 268, verdictWidth - 32, 14, 10);
                gap.color = r.gap > 0.1f ? MLUI.Warn : MLUI.Sub;
            }
            MLUI.Fill(p, 16, 292, verdictWidth - 32, 1, Hairline);
            MLUI.Caps(p, "Best epoch", 16, 302, 110);
            var best = MLUI.Text(p, r.bestEpoch < 0 ? "—" : $"{r.bestEpoch + 1}<size=65%><color=#FFFFFF70>  of {Mathf.Max(r.epochs, r.bestEpoch + 1)}</color></size>", 16, 316, 110, 22, 17);
            best.fontStyle = FontStyles.Bold;
            MLUI.Caps(p, "Best val loss", 132, 302, 110);
            var loss = MLUI.Text(p, r.bestEpoch < 0 ? "—" : r.bestValLoss.ToString("0.000"), 132, 316, 110, 22, 17); loss.fontStyle = FontStyles.Bold;

            float x0 = verdictWidth + 12, kw = (w - x0 - 20) / 3f, kh = 66;
            bool any = r.evaluated > 0;
            Kpi(p, x0, 0, kw, kh, "Accuracy", any ? r.accuracy.ToString("0.0%") : "—", any ? $"{r.correct:N0} of {r.evaluated:N0} images" : "no predictions yet", MLUI.Heat(any ? r.accuracy : -1), null);
            Kpi(p, x0 + kw + 10, 0, kw, kh, "Macro F1", any ? r.macroF1.ToString("0.00") : "—", any ? $"precision {r.macroPrecision:0%}  ·  recall {r.macroRecall:0%}" : "", MLUI.Heat(any ? r.macroF1 : -1), () => ShowTab(Tab.Classes));
            Kpi(p, x0 + 2 * (kw + 10), 0, kw, kh, "Top-3 accuracy", any ? r.top3.ToString("0.0%") : "—", "true class among the top 3", MLUI.TextColor, null);
            Kpi(p, x0, kh + 10, kw, kh, "Mean confidence", any ? r.meanConfidence.ToString("0%") : "—", any ? $"right {r.confidenceRight:0%}  ·  wrong {r.confidenceWrong:0%}" : "", MLUI.TextColor, () => ShowTab(Tab.Confidence));
            string calibration = r.CalibrationWord;
            Kpi(p, x0 + kw + 10, kh + 10, kw, kh, "Calibration error", any ? r.ece.ToString("0.0%") : "—", any ? calibration : "", !any ? MLUI.TextColor : r.ece < 0.05f ? MLUI.Good : r.ece < 0.1f ? MLUI.Warn : MLUI.Bad, () => ShowTab(Tab.Confidence));
            Kpi(p, x0 + 2 * (kw + 10), kh + 10, kw, kh, "Mistakes", any ? (r.evaluated - r.correct).ToString("N0") : "—", any ? $"{r.confidentWrong} of them over 90% sure" : "", MLUI.Bad, () => { ShowMistakes(); ShowTab(Tab.Images); });

            float listTop = 2 * (kh + 10), listWidth = (w - x0 - 10) * 0.5f;
            ClassList(p, x0, listTop, listWidth, h - listTop, "Strongest classes", true);
            ClassList(p, x0 + listWidth + 10, listTop, listWidth, h - listTop, "Weakest classes", false);
        }

        void FitBar(RectTransform p, string label, float value, Color color, float y, float width)
        {
            MLUI.Text(p, label, 16, y, 40, 14, 10).color = MLUI.Sub;
            float x = 56, bar = width - x - 66;
            MLUI.Surface(p, x, y + 4, bar, 6, Hairline, 3);
            if (value > 0) MLUI.Surface(p, x, y + 4, Mathf.Max(6, bar * Mathf.Clamp01(value)), 6, color, 3);
            var text = MLUI.Text(p, value < 0 ? "—" : value.ToString("0.0%"), width - 60, y, 44, 14, 10); text.alignment = TextAlignmentOptions.TopRight;
        }

        void Kpi(RectTransform p, float x, float y, float w, float h, string label, string value, string note, Color color, UnityAction click)
        {
            if (click != null) HoverButton("Kpi " + label, p, x, y, w, h, 0.045f, 12).onClick.AddListener(click);
            else MLUI.Card(p, x, y, w, h, 12);
            MLUI.Caps(p, label, x + 12, y + 10, w - 24, 14, 9);
            var big = MLUI.Text(p, value, x + 12, y + 23, w - 24, 26, 21); big.fontStyle = FontStyles.Bold; big.color = color; big.textWrappingMode = TextWrappingModes.NoWrap;
            var small = MLUI.Text(p, note, x + 12, y + 48, w - 24, 12, 8.5f); small.color = MLUI.Faint;
            small.textWrappingMode = TextWrappingModes.NoWrap; small.overflowMode = TextOverflowModes.Truncate;
        }

        void ClassList(RectTransform p, float x, float y, float w, float h, string title, bool strongest)
        {
            var r = report;
            MLUI.Card(p, x, y, w, h, 14);
            MLUI.Caps(p, title, x + 14, y + 12, w - 28);
            float row = Mathf.Min(46f, (h - 34) / 4f);
            int shown = 0;
            for (int k = 0; k < r.classes && shown < 4; k++)
            {
                int c = strongest ? r.byF1[k] : r.byF1[r.classes - 1 - k];
                if (r.f1[c] < 0) continue;   // no images of this class
                float ry = y + 30 + shown * row; shown++;
                HoverButton("Class " + session.Data.classes[c], p, x + 6, ry, w - 12, row - 4, 0f, 9).onClick.AddListener(() => ShowClass(c));
                var className = MLUI.Text(p, session.Data.classes[c], x + 14, ry + 4, w - 80, 16, 12); className.fontStyle = FontStyles.Bold;
                className.textWrappingMode = TextWrappingModes.NoWrap; className.overflowMode = TextOverflowModes.Truncate;
                var score = MLUI.Text(p, r.f1[c].ToString("0.00"), x + w - 60, ry + 4, 46, 16, 12); score.alignment = TextAlignmentOptions.TopRight;
                score.fontStyle = FontStyles.Bold; score.color = MLUI.Heat(r.f1[c]);
                float bar = w - 28;
                MLUI.Surface(p, x + 14, ry + 22, bar, 4, Hairline, 2);
                MLUI.Surface(p, x + 14, ry + 22, Mathf.Max(4, bar * Mathf.Clamp01(r.f1[c])), 4, MLUI.Heat(r.f1[c]), 2);
                var note = MLUI.Text(p, $"precision {r.precision[c]:0%}  ·  recall {Mathf.Max(0, r.recall[c]):0%}", x + 14, ry + 28, w - 28, 12, 8.5f); note.color = MLUI.Faint;
            }
            if (shown == 0) MLUI.Text(p, "No predictions yet", x + 14, y + 34, w - 28, 16, 10.5f).color = MLUI.Faint;
        }

        // ---- Classes: precision, recall, F1 and image count per class, best first ----

        void DrawClassesPage(RectTransform p, float w, float h)
        {
            var r = report; int n = r.classes;
            MLUI.Card(p, 0, 0, w, h, 16);
            const float nameX = 46, precisionX = 190, recallX = 330, f1X = 470, bar = 86;
            MLUI.Caps(p, "Class", nameX + 13, 14, 120);
            MLUI.Caps(p, "Precision", precisionX, 14, 120);
            MLUI.Caps(p, "Recall", recallX, 14, 120);
            MLUI.Caps(p, "F1", f1X, 14, 120);
            MLUI.Caps(p, "Images", w - 96, 14, 80).alignment = TextAlignmentOptions.TopRight;
            MLUI.Fill(p, 12, 32, w - 24, 1, Hairline);
            float top = 38, row = Mathf.Min(31f, (h - top - 6) / Mathf.Max(1, n));
            for (int k = 0; k < n; k++)
            {
                int c = r.byF1[k]; float y = top + k * row, mid = y + row * 0.5f;
                HoverButton("Class row " + session.Data.classes[c], p, 8, y, w - 16, row - 2, 0f, 8).onClick.AddListener(() => ShowClass(c));
                var rank = MLUI.Text(p, (k + 1).ToString(), 18, mid - 7, 20, 14, 10); rank.color = MLUI.Faint;
                MLUI.Dot(p, nameX, mid - 3.5f, 7, MLUI.ClassColor(c));
                var label = MLUI.Text(p, session.Data.classes[c], nameX + 13, mid - 8, precisionX - nameX - 20, 16, 12); label.fontStyle = FontStyles.Bold;
                label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Truncate;
                Metric(p, precisionX, mid, bar, r.support[c] == 0 ? -1 : r.precision[c], false);
                Metric(p, recallX, mid, bar, r.recall[c], false);
                Metric(p, f1X, mid, bar, r.f1[c], true);
                var count = MLUI.Text(p, r.support[c].ToString("N0"), w - 96, mid - 7, 80, 14, 10.5f); count.alignment = TextAlignmentOptions.TopRight; count.color = MLUI.Sub;
            }
        }

        void Metric(RectTransform p, float x, float mid, float bar, float value, bool score)
        {
            MLUI.Surface(p, x, mid - 3, bar, 6, Hairline, 3);
            if (value > 0) MLUI.Surface(p, x, mid - 3, Mathf.Max(6, bar * Mathf.Clamp01(value)), 6, MLUI.Heat(value), 3);
            var text = MLUI.Text(p, value < 0 ? "—" : score ? value.ToString("0.00") : value.ToString("0%"), x + bar + 8, mid - 7, 44, 14, 10.5f);
            if (score) text.fontStyle = FontStyles.Bold;
        }

        // ---- Confusion: the matrix (click a cell for its images) and the most confused pairs ----

        void DrawConfusionPage(RectTransform p, float w, float h)
        {
            var r = report; int n = r.classes;
            const float labels = 78, header = 36;
            float cell = Mathf.Min(28f, (h - header - 30) / Mathf.Max(1, n)), matrixWidth = labels + n * cell + 16;
            MLUI.Card(p, 0, 0, matrixWidth, h, 16);
            MLUI.Caps(p, "Predicted  →", labels, 9, n * cell);
            int max = 1, diagonal = 1;
            for (int a = 0; a < n; a++) for (int b = 0; b < n; b++) { if (a == b) diagonal = Mathf.Max(diagonal, r.confusion[a, b]); else max = Mathf.Max(max, r.confusion[a, b]); }
            for (int c = 0; c < n; c++)
            {
                var column = MLUI.Text(p, Short(session.Data.classes[c], 3), labels + c * cell, 22, cell, 12, 8); column.color = MLUI.Faint; column.alignment = TextAlignmentOptions.Top;
            }
            for (int row = 0; row < n; row++)
            {
                var rowName = MLUI.Text(p, session.Data.classes[row], 10, header + row * cell + cell * 0.5f - 7, labels - 16, 14, 9.5f);
                rowName.color = MLUI.Sub; rowName.alignment = TextAlignmentOptions.TopRight; rowName.textWrappingMode = TextWrappingModes.NoWrap; rowName.overflowMode = TextOverflowModes.Truncate;
                for (int c = 0; c < n; c++)
                {
                    int truthClass = row, predictedClass = c, v = r.confusion[row, c];
                    Color hue = row == c ? MLUI.Good : MLUI.Bad;
                    // Mistakes fade in from nearly nothing, so the few big confusions stand out.
                    float t = v == 0 ? 0 : row == c ? Mathf.Sqrt(v / (float)diagonal) : Mathf.Lerp(0.12f, 1f, Mathf.Pow(v / (float)max, 0.8f));
                    var shade = Color.Lerp(new Color(1, 1, 1, 0.04f), new Color(hue.r, hue.g, hue.b, 0.85f), t);
                    var square = MLUI.Surface(p, labels + c * cell + 1, header + row * cell + 1, cell - 2, cell - 2, shade, 4);
                    square.raycastTarget = true; square.name = $"Confusion {row},{c}";
                    var button = square.gameObject.AddComponent<Button>(); button.targetGraphic = square;
                    button.onClick.AddListener(() => session.SelectPair(truthClass, predictedClass));
                    var colors = button.colors; colors.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1); colors.fadeDuration = 0.06f; button.colors = colors;
                    button.navigation = new Navigation { mode = Navigation.Mode.None };
                    if (v > 0)
                    {
                        var count = MLUI.Text(square.transform, v.ToString(), 0, (cell - 2) * 0.5f - 6, cell - 2, 12, 8.5f);
                        count.alignment = TextAlignmentOptions.Top; count.color = t > 0.55f ? new Color(0.05f, 0.06f, 0.08f) : MLUI.Sub;
                    }
                }
            }
            var axis = MLUI.Caps(p, "Rows: true class  ·  click a cell for its images", 16, header + n * cell + 10, matrixWidth - 32, 12, 8.5f); axis.color = MLUI.Faint;

            float x = matrixWidth + 12, pw = w - x;
            MLUI.Card(p, x, 0, pw, h, 16);
            MLUI.Caps(p, "Most confused pairs", x + 16, 14, pw - 32);
            float rowHeight = (h - 40) / 6f;
            int shown = Mathf.Min(6, r.pairs.Count);
            for (int k = 0; k < shown; k++)
            {
                var pair = r.pairs[k]; float y = 34 + k * rowHeight;
                int t = pair.truth, pr = pair.predicted;
                HoverButton($"Pair {t},{pr}", p, x + 6, y, pw - 12, rowHeight - 4, 0f, 10).onClick.AddListener(() => session.SelectPair(t, pr));
                float photo = Mathf.Min(38f, rowHeight - 12), py = y + (rowHeight - 4 - photo) * 0.5f;
                int found = 0;
                for (int i = 0; i < session.Data.samples.Length && found < 3; i++)
                {
                    var s = session.Data.samples[i];
                    if (s.removed || s.true_idx != t || s.pred_idx != pr) continue;
                    MLUI.Photo(p, session, i, x + 14 + found * (photo + 4), py, photo, photo); found++;
                }
                float tx = x + 14 + 3 * (photo + 4) + 6;
                var title = MLUI.Text(p, $"{session.Data.classes[t]}  <color=#{ColorUtility.ToHtmlStringRGB(MLUI.Bad)}>→ {session.Data.classes[pr]}</color>", tx, y + 7, pw - (tx - x) - 12, 16, 12);
                title.fontStyle = FontStyles.Bold; title.textWrappingMode = TextWrappingModes.NoWrap; title.overflowMode = TextOverflowModes.Truncate;
                float share = r.support[t] > 0 ? pair.count / (float)r.support[t] : 0;
                var note = MLUI.Text(p, $"{pair.count} images  ·  {share:0%} of {session.Data.classes[t]}", tx, y + 25, pw - (tx - x) - 12, 14, 9.5f); note.color = MLUI.Sub;
            }
            if (shown == 0) MLUI.Text(p, "No confusions yet", x + 16, 40, pw - 32, 16, 10.5f).color = MLUI.Faint;
        }

        // ---- Confidence: how sure it was when right and when wrong, and calibration ----

        void DrawConfidencePage(RectTransform p, float w, float h)
        {
            var r = report; float cw = (w - 12) * 0.5f;
            int right = r.correct, wrong = r.evaluated - r.correct;

            MLUI.Card(p, 0, 0, cw, h, 16);
            MLUI.Caps(p, "How sure it was", 16, 14, 200);
            float key = cw - 112;
            MLUI.Dot(p, key, 17, 7, MLUI.Good); MLUI.Text(p, "Right", key + 10, 13, 40, 14, 9.5f).color = MLUI.Sub;
            MLUI.Dot(p, key + 52, 17, 7, MLUI.Bad); MLUI.Text(p, "Wrong", key + 62, 13, 44, 14, 9.5f).color = MLUI.Sub;
            float x0 = 22, y0 = 40, plotWidth = cw - 44, plotHeight = h - y0 - 104;
            for (int g = 0; g <= 4; g++) MLUI.Fill(p, x0, y0 + plotHeight * g / 4f, plotWidth, g == 4 ? 1f : 0.6f, new Color(1, 1, 1, g == 4 ? 0.14f : 0.05f));
            // Each group as a share of itself, so the few wrong answers show their shape next to the many right ones.
            float most = 0.0001f;
            for (int b = 0; b < MLReport.Bins; b++)
            {
                if (right > 0) most = Mathf.Max(most, r.histRight[b] / (float)right);
                if (wrong > 0) most = Mathf.Max(most, r.histWrong[b] / (float)wrong);
            }
            float binWidth = plotWidth / MLReport.Bins;
            for (int b = 0; b < MLReport.Bins; b++)
            {
                float a = right > 0 ? r.histRight[b] / (float)right / most * plotHeight : 0, c = wrong > 0 ? r.histWrong[b] / (float)wrong / most * plotHeight : 0;
                if (a >= 1) MLUI.Surface(p, x0 + b * binWidth + binWidth * 0.12f, y0 + plotHeight - a, binWidth * 0.36f, a, MLUI.Good, 2);
                if (c >= 1) MLUI.Surface(p, x0 + b * binWidth + binWidth * 0.52f, y0 + plotHeight - c, binWidth * 0.36f, c, MLUI.Bad, 2);
            }
            AxisLabel(p, "0%", x0, y0 + plotHeight + 4); AxisLabel(p, "50%", x0 + plotWidth * 0.5f, y0 + plotHeight + 4); AxisLabel(p, "100%", x0 + plotWidth, y0 + plotHeight + 4);
            float sy = y0 + plotHeight + 24;
            MLUI.Caps(p, "When right", 16, sy, 120);
            var whenRight = MLUI.Text(p, right > 0 ? $"{r.confidenceRight:0%} sure" : "—", 16, sy + 13, 140, 22, 17); whenRight.fontStyle = FontStyles.Bold; whenRight.color = MLUI.Good;
            MLUI.Caps(p, "When wrong", cw * 0.5f, sy, 120);
            var whenWrong = MLUI.Text(p, wrong > 0 ? $"{r.confidenceWrong:0%} sure" : "—", cw * 0.5f, sy + 13, 140, 22, 17); whenWrong.fontStyle = FontStyles.Bold; whenWrong.color = MLUI.Bad;
            if (r.confidentWrong > 0) MLUI.Pill(p, $"Show the {r.confidentWrong} confidently wrong", 16, h - 34, cw - 32, 24, ShowSuspects);

            float x1 = cw + 12;
            MLUI.Card(p, x1, 0, cw, h, 16);
            MLUI.Caps(p, "Calibration", x1 + 16, 14, 200);
            var ece = MLUI.Text(p, r.evaluated > 0 ? $"ECE {r.ece:0.0%}" : "", x1 + cw - 116, 11, 100, 16, 12); ece.alignment = TextAlignmentOptions.TopRight; ece.fontStyle = FontStyles.Bold;
            ece.color = r.ece < 0.05f ? MLUI.Good : r.ece < 0.1f ? MLUI.Warn : MLUI.Bad;
            float side = Mathf.Min(cw - 60, h - 40 - 96), px = x1 + (cw - side) * 0.5f + 6, py = 42;
            MLUI.Well(p, px, py, side, side, 6);
            var accuracyAxis = MLUI.Text(p, "accuracy", px - 50, py, 44, 12, 8.5f); accuracyAxis.color = MLUI.Faint; accuracyAxis.alignment = TextAlignmentOptions.TopRight;
            float bin = side / MLReport.Bins;
            for (int b = 0; b < MLReport.Bins; b++)
            {
                if (r.binCount[b] == 0) continue;
                float bar = r.binAccuracy[b] * side;
                if (bar >= 1) MLUI.Surface(p, px + b * bin + 1.5f, py + side - bar, bin - 3, bar, new Color(MLUI.Accent.r, MLUI.Accent.g, MLUI.Accent.b, 0.75f), 2);
            }
            // Perfect calibration: accuracy equals confidence.
            const int dashes = 24;
            for (int d = 0; d < dashes; d++)
            {
                float a = d / (float)dashes, b = (d + 0.55f) / dashes;
                MLUI.Line(p, new Vector2(px + a * side, py + side - a * side), new Vector2(px + b * side, py + side - b * side), 1f, new Color(1, 1, 1, 0.5f));
            }
            AxisLabel(p, "confidence  →", px + side * 0.5f, py + side + 4);
            // Bars above the line: right more often than it claims; below: surer than it should be.
            var kind = r.CalibrationKind;
            string verdict = r.evaluated == 0 ? "" : kind == MLReport.Calibration.Good ? "Well calibrated: when it says 80%, it's right about 80% of the time."
                : kind == MLReport.Calibration.Mixed ? "Mixed: underconfident where bars rise above the line, overconfident where they fall below it."
                : kind == MLReport.Calibration.Over ? $"Overconfident: {r.meanConfidence:0%} sure on average, right {r.accuracy:0%} of the time."
                : $"Underconfident: {r.meanConfidence:0%} sure on average, right {r.accuracy:0%} of the time.";
            var line = MLUI.Text(p, verdict, x1 + 16, py + side + 22, cw - 32, 32, 10.5f);
            line.color = kind == MLReport.Calibration.Good ? MLUI.Good : kind == MLReport.Calibration.Under ? MLUI.Accent : MLUI.Warn;
        }

        void AxisLabel(RectTransform p, string text, float centre, float y)
        {
            var label = MLUI.Text(p, text, centre - 40, y, 80, 12, 8.5f); label.color = MLUI.Faint; label.alignment = TextAlignmentOptions.Top;
        }

        static string Short(string value, int length) => string.IsNullOrEmpty(value) ? "?" : value.Length <= length ? value : value.Substring(0, length);

        T Graphic<T>(RectTransform parent, string name, float x, float y, float w, float h) where T : MaskableGraphic
        {
            // The CanvasRenderer first: added at runtime, the Graphic doesn't always bring one and then draws nothing.
            var go = MLUI.Rect(name, parent, x, y, w, h).gameObject; go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<T>(); graphic.raycastTarget = false; return graphic;
        }
    }
}
