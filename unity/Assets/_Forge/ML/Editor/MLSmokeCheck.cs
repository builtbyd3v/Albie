using System;
using Forge.ML;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Batch invocation: Unity -batchmode -projectPath ... -executeMethod MLSmokeCheck.Run
// Deliberately omit -quit: the check exits after Play mode has initialized the panels.
[InitializeOnLoad]
public static class MLSmokeCheck
{
    const string Key = "Forge.ML.Smoke";
    static double started;
    static MLSmokeCheck() { EditorApplication.playModeStateChanged += OnPlayMode; }
    public static void BuildAndRun() { MLSandboxBuilder.Build(); Run(); }
    [MenuItem("Forge/ML/Validate Sandbox")]
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/_Forge/ML/MLSandbox.unity");
        SessionState.SetBool(Key, true);
        EditorApplication.isPlaying = true;
    }
    static void OnPlayMode(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Key, false) || state != PlayModeStateChange.EnteredPlayMode) return;
        started = EditorApplication.timeSinceStartup; EditorApplication.update += Check;
    }
    static void Check()
    {
        if (EditorApplication.timeSinceStartup - started < 2) return;
        EditorApplication.update -= Check; SessionState.SetBool(Key, false);
        try
        {
            var session = UnityEngine.Object.FindAnyObjectByType<MLSession>();
            Require(session != null && session.Data != null, "Session loads mock fixture");
            session.replay = true;
            Require(session.UsesGeneratedMocks && session.Data.samples.Length == 1000, "Shared generated mocks load all 1000 samples");
            Require(session.Atlas.width == 2048 && session.Atlas.height == 2048, "Full-resolution atlas is imported");
            var firstUV = session.UV(0); var nextRowUV = session.UV(32); var lastUV = session.UV(999);
            Require(Mathf.Approximately(firstUV.y, 31f/32) && Mathf.Approximately(firstUV.width, 1f/32), "First atlas row is V-flipped");
            Require(Mathf.Approximately(nextRowUV.y, 30f/32) && Mathf.Approximately(lastUV.x, 7f/32) && Mathf.Approximately(lastUV.y, 0), "Atlas row transition and final sample UVs");
            var counts = new int[session.Data.classes.Length, session.Data.classes.Length];
            foreach (var s in session.Data.samples) counts[s.true_idx,s.pred_idx]++;
            for (int r=0;r<counts.GetLength(0);r++) for(int c=0;c<counts.GetLength(1);c++)
                Require(counts[r,c] == session.Data.confusion[r][c], "Results agree with generated confusion matrix");
            var stack = UnityEngine.Object.FindAnyObjectByType<DataStack>();
            Require(stack != null, "DataStack starts");
            Require(UnityEngine.Object.FindAnyObjectByType<Model3D>() != null, "Model3D starts");
            var wall = UnityEngine.Object.FindAnyObjectByType<ResultsWall>();
            var dash = UnityEngine.Object.FindAnyObjectByType<TrainingDash>();
            Require(wall != null && dash != null, "Results and training panels start");
            var vizPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Forge/ML/Prefabs/VizCard.prefab");
            var regressionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Forge/ML/Prefabs/RegressionResults.prefab");
            Require(vizPrefab != null && vizPrefab.GetComponent<VizCard>() != null, "VizCard prefab is generated");
            Require(regressionPrefab != null && regressionPrefab.GetComponent<RegressionResults>() != null, "Regression results prefab is generated");
            Require(Resources.Load<GameObject>("MLPanels/VizCard") != null && Resources.Load<GameObject>("MLPanels/RegressionResults") != null, "Runtime Resources prefabs are generated");
            var layout = UnityEngine.Object.FindAnyObjectByType<MLLayoutController>();
            Require(layout != null, "Sandbox has ML layout event handler");
            ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-training", "ml-viz" } });
            Require(!stack.gameObject.activeSelf && dash.gameObject.activeSelf, "ML layout event switches panel visibility");
            ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-datastack", "ml-model3d", "ml-dots", "ml-live" } });
            Require(stack.gameObject.activeSelf, "Image-classification layout restores core panels");
            foreach (var panel in UnityEngine.Object.FindObjectsByType<ForgePanel>())
                Require(panel.transform.position.y > 1f, "Saved sandbox panel stays at camera height: " + panel.name);
            var vizObject = UnityEngine.Object.Instantiate(vizPrefab); var viz = vizObject.GetComponent<VizCard>();
            foreach (VizCard.Kind kind in Enum.GetValues(typeof(VizCard.Kind))) { viz.kind = kind; viz.Render(); Require(vizObject.GetComponent<ForgePanel>().Content.childCount > 1, "VizCard renders " + kind); }
            UnityEngine.Object.Destroy(vizObject);
            var regressionObject = UnityEngine.Object.Instantiate(regressionPrefab); var regression = regressionObject.GetComponent<RegressionResults>(); regression.Initialize();
            Require(regression.samples.Length == 24, "Regression fixture loads mock rows"); regression.Show(5);
            UnityEngine.Object.Destroy(regressionObject);
            var content = wall.GetComponent<ForgePanel>().Content;
            Require(content.Find("Dot wall") != null && wall.VisibleDotCount == session.Data.samples.Length, "One instanced dot per validation image renders");
            var sample = Array.Find(session.Data.samples, s => !s.correct);
            Require(sample != null, "Fixture includes wrong predictions");
            int original = sample.true_idx;
            Require(!sample.correct, "Fixture includes known wrong prediction");
            session.Relabel(sample, sample.pred_idx);
            Require(sample.correct && sample.label_idx == sample.pred_idx, "Relabel updates correctness");
            session.Remove(sample);
            Require(sample.removed && wall.VisibleDotCount == session.Data.samples.Length - 1, "Remove refreshes dot wall");
            session.ApplySampleUpdate(sample.id, original, false);
            session.SelectPair(original,sample.pred_idx);
            int expected = 0;
            foreach (var s in session.Data.samples) if (s.true_idx == original && s.pred_idx == sample.pred_idx) expected++;
            Require(wall.VisibleDotCount == expected, "Confusion pair filters dot wall");
            content.Find("All").GetComponent<Button>().onClick.Invoke();
            Require(wall.VisibleDotCount == session.Data.samples.Length, "All resets dot wall filter");
            session.Restart();
            var replay = Newtonsoft.Json.JsonConvert.DeserializeObject<MLReplayEvent[]>(session.mockAssets.events.text);
            foreach (var e in replay) session.ApplyReplayEvent(e);
            Require(session.Done && session.TotalEpochs == 15 && session.EpochIndex == 14, "Timestamped replay completes 15 epochs");
            Require(session.Overfit, "Replay detects overfitting");

            var summary = dash.GetComponent<ForgePanel>().Content.Find("Evaluation");
            Require(Array.FindAll(summary.GetComponentsInChildren<Button>(), b => b.gameObject.activeInHierarchy).Length == session.Data.classes.Length * session.Data.classes.Length, "Completion creates clickable confusion matrix");
            session.Restart(); Require(!session.Done && session.EpochIndex == -1, "Replay resets progress");
            Debug.Log("[ML smoke] PASS: mock loading, panel initialization, relabel, remove, pair filtering, overfit, completion and restart.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); if (Application.isBatchMode) EditorApplication.Exit(1); }
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception("[ML smoke] " + message); }
}
