using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen streaming prefabs and sandboxes. Safe to run again; each overwrites its output.
/// Forge > Build Screens ForgePanel: ScreenForgePanel.prefab, a variant of Core's ForgePanel for the
/// Workspace's screen-main slot (RawImage under Content + ScreenSurface + both receivers).
/// Forge > Build Screens Sandbox: ScreensSandbox.unity (plain camera, no XR) and the ScreenPanel prefab
/// (quad + ScreenSurface + both receivers).
/// Forge > Build Screens Quest Sandbox: ScreensQuest.unity (passthrough rig, panel 1 m in front of the eyes).
/// Forge > Build Screens Quest APK: builds ScreensQuest.unity to Builds/ScreensQuest.apk for the headset.
/// </summary>
public static class ScreensSandboxBuilder
{
    const string ScenePath = "Assets/_Forge/Screens/ScreensSandbox.unity";
    const string QuestScenePath = "Assets/_Forge/Screens/ScreensQuest.unity";
    const string MaterialPath = "Assets/_Forge/Screens/ScreenPanel.mat";
    const string PrefabPath = "Assets/_Forge/Screens/ScreenPanel.prefab";
    const string ForgePanelPath = "Assets/_Forge/Core/Prefabs/ForgePanel.prefab";
    const string ScreenForgePanelPath = "Assets/_Forge/Screens/ScreenForgePanel.prefab";
    const string CameraRigPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    const string ApkPath = "Builds/ScreensQuest.apk";

    [MenuItem("Forge/Build Screens ForgePanel")]
    public static void BuildForgePanel()
    {
        // A variant keeps Core's frame, title bar and ray/poke wiring, and picks up Hudson's later changes.
        var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePanelPath));
        var panel = root.GetComponent<ForgePanel>();
        panel.panelId = "screen-main";
        panel.Title = "VS Code";
        panel.SizeMeters = new Vector2(0.9f, 0.55f); // Workspace's Main slot; the width then follows the stream
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        var stream = new GameObject("Stream", typeof(RectTransform), typeof(RawImage));
        var rect = (RectTransform)stream.transform;
        rect.SetParent(panel.Content, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var image = stream.GetComponent<RawImage>();
        image.raycastTarget = false; // view only; input is the real laptop keyboard

        root.AddComponent<ScreenSurface>().image = image;
        root.AddComponent<ScreenReceiver>();
        root.AddComponent<JpegScreenReceiver>().enabled = false; // ScreenReceiver turns it on if WebRTC fails

        PrefabUtility.SaveAsPrefabAsset(root, ScreenForgePanelPath);
        Object.DestroyImmediate(root);
        Debug.Log("[ScreensSandboxBuilder] Built " + ScreenForgePanelPath + ". Hudson: put it on the screen-main items in Main's Workspace.");
    }

    [MenuItem("Forge/Build Screens Sandbox")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cam = new GameObject("Main Camera").AddComponent<Camera>();
        cam.tag = "MainCamera";
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f);

        // Unlit, so the screen shows its true colors regardless of lighting
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(material, MaterialPath);
        }

        // About a 24" monitor, 1 m away (section 8.2: VS Code readable at 1 m)
        var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
        panel.name = "ScreenPanel";
        Object.DestroyImmediate(panel.GetComponent<Collider>());
        panel.transform.position = new Vector3(0f, 0f, 1f);
        panel.transform.localScale = new Vector3(0.53f, 0.33f, 1f);
        panel.GetComponent<Renderer>().sharedMaterial = material;
        panel.AddComponent<ScreenSurface>();
        panel.AddComponent<ScreenReceiver>();
        panel.AddComponent<JpegScreenReceiver>().enabled = false; // ScreenReceiver turns it on if WebRTC fails
        PrefabUtility.SaveAsPrefabAssetAndConnect(panel, PrefabPath, InteractionMode.AutomatedAction);

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[ScreensSandboxBuilder] Built " + ScenePath + ". Run signaling.py, share a window from index.html, press Play.");
    }

    [MenuItem("Forge/Build Screens Quest Sandbox")]
    public static void BuildQuest()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null) Build();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Passthrough rig, as in Main. Eye-level origin, so (0, 0, 1) is 1 m straight ahead at startup.
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPath));
        var manager = rig.GetComponent<OVRManager>();
        manager.isInsightPassthroughEnabled = true;
        manager.trackingOriginType = OVRManager.TrackingOrigin.EyeLevel;
        rig.AddComponent<OVRPassthroughLayer>();
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
        }

        var panel = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
        panel.transform.position = new Vector3(0f, 0f, 1f);

        EditorSceneManager.SaveScene(scene, QuestScenePath);
        Debug.Log("[ScreensSandboxBuilder] Built " + QuestScenePath + ". Forge > Build Screens Quest APK makes the headset build.");
    }

    [MenuItem("Forge/Build Screens Quest APK")]
    public static void BuildQuestApk()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(QuestScenePath) == null) BuildQuest();
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { QuestScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
        });
        var s = report.summary;
        Debug.Log($"[ScreensSandboxBuilder] APK {s.result}: {ApkPath}, {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalTime.TotalMinutes:F1} min");
    }
}
