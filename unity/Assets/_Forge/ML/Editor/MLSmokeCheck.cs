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
        if (EditorApplication.timeSinceStartup - started < 8)
        {
            var liveGraphs = UnityEngine.Object.FindAnyObjectByType<TrainingDash>();
            if (liveGraphs == null || liveGraphs.ProbeCount != 16 || liveGraphs.AccuracyEpochCount == 0) return;
        }
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
            stack.Select();
            var picked = stack.GetComponent<ForgePanel>().Content.Find("Picked image metadata");
            Require(picked != null && picked.gameObject.activeSelf, "Picking a dataset card opens its metadata card");
            bool hasMeta = false;
            foreach (var text in picked.GetComponentsInChildren<TMPro.TMP_Text>(true)) if (text.text.Contains("Path:") && text.text.Contains("Prediction:")) hasMeta = true;
            Require(hasMeta, "Picked card shows path, split, image shape and prediction metadata");
            stack.PutBack();
            Require(UnityEngine.Object.FindAnyObjectByType<Model3D>() != null, "Model3D starts");
            var wall = UnityEngine.Object.FindAnyObjectByType<ResultsWall>();
            var dash = UnityEngine.Object.FindAnyObjectByType<TrainingDash>();
            Require(wall != null && dash != null, "Results and training panels start");
            Debug.Log($"[ML smoke] Replay={session.UsesGeneratedMocks}, live-graph mock={session.HasReplayLiveGraphs}, epoch={session.EpochIndex}, probes={dash.ProbeCount}, accuracy epochs={dash.AccuracyEpochCount}");
            Require(dash.ProbeCount == 16 && dash.AccuracyEpochCount > 0, "Training dashboard animates the 16-sample probe strip and class-accuracy heatmap during mock replay");
            var model = UnityEngine.Object.FindAnyObjectByType<Model3D>();
            Require(model.GroupCount > 0 && model.GroupCount < session.Data.layers.Length, "Model3D groups layers into blocks");
            model.SetOpen(0, true);
            Require(model.IsOpen(0), "Opening a block unfolds its layers");
            string outputShape = string.Join("×", session.Data.layers[0].out_shape); bool readableShape = false;
            foreach (var label in model.GetComponent<ForgePanel>().Content.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (label.text.Contains(outputShape) && label.fontSize >= 11) readableShape = true;
            model.CollapseAll();
            Require(readableShape, "Model layer output dimensions use readable text");
            var predictions = new int[session.Data.samples.Length];
            for (int i = 0; i < predictions.Length; i++) predictions[i] = session.Data.samples[i].pred_idx;
            float[] classAccuracy = null; session.ClassAccuracyUpdated += (_, values) => classAccuracy = values;
            session.ApplyEpochPredictions(predictions, Mathf.Max(1, session.EpochIndex + 1));
            Require(classAccuracy != null && classAccuracy.Length == session.Data.classes.Length, "Per-class accuracy derives from the watched prediction array");
            session.ApplyReplayEvent(new MLReplayEvent
            {
                type = "ml.class_acc", payload = new MLReplayPayload
                {
                    run_id = "mock0001", epoch = 1,
                    per_class = new float?[] { .25f, null, .5f, .75f, 1f, 0f, .1f, .2f, .3f, .4f }
                }
            });
            Require(classAccuracy != null && Mathf.Approximately(classAccuracy[0], .25f) && classAccuracy[1] < 0,
                "ml.class_acc updates the heatmap and renders null classes as unavailable");
            var probes = new MLProbeSample[16];
            for (int i = 0; i < probes.Length; i++) probes[i] = new MLProbeSample
            {
                id = session.Data.samples[i].id, pred_idx = session.Data.samples[i].pred_idx,
                probs = new float[session.Data.classes.Length]
            };
            session.ApplyReplayEvent(new MLReplayEvent
            {
                type = "ml.probe", payload = new MLReplayPayload { run_id = "mock0001", epoch = 1, samples = probes }
            });
            Require(dash.ProbeCount == 16, "ml.probe updates the fixed sample strip");
            var vizPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Forge/ML/Prefabs/VizCard.prefab");
            var regressionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Forge/ML/Prefabs/RegressionResults.prefab");
            Require(vizPrefab != null && vizPrefab.GetComponent<VizCard>() != null, "VizCard prefab is generated");
            Require(regressionPrefab != null && regressionPrefab.GetComponent<RegressionResults>() != null, "Regression results prefab is generated");
            Require(Resources.Load<GameObject>("MLPanels/VizCard") != null && Resources.Load<GameObject>("MLPanels/RegressionResults") != null, "Runtime Resources prefabs are generated");
            var vizMocks = Resources.Load<TextAsset>("MLVizCardMocks");
            Require(vizMocks != null && Newtonsoft.Json.Linq.JArray.Parse(vizMocks.text).Count == 7, "Seven VizCard mock JSON documents are available");
            var vizObject = UnityEngine.Object.Instantiate(vizPrefab); var viz = vizObject.GetComponent<VizCard>();
            var cards = Newtonsoft.Json.Linq.JArray.Parse(vizMocks.text);
            foreach (var card in cards)
            {
                viz.LoadJson(card.ToString()); viz.Render();
                Require(vizObject.GetComponent<ForgePanel>().Content.childCount > 1, "VizCard JSON renders " + viz.kind);
            }
            string selectedVizSample = null; viz.SampleSelected += id => selectedVizSample = id;
            viz.LoadJson(cards[3].ToString()); viz.Render();
            vizObject.GetComponent<ForgePanel>().Content.Find("Point val_0000").GetComponent<Button>().onClick.Invoke();
            Require(!string.IsNullOrEmpty(selectedVizSample), "VizCard scatter click emits its sample id");
            UnityEngine.Object.Destroy(vizObject);
            var layout = UnityEngine.Object.FindAnyObjectByType<MLLayoutController>();
            Require(layout != null, "Sandbox has ML layout event handler");
            ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-training", "ml-viz" } });
            Require(!stack.gameObject.activeSelf && dash.gameObject.activeSelf, "ML layout event switches panel visibility");
            ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-datastack", "ml-model3d", "ml-dots", "ml-live" } });
            Require(stack.gameObject.activeSelf, "Image-classification layout restores core panels");
            var sceneViz = UnityEngine.Object.FindAnyObjectByType<VizCard>(FindObjectsInactive.Include);
            var regressionPanel = UnityEngine.Object.FindAnyObjectByType<RegressionResults>(FindObjectsInactive.Include);
            Require(sceneViz != null && regressionPanel != null && !sceneViz.gameObject.activeSelf && !regressionPanel.gameObject.activeSelf, "VizCard and regression panels are available for layouts");
            ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-regression" } });
            Require(regressionPanel.gameObject.activeSelf && !stack.gameObject.activeSelf && !sceneViz.gameObject.activeSelf, "Regression layout swaps panels without reloading the scene");
            regressionPanel.Initialize();
            regressionPanel.ShowMockRows();
            var points = regressionPanel.GetComponentsInChildren<RegressionPoint>(true);
            Require(regressionPanel.samples.Length == 24 && points.Length == 24, "Regression mock scatter renders clickable rows");
            points[4].OnPointerClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
            var rowDetail = regressionPanel.GetComponent<ForgePanel>().Content.Find("Row detail");
            bool selectedRegressionRow = false;
            if (rowDetail != null) foreach (var text in rowDetail.GetComponentsInChildren<TMPro.TMP_Text>(true)) if (text.text.Contains("row-005")) selectedRegressionRow = true;
            Require(selectedRegressionRow, "Regression point opens its row card");
            ForgeNet.Emit("ml.layout", new { panels = new[] { "ml-datastack", "ml-model3d", "ml-dots", "ml-live" } });
            foreach (var panel in UnityEngine.Object.FindObjectsByType<ForgePanel>())
                Require(panel.transform.position.y > 1f, "Saved sandbox panel stays at camera height: " + panel.name);
            var regressionObject = UnityEngine.Object.Instantiate(regressionPrefab); var regression = regressionObject.GetComponent<RegressionResults>(); regression.Initialize();
            if (ForgeNet.Status == ForgeNetStatus.Connecting)
                Require(regression.samples.Length == 0 && regressionObject.GetComponentsInChildren<RegressionPoint>(true).Length == 0, "Regression panel starts with a message, no points");
            regression.ShowMockRows();
            bool labeled = false;
            foreach (var text in regressionObject.GetComponentsInChildren<TMPro.TMP_Text>(true)) if (text.text.StartsWith("Mock rows")) labeled = true;
            Require(regression.samples.Length == 24 && labeled, "Regression mock rows are labeled as mock"); regression.Show(5);
            UnityEngine.Object.Destroy(regressionObject);
            var content = wall.GetComponent<ForgePanel>().Content;
            Require(wall.ActiveTab == ResultsWall.Tab.Overview && wall.Report != null && wall.Report.evaluated > 0, "Results opens on the Overview tab with a report");
            foreach (ResultsWall.Tab tab in Enum.GetValues(typeof(ResultsWall.Tab)))
            {
                wall.ShowTab(tab);
                Require(content.Find(tab + " page").gameObject.activeSelf && content.Find(tab + " page").childCount > 0, "Results tab draws: " + tab);
            }
            Require(wall.Report.byF1.Length == session.Data.classes.Length && wall.Report.pairs.Count > 0, "Report ranks classes and confused pairs");
            wall.ShowTab(ResultsWall.Tab.Images);
            var images = content.Find("Images page");
            Require(images.Find("Dot wall") != null && wall.VisibleDotCount == session.Data.samples.Length, "One dot per validation image renders");
            ActiveButton(images, "Show mistakes").onClick.Invoke();
            Require(wall.IsGallery && wall.GalleryItemCount > 24, "Show mistakes opens a paged gallery");
            int thumbs = 0; foreach (Transform child in images.Find("Dot wall/Dynamic content")) if (child.gameObject.activeSelf && child.name.StartsWith("Mistake ")) thumbs++;
            Require(thumbs == 24, "Mistakes gallery shows 24 thumbnails per page");
            ActiveButton(images, "›").onClick.Invoke();
            Require(wall.GalleryPage == 1, "Mistakes gallery advances pages");
            ActiveButton(images, "By class").onClick.Invoke();
            Require(wall.IsClassPicker, "By class opens a class picker");
            images.Find("Dot wall/Dynamic content/airplane").GetComponent<Button>().onClick.Invoke();
            Require(wall.IsGallery && wall.ActiveClassFilter == 0, "Class picker filters the mistakes gallery");
            Array.Find(images.Find("Dot wall/Dynamic content").GetComponentsInChildren<Button>(), b => b.name.StartsWith("Mistake ")).onClick.Invoke();
            Require(!string.IsNullOrEmpty(wall.SelectedId), "Gallery thumbnail opens the sample detail card");
            wall.ShowSuspects();
            Require(wall.IsGallery && wall.GalleryItemCount == wall.Report.suspects.Count, "Suspect labels lists the wrong images by loss");
            wall.ShowClass(3);
            Require(wall.ActiveTab == ResultsWall.Tab.Images && !wall.IsGallery && wall.ActiveClassFilter == 3 && wall.VisibleDotCount == wall.Report.support[3], "A class opens its images on the wall");
            ActiveButton(images, "All images").onClick.Invoke();
            Require(!wall.IsGallery && wall.VisibleDotCount == session.Data.samples.Length, "All images returns to the full dot wall");
            var sample = Array.Find(session.Data.samples, s => !s.correct);
            Require(sample != null, "Fixture includes wrong predictions");
            int original = sample.true_idx;
            Require(!sample.correct, "Fixture includes known wrong prediction");
            session.Relabel(sample, sample.pred_idx);
            Require(sample.correct && sample.label_idx == sample.pred_idx, "Relabel updates correctness");
            session.Remove(sample);
            Require(sample.removed && wall.VisibleDotCount == session.Data.samples.Length - 1, "Remove refreshes dot wall");
            session.ApplySampleUpdate(sample.id, original, false);
            wall.ShowTab(ResultsWall.Tab.Overview);
            session.SelectPair(original,sample.pred_idx);
            int expected = 0;
            foreach (var s in session.Data.samples) if (s.true_idx == original && s.pred_idx == sample.pred_idx) expected++;
            Require(wall.ActiveTab == ResultsWall.Tab.Images && wall.VisibleDotCount == expected, "Confusion pair opens its images");
            ActiveButton(images, "All images").onClick.Invoke();
            Require(wall.VisibleDotCount == session.Data.samples.Length, "All resets dot wall filter");
            session.Restart();
            var replay = Newtonsoft.Json.JsonConvert.DeserializeObject<MLReplayEvent[]>(session.mockAssets.events.text);
            foreach (var e in replay) session.ApplyReplayEvent(e);
            Require(session.Done && session.TotalEpochs == 15 && session.EpochIndex == 14, "Timestamped replay completes 15 epochs");
            Require(session.Overfit, "Replay detects overfitting");
            Require(wall.ActiveTab == ResultsWall.Tab.Overview && wall.Report.fit == MLReport.Fit.Overfitting, "Finishing the run opens the Overview with its verdict");
            wall.ShowTab(ResultsWall.Tab.Confusion);
            Require(Array.FindAll(content.GetComponentsInChildren<Button>(), b => b.gameObject.activeInHierarchy && b.name.StartsWith("Confusion ")).Length == session.Data.classes.Length * session.Data.classes.Length, "Confusion tab has a clickable matrix");

            var dashContent = dash.GetComponent<ForgePanel>().Content;
            Require(dashContent.Find("Live graphs") != null && dash.ProbeCount == 16 && dash.AccuracyEpochCount == 15, "Training panel keeps its live graphs after the run");
            session.Restart(); Require(!session.Done && session.EpochIndex == -1, "Replay resets progress");
            var train = ActiveButton(dashContent, "Train");
            Require(train != null, "Training panel exposes Train");
            if (!session.IsLive)
            {
                train.onClick.Invoke();
                Require(!string.IsNullOrEmpty(session.Notice) && session.Notice.Contains("needs the hub"), "Train explains an unavailable hub");
            }
            Debug.Log("[ML smoke] PASS: mock loading, panel initialization, relabel, remove, pair filtering, overfit, completion and restart.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); if (Application.isBatchMode) EditorApplication.Exit(1); }
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception("[ML smoke] " + message); }
    static Button ActiveButton(Transform root, string name) => Array.Find(root.GetComponentsInChildren<Button>(), b => b.gameObject.activeInHierarchy && b.name == name);
}
