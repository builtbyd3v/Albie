using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class TrainingDash : MonoBehaviour
    {
        public MLSession session;
        TMP_Text status, warning;
        Image progress;
        MLCurve loss, accuracy;
        RectTransform summary;
        void Start()
        {
            session = MLSession.Resolve(session); var root = GetComponent<ForgePanel>().Content;
            status = MLUI.Text(root, "", 8, 0, 750, 25);
            MLUI.Fill(root, 8, 29, 740, 6, new Color(.2f,.25f,.3f)); progress = MLUI.Fill(root, 8, 29, 0, 6, Color.cyan);
            MLUI.Text(root, "LOSS    blue: train / orange: val", 8, 42, 330, 24, 13);
            MLUI.Text(root, "ACCURACY    0–100%", 380, 42, 330, 24, 13);
            loss = MLUI.Rect("Loss curves", root, 8, 70, 330, 95).gameObject.AddComponent<MLCurve>(); loss.session = session; loss.raycastTarget = false;
            accuracy = MLUI.Rect("Accuracy curves", root, 380, 70, 330, 95).gameObject.AddComponent<MLCurve>(); accuracy.session = session; accuracy.accuracy = true; accuracy.raycastTarget = false;
            warning = MLUI.Text(root, "", 8, 175, 630, 28, 14); warning.color = new Color(1,.6f,.25f);
            MLUI.Button(root, "Replay", 650, 172, 90, 28, session.Restart);
            summary = MLUI.Rect("Evaluation", root, 8, 210, 740, 300);
            session.Changed += Refresh; Refresh();
        }
        void Refresh()
        {
            int e = session.EpochIndex;
            status.text = e < 0 ? "Waiting for mock training…" : $"{(session.Done ? "DONE" : "TRAINING")}   Epoch {e+1}/{session.TotalEpochs}   LR {session.Data.metrics[e].lr:G3}   val acc {session.Data.metrics[e].val_acc:P1}";
            progress.rectTransform.sizeDelta = new Vector2(740f * (e+1) / Mathf.Max(1,session.TotalEpochs), 6);
            warning.text = session.Overfit ? "OVERFIT: validation loss rose twice while training loss fell" : "";
            loss.SetVerticesDirty(); accuracy.SetVerticesDirty(); MLUI.Clear(summary);
            if (!session.Done) return;
            int n = session.Data.classes.Length; var matrix = new int[n,n];
            foreach (var s in session.Data.samples) if (!s.removed) matrix[s.true_idx,s.pred_idx]++;
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
                    b.image.color = Color.Lerp(new Color(.12f,.17f,.24f), r == c ? new Color(.1f,.65f,.4f) : new Color(.85f,.25f,.2f), matrix[r,c]/(float)max);
                }
                MLUI.Text(summary, $"{r}: {session.Data.classes[r]}  {(count == 0 ? "—" : (matrix[r,r]/(float)count).ToString("P0"))}", 370, 30+r*23, 330, 23, 13);
            }
        }
        void OnDestroy() { if (session != null) session.Changed -= Refresh; }
    }
}
