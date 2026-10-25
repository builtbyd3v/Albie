using Forge.ML;
using Oculus.Interaction;
using Oculus.Interaction.OVR.Editor.QuickActions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

public static class MLSandboxBuilder
{
    const string Folder = "Assets/_Forge/ML/Prefabs/";
    [MenuItem("Forge/ML/Build Prefabs and Sandbox")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath) == null)
            throw new System.InvalidOperationException("Core ForgePanel prefab is required. Ask Core to build its prefabs first.");
        BuildMockReferences();
        var atlas = new Material(Shader.Find("Forge/ML/InstancedAtlas")) { enableInstancing = true };
        SaveMaterial(atlas, Folder+"Atlas.mat");
        SaveMaterial(new Material(Shader.Find("Forge/ML/Slab")), Folder+"Slab.mat");
        SaveMaterial(new Material(Shader.Find("Forge/ML/Beam")), Folder+"Beam.mat");
        var stack = Panel<DataStack>("DataStack", "ml-datastack", "Dataset", new Vector2(.60f,.42f));
        stack.GetComponent<DataStack>().atlasMaterial = AssetDatabase.LoadAssetAtPath<Material>(Folder+"Atlas.mat");
        Save(stack,"DataStack");
        var model = Panel<Model3D>("Model3D", "ml-model3d", "Model architecture", new Vector2(.56f,.50f));
        model.GetComponent<Model3D>().slabMaterial = AssetDatabase.LoadAssetAtPath<Material>(Folder+"Slab.mat");
        model.GetComponent<Model3D>().beamMaterial = AssetDatabase.LoadAssetAtPath<Material>(Folder+"Beam.mat");
        Save(model,"Model3D");
        SaveMaterial(new Material(Shader.Find("Forge/ML/DotUI")), Folder+"DotUI.mat");
        var results = Panel<ResultsWall>("ResultsWall", "ml-results", "Results", new Vector2(.74f,.50f));
        results.GetComponent<ResultsWall>().dotMaterialAsset = AssetDatabase.LoadAssetAtPath<Material>(Folder+"DotUI.mat");
        Save(results,"ResultsWall");
        Save(Panel<TrainingDash>("TrainingDash", "ml-training", "Training dashboard", new Vector2(.78f,.58f)),"TrainingDash");
        Save(Panel<VizCard>("VizCard", "ml-viz-card", "Visualization", new Vector2(.78f,.42f)),"VizCard");
        Save(Panel<RegressionResults>("RegressionResults", "ml-regression-results", "Regression results", new Vector2(.78f,.42f)),"RegressionResults");
        CopyToResources("DataStack"); CopyToResources("Model3D"); CopyToResources("ResultsWall");
        CopyToResources("TrainingDash"); CopyToResources("VizCard"); CopyToResources("RegressionResults");
        BuildScene();
        AssetDatabase.SaveAssets();
        Debug.Log("[ML] Built six prefabs and MLSandbox. Open sandbox and press Play; hub is not required.");
    }
    // The sandbox scene alone, from the saved prefabs.
    public static void BuildScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Desktop camera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,1.5f,-1.8f); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f,.04f,.07f);
        // XR rig like Main's (passthrough, floor-level origin, Interaction SDK hands + controllers), used when an
        // XR runtime is running. Standing 1.2 m back from the panels.
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab"));
        rig.transform.position = new Vector3(0,0,-1.2f);
        var manager = rig.GetComponent<OVRManager>();
        manager.isInsightPassthroughEnabled = true;
        manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
        rig.AddComponent<OVRPassthroughLayer>();
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true)) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.clear; }
        OVRQuickActionsAPI.AddOVRInteractionRig(generateAsEditableCopy: false);
        var selector = new GameObject("Rig selector").AddComponent<MLSandboxRig>();
        selector.xrRig = rig; selector.desktopCamera = camera;
        // Invisible floor for the Interaction SDK locomotor (it disables walking without ground under it).
        var floor = new GameObject("Floor").AddComponent<BoxCollider>();
        floor.size = new Vector3(20,.1f,20); floor.center = new Vector3(0,-.05f,0);
        var events = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
        events.AddComponent<InputSystemUIInputModule>();
