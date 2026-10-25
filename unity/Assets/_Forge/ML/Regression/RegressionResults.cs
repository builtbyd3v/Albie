using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public sealed class RegressionResults : MonoBehaviour
    {
        [System.Serializable] public class Sample { public string id; public float yTrue, yPred; }
        public Sample[] samples;
        RectTransform root, detail;
        string message;  // shown instead of the scatter while the hub has no regression results
        void Start() => Initialize();
        void OnEnable() { ForgeEvents.OnMlResultsReady += OnResultsReady; ForgeNet.StatusChanged += OnNetStatus; }
        void OnDisable() { ForgeEvents.OnMlResultsReady -= OnResultsReady; ForgeNet.StatusChanged -= OnNetStatus; }
        public void Initialize()
        {
            root = GetComponent<ForgePanel>().Content;
            if (samples == null || samples.Length == 0) samples = DemoSamples();
            Draw();
            if (ForgeNet.Status == ForgeNetStatus.Live) LoadFromHub(null);
        }
        void OnNetStatus(ForgeNetStatus status) { if (status == ForgeNetStatus.Live && root != null) LoadFromHub(null); }
        void OnResultsReady(global::MlResultsReady ready) { if (ready != null && root != null) LoadFromHub(ready.runId); }
        // Live hub: the run's GET /ml/results ({samples: [{id, y_true, y_pred, residual}]}); without a run id the
        // latest finished one. A run that never evaluated (a backtest) has none: say so instead of demo points.
        void LoadFromHub(string runId)
        {
            if (!string.IsNullOrEmpty(runId)) { FetchResults(runId); return; }
            ForgeNet.Get("/ml/runs", text =>
            {
                foreach (var run in JArray.Parse(text))
                    if (run.Value<string>("status") == "done") { FetchResults(run.Value<string>("run_id")); return; }
                ShowMessage("No predicted-vs-actual results yet: they appear after a run that evaluates on validation rows.");
            }, _ => { });
        }
        void FetchResults(string runId)
        {
            ForgeNet.Get($"/ml/results/{runId}", text =>
            {
                var loaded = new List<Sample>();
                foreach (var row in JObject.Parse(text)["samples"] as JArray ?? new JArray())
                {
                    var yTrue = row["y_true"]; var yPred = row["y_pred"];
                    if (yTrue == null || yPred == null || yTrue.Type == JTokenType.Null || yPred.Type == JTokenType.Null) continue;
                    loaded.Add(new Sample { id = row.Value<string>("id"), yTrue = yTrue.Value<float>(), yPred = yPred.Value<float>() });
                }
                if (loaded.Count == 0) { ShowMessage($"Run {runId} has no regression results."); return; }
                samples = loaded.ToArray(); message = null; Draw();
            }, _ => ShowMessage("No predicted-vs-actual results yet: they appear after a run that evaluates on validation rows."));
        }
        void ShowMessage(string text) { message = text; Draw(); }
        void Draw()
        {
            MLUI.Clear(root); MLUI.Text(root, "Predicted vs actual", 8, 4, 450, 25, 16);
            if (message != null) { MLUI.Text(root, message, 14, 60, 700, 60, 16); detail = null; return; }
            var plot = MLUI.Rect("Regression scatter", root, 14, 38, 420, 280);
            MLUI.Fill(plot, 0, 274, 410, 2, new Color(.45f,.55f,.65f)); MLUI.Fill(plot, 0, 0, 2, 276, new Color(.45f,.55f,.65f));
            // One shared range for both axes (min to max, so negative targets fit), keeping the diagonal honest.
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var sample in samples) { lo = Mathf.Min(lo, sample.yTrue, sample.yPred); hi = Mathf.Max(hi, sample.yTrue, sample.yPred); }
            float span = Mathf.Max(1e-6f, hi - lo);
            for (int i = 0; i < samples.Length; i++) { var s = samples[i]; var dot = MLUI.Rect("Point " + i, plot, (s.yTrue - lo) / span * 398 - 5, 270 - (s.yPred - lo) / span * 258 - 5, 11, 11).gameObject; dot.AddComponent<Image>().color = new Color(1f,.62f,.22f); var point = dot.AddComponent<RegressionPoint>(); point.owner = this; point.index = i; }
            MLUI.Text(root, "actual →", 340, 318, 100, 22, 11); MLUI.Text(root, "predicted ↑", 0, 38, 90, 22, 11);
            detail = MLUI.Rect("Row detail", root, 448, 38, 280, 280); Show(0);
        }
        public void Show(int index)
        {
            if (detail == null || index < 0 || index >= samples.Length) return;
            MLUI.Clear(detail); var s = samples[index]; float residual = s.yPred - s.yTrue;
            MLUI.Text(detail, "Selected dataset row", 0, 0, 260, 26, 15);
            MLUI.Text(detail, $"ID: {s.id}\nActual: {s.yTrue:0.###}\nPredicted: {s.yPred:0.###}\nResidual: {residual:+0.###;-0.###;0}", 0, 42, 260, 115, 15);
            MLUI.Text(detail, residual >= 0 ? "Over-predicted" : "Under-predicted", 0, 190, 240, 25, 14).color = residual >= 0 ? new Color(1,.58f,.3f) : new Color(.35f,.75f,1);
        }
        static Sample[] DemoSamples()
        {
            var result = new Sample[24]; for (int i = 0; i < result.Length; i++) { float actual = 8 + i * 2.7f; result[i] = new Sample { id = "row-" + (i + 1).ToString("000"), yTrue = actual, yPred = actual + Mathf.Sin(i * 1.73f) * 4.2f }; } return result;
        }
    }
    public sealed class RegressionPoint : MonoBehaviour, IPointerClickHandler { public RegressionResults owner; public int index; public void OnPointerClick(PointerEventData eventData) { owner?.Show(index); } }
}
