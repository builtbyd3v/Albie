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
            Require(UnityEngine.Object.FindAnyObjectByType<DataStack>() != null, "DataStack starts");
            Require(UnityEngine.Object.FindAnyObjectByType<Model3D>() != null, "Model3D starts");
            var wall = UnityEngine.Object.FindAnyObjectByType<ResultsWall>();
            var dash = UnityEngine.Object.FindAnyObjectByType<TrainingDash>();
            Require(wall != null && dash != null, "Results and training panels start");
            foreach (var panel in UnityEngine.Object.FindObjectsByType<ForgePanel>(FindObjectsSortMode.None))
                Require(panel.transform.position.y > 1f, "Saved sandbox panel stays at camera height: " + panel.name);
            var content = wall.GetComponent<ForgePanel>().Content;
            var grid = content.Find("Viewport/Grid");
            Require(ActiveChildren(grid) == session.Data.samples.Length, "All validation images render");
            var sample = session.Data.samples[0]; int original = sample.true_idx;
            Require(!sample.correct, "Fixture includes known wrong prediction");
            session.Relabel(sample, sample.pred_idx);
            Require(sample.correct && sample.label_idx == sample.pred_idx, "Relabel updates correctness");
            session.Remove(sample);
            Require(sample.removed && ActiveChildren(grid) == session.Data.samples.Length - 1, "Remove refreshes wall");
            session.ApplySampleUpdate(sample.id, original, false);
            session.SelectPair(original,sample.pred_idx);
            int expected = 0;
            foreach (var s in session.Data.samples) if (s.true_idx == original && s.pred_idx == sample.pred_idx) expected++;
            Require(ActiveChildren(grid) == expected, "Confusion pair filters wall");
            content.Find("All").GetComponent<Button>().onClick.Invoke();
            Require(ActiveChildren(grid) == session.Data.samples.Length, "All resets pair filter");
            session.Restart();
            foreach (var metric in session.Data.metrics) session.ApplyMetrics(metric);
            Require(session.Overfit, "Replay detects overfitting");
            session.SetRunState("done", session.Data.metrics.Length);
            var summary = dash.GetComponent<ForgePanel>().Content.Find("Evaluation");
            Require(summary.GetComponentsInChildren<Button>().Length == session.Data.classes.Length * session.Data.classes.Length, "Completion creates clickable confusion matrix");
            session.Restart(); Require(!session.Done && session.EpochIndex == -1, "Replay resets progress");
            Debug.Log("[ML smoke] PASS: mock loading, panel initialization, relabel, remove, pair filtering, overfit, completion and restart.");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    static int ActiveChildren(Transform t) { int n = 0; for(int i=0;i<t.childCount;i++) if(t.GetChild(i).gameObject.activeSelf) n++; return n; }
    static void Require(bool condition, string message) { if (!condition) throw new Exception("[ML smoke] " + message); }
}
