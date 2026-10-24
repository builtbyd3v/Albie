using Forge.ML;
using Oculus.Interaction;
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
        var atlas = new Material(Shader.Find("Forge/ML/InstancedAtlas")) { enableInstancing = true };
        var block = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        SaveMaterial(atlas, Folder+"Atlas.mat"); SaveMaterial(block, Folder+"Layer.mat");
        var stack = Panel<DataStack>("DataStack", "ml-datastack", "Dataset", new Vector2(.40f,.42f));
        stack.GetComponent<DataStack>().atlasMaterial = AssetDatabase.LoadAssetAtPath<Material>(Folder+"Atlas.mat");
        Save(stack,"DataStack");
        var model = Panel<Model3D>("Model3D", "ml-model3d", "Model architecture", new Vector2(.48f,.60f));
        model.GetComponent<Model3D>().blockMaterial = AssetDatabase.LoadAssetAtPath<Material>(Folder+"Layer.mat");
        Save(model,"Model3D");
        Save(Panel<ResultsWall>("ResultsWall", "ml-results", "Validation results", new Vector2(.65f,.45f)),"ResultsWall");
        Save(Panel<TrainingDash>("TrainingDash", "ml-training", "Training dashboard", new Vector2(.78f,.58f)),"TrainingDash");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Desktop camera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,1.5f,-1.8f); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f,.04f,.07f);
        var events = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
        events.AddComponent<InputSystemUIInputModule>();
#else
        events.AddComponent<StandaloneInputModule>();
#endif
        events.AddComponent<PointableCanvasModule>().ExclusiveMode = false;
        new GameObject("Shared ML session").AddComponent<MLSession>();
        Place("DataStack", new Vector3(-.88f,1.35f,0), camera);
        Place("Model3D", new Vector3(-.39f,1.35f,0), camera);
        Place("ResultsWall", new Vector3(.26f,1.35f,0), camera);
        Place("TrainingDash", new Vector3(.02f,1.95f,.15f), camera);
        EditorSceneManager.SaveScene(scene, "Assets/_Forge/ML/MLSandbox.unity");
        AssetDatabase.SaveAssets();
        Debug.Log("[ML] Built four prefabs and MLSandbox. Open sandbox and press Play; hub is not required.");
    }
    static GameObject Panel<T>(string name, string id, string title, Vector2 size) where T : Component
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = name; var panel = go.GetComponent<ForgePanel>(); panel.panelId = id; panel.Title = title; panel.SizeMeters = size;
        for (int i = panel.Content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(panel.Content.GetChild(i).gameObject);
        go.AddComponent<T>(); return go;
    }
    static void Save(GameObject go, string name) { PrefabUtility.SaveAsPrefabAsset(go, Folder+name+".prefab"); Object.DestroyImmediate(go); }
    static void SaveMaterial(Material material, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) { EditorUtility.CopySerialized(material, existing); Object.DestroyImmediate(material); }
        else AssetDatabase.CreateAsset(material,path);
    }
    static void Place(string name, Vector3 position, Camera camera)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Folder+name+".prefab"));
        ((RectTransform)go.transform).anchoredPosition3D = position;
        go.GetComponent<Canvas>().worldCamera = camera;
        PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(go.GetComponent<Canvas>());
        if (go.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null) go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
    }
}
