using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using Oculus.Interaction.OVR.Editor.QuickActions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

/// <summary>
/// Forge > Build Main Scene: (re)builds Assets/_Forge/Core/Main.unity as a mixed reality scene.
/// Passthrough camera rig with Interaction SDK hands + controllers, MRUK loading the room (simulator synthetic
/// room or headset scan), and the desk workspace: walk up to a table and the mode dock appears on it.
/// Safe to run again; it overwrites the scene. Builds the core prefabs first if they don't exist.
/// </summary>
public static class MainSceneBuilder
{
    const string ScenePath = "Assets/_Forge/Core/Main.unity";
    const string CameraRigPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    const string MrukPath = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";

    [MenuItem("Forge/Build Main Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        EnableMixedRealityFeatures();
        if (!ForgePrefabBuilder.Exist()) ForgePrefabBuilder.BuildAll();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(60f, -30f, 0f);
        RenderSettings.skybox = null;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.65f);

        // Player rig with passthrough as the background. Floor-level origin so heights match MRUK's room.
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPath));
        var manager = rig.GetComponent<OVRManager>();
        manager.isInsightPassthroughEnabled = true;
        manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
        rig.AddComponent<OVRPassthroughLayer>();
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
        }
        OVRQuickActionsAPI.AddOVRInteractionRig(generateAsEditableCopy: false);
        var head = rig.transform.Find("TrackingSpace/CenterEyeAnchor");

        PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(MrukPath));

        // UI events from Interaction SDK rays/pokes
        new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));

        var root = new GameObject("Workspace");
        var locator = root.AddComponent<DeskLocator>();
        locator.head = head;
        var presence = root.AddComponent<DeskPresence>();
        presence.locator = locator;
        presence.head = head;
        var dock = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DockPath), root.transform);
        var workspace = root.AddComponent<Workspace>();
        workspace.presence = presence;
        workspace.dock = dock.GetComponent<DeskDock>();
        workspace.panelPrefab = AssetDatabase.LoadAssetAtPath<ForgePanel>(ForgePrefabBuilder.PanelPath);
        workspace.volumePrefab = AssetDatabase.LoadAssetAtPath<ForgeVolume>(ForgePrefabBuilder.VolumePath);
        workspace.modes = Workspace.DefaultModes();

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddSceneToBuildSettings(ScenePath);
        Debug.Log("[MainSceneBuilder] Built " + ScenePath + ". Activate the Meta XR Simulator, pick a room, press Play, walk up to a table.");
    }

    static void AddSceneToBuildSettings(string path)
    {
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(s => s.path == path);
        scenes.Insert(0, new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    // Passthrough and scene (room data) support in the Meta project config.
    static void EnableMixedRealityFeatures()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (config.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None)
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
        if (config.sceneSupport == OVRProjectConfig.FeatureSupport.None)
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
        if (config.anchorSupport == OVRProjectConfig.AnchorSupport.Disabled)
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
        OVRProjectConfig.CommitProjectConfig(config);
    }
}
