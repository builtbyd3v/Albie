using System;
using Forge.ML;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Exercises the saved Main scene and its real Workspace bridge. Button callbacks are checked here;
// controller/hand ray delivery must additionally be checked with an active XR runtime.
[InitializeOnLoad]
public static class MLMainSmokeCheck
{
    const string Pending = "Forge.ML.MainSmoke.Pending";
    const string Restore = "Forge.ML.MainSmoke.RestoreDesktop";
    const string Previous = "Forge.ML.MainSmoke.PreviousDesktop";
    static double started, next;
    static int stage;
    static Workspace workspace;
    static string runtimeError;

    static MLMainSmokeCheck() { EditorApplication.playModeStateChanged += OnPlayMode; }

    [MenuItem("Forge/ML/Validate Main")]
    public static void Run()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("[ML Main smoke] Stop Play mode first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.OpenScene("Assets/_Forge/Core/Main.unity");
        SessionState.SetBool(Previous, EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
        SessionState.SetBool(Restore, true);
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
        SessionState.SetBool(Pending, true);
        EditorApplication.isPlaying = true;
    }

    static void OnPlayMode(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Restore, false))
        {
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, SessionState.GetBool(Previous, false));
            SessionState.EraseBool(Restore);
            SessionState.EraseBool(Pending);
            EditorApplication.update -= Check;
            Application.logMessageReceived -= OnLog;
        }
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
        started = next = EditorApplication.timeSinceStartup;
        stage = 0;
        runtimeError = null;
        Application.logMessageReceived += OnLog;
        EditorApplication.update += Check;
    }

    static void Check()
    {
        try
        {
            double now = EditorApplication.timeSinceStartup;
            Require(runtimeError == null, "Unexpected runtime error: " + runtimeError);
            Require(now - started < 45, "Timed out waiting for Main's desk and panels");
            if (now < next) return;
            if (stage == 0)
            {
                var canvasInput = UnityEngine.Object.FindAnyObjectByType<PointableCanvasModule>();
                Require(canvasInput != null && !canvasInput.ExclusiveMode,
                    "Main's Interaction SDK canvas allows coexisting UI inputs for controller and hand rays");
                workspace = UnityEngine.Object.FindAnyObjectByType<Workspace>();
                Require(workspace != null, "Main contains Workspace");
                workspace.presence.forceSeated = true;
                if (!workspace.presence.AtDesk) return;
                Require(workspace.GetComponent<MLWorkspaceBridge>() != null, "ML bridge installs in Main");
                workspace.Open("ml");
            }
            else if (stage == 1)
            {
                Require(workspace.ActiveMode == "ml", "ML desk opens");
                CheckDefaultPanels();
                workspace.SetLayout(WorkspaceLayout.Flat);
                Require(workspace.layout == WorkspaceLayout.Flat, "Flat layout is selected");
                CheckDefaultPanels();
                workspace.SetLayout(WorkspaceLayout.Curved);
                Require(workspace.layout == WorkspaceLayout.Curved, "Curved layout is restored");
                ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-viz", "ml-regression" } });
            }
            else if (stage == 2)
            {
                Require(Panel("ml-datastack") == null && Panel("ml-training") == null, "Layout swap removes old ML panels");
                var vizPanel = Panel("ml-viz-card");
                var regressionPanel = Panel("ml-regression-results");
                Require(vizPanel != null && regressionPanel != null, "Layout aliases instantiate VizCard and regression in Main");
                var viz = vizPanel.GetComponent<VizCard>();
                var cards = JArray.Parse(Resources.Load<TextAsset>("MLVizCardMocks").text);
                foreach (var card in cards)
                {
                    ForgeNet.Emit("ml.viz", card);
                    Require(vizPanel.Content.GetComponentsInChildren<Graphic>().Length > 0, "VizCard renders " + card["kind"]);
                    foreach (var graphic in vizPanel.Content.GetComponentsInChildren<Graphic>())
                        Require(graphic.GetComponent<CanvasRenderer>() != null, "Graphic has CanvasRenderer: " + graphic.name);
                    Canvas.ForceUpdateCanvases();
                    Require(runtimeError == null, "VizCard render error: " + runtimeError);
                }
                string selected = null;
                viz.SampleSelected += id => selected = id;
                ForgeNet.Emit("ml.viz", cards[3]);
                var point = Array.Find(vizPanel.Content.GetComponentsInChildren<Button>(), b => b.name == "Point val_0000");
                Require(point != null, "Main scatter exposes sample point");
                point.onClick.Invoke();
                Require(selected == "val_0000", "Main scatter selects the correct sample");
                var regression = regressionPanel.GetComponent<RegressionResults>();
                var points = regressionPanel.GetComponentsInChildren<RegressionPoint>();
                Require(regression.samples.Length == 24 && points.Length == 24, "Main regression scatter renders 24 rows");
                points[4].OnPointerClick(new PointerEventData(EventSystem.current));
                Require(Array.Exists(regressionPanel.Content.GetComponentsInChildren<TMPro.TMP_Text>(), t => t.text.Contains("row-005")), "Main regression point opens its row detail");
                ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-datastack", "ml-model3d", "ml-dots", "ml-live" } });
            }
            else
            {
                CheckDefaultPanels();
                Require(Panel("ml-viz-card") == null && Panel("ml-regression-results") == null, "Default layout removes temporary panels");
                Debug.Log("[ML Main smoke] PASS: saved Main scene, ML desk, flat/curved layout, ml.layout aliases, seven VizCard formats, scatter selection, regression row detail and restoration.");
                Finish(0);
                return;
            }
            stage++;
            next = now + 2;
        }
        catch (Exception error) { Debug.LogException(error); Finish(1); }
    }

    static void CheckDefaultPanels()
    {
        foreach (string id in new[] { "ml-datastack", "ml-model3d", "ml-results", "ml-training" })
            Require(Panel(id) != null, "ML panel exists in Main: " + id);
    }
    static ForgePanel Panel(string id) => Array.Find(UnityEngine.Object.FindObjectsByType<ForgePanel>(FindObjectsSortMode.None), p => p.panelId == id);
    static void Require(bool condition, string message) { if (!condition) throw new Exception("[ML Main smoke] " + message); }
    static void OnLog(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) runtimeError ??= message;
    }
    static void Finish(int code)
    {
        EditorApplication.update -= Check;
        Application.logMessageReceived -= OnLog;
        SessionState.EraseBool(Pending);
        if (Application.isBatchMode) EditorApplication.Exit(code);
        else EditorApplication.isPlaying = false;
    }
}