#else
        events.AddComponent<StandaloneInputModule>();
#endif
        events.AddComponent<PointableCanvasModule>().ExclusiveMode = false;
        var session = new GameObject("Shared ML session"); session.AddComponent<MLSession>(); session.AddComponent<MLLayoutController>();
        Place("DataStack", new Vector3(-1f,1.35f,0), camera);
        Place("Model3D", new Vector3(-.39f,1.35f,0), camera);
        Place("ResultsWall", new Vector3(.31f,1.35f,0), camera);
        Place("TrainingDash", new Vector3(.02f,1.95f,.15f), camera);
        Place("VizCard", new Vector3(.76f,1.35f,.1f), camera).SetActive(false);
        Place("RegressionResults", new Vector3(.76f,1.35f,.1f), camera).SetActive(false);
        EditorSceneManager.SaveScene(scene, "Assets/_Forge/ML/MLSandbox.unity");
    }
    static void BuildMockReferences()
    {
        const string resourceFolder = "Assets/_Forge/ML/Resources";
        if (!AssetDatabase.IsValidFolder(resourceFolder)) AssetDatabase.CreateFolder("Assets/_Forge/ML", "Resources");
        const string path = resourceFolder + "/MLMockAssets.asset";
        var assets = AssetDatabase.LoadAssetAtPath<MLMockAssets>(path);
        if (assets == null) { assets = ScriptableObject.CreateInstance<MLMockAssets>(); AssetDatabase.CreateAsset(assets, path); }
        const string source = "Assets/_Forge/Mocks/ml/";
        assets.classes = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_classes.json");
        assets.dataset = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_dataset_val.json");
        assets.atlasMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_atlas_val.json");
        assets.graph = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_graph.json");
        assets.results = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_results.json");
        assets.confusion = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_confusion.json");
        assets.events = AssetDatabase.LoadAssetAtPath<TextAsset>(source + "ml_events.json");
        assets.atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(source + "ml_atlas_val.png");
        // Validate the imported size without changing the generated atlas importer.
        assets.Load(out _, out _, out _);
        EditorUtility.SetDirty(assets);
    }
    static GameObject Panel<T>(string name, string id, string title, Vector2 size) where T : Component
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = name; var panel = go.GetComponent<ForgePanel>(); panel.panelId = id; panel.Title = title; panel.SizeMeters = size;
        for (int i = panel.Content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(panel.Content.GetChild(i).gameObject);
        go.AddComponent<T>(); return go;
    }
    static void Save(GameObject go, string name) { PrefabUtility.SaveAsPrefabAsset(go, Folder+name+".prefab"); Object.DestroyImmediate(go); }
    static void CopyToResources(string name)
    {
        const string resources = "Assets/_Forge/ML/Resources/MLPanels";
        if (!AssetDatabase.IsValidFolder(resources)) AssetDatabase.CreateFolder("Assets/_Forge/ML/Resources", "MLPanels");
        string source = Folder + name + ".prefab", destination = resources + "/" + name + ".prefab";
        var contents = PrefabUtility.LoadPrefabContents(source);
        PrefabUtility.SaveAsPrefabAsset(contents, destination);
        PrefabUtility.UnloadPrefabContents(contents);
    }
    static void SaveMaterial(Material material, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { EditorUtility.CopySerialized(material, existing); Object.DestroyImmediate(material); }
        else AssetDatabase.CreateAsset(material,path);
    }
    static GameObject Place(string name, Vector3 position, Camera camera)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Folder+name+".prefab"));
        ((RectTransform)go.transform).anchoredPosition3D = position;
        go.GetComponent<Canvas>().worldCamera = camera;
        PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(go.GetComponent<Canvas>());
        if (go.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null) go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        return go;
    }
}
