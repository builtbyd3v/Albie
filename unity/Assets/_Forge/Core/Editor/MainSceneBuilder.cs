using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Forge > Build Main Scene: (re)builds Assets/_Forge/Core/Main.unity as a mixed reality scene.
/// Passthrough camera rig, MRUK loading the room (simulator synthetic room or headset scan),
/// tables highlighted so we can see the desk was found, and a DeskLocator.
/// Safe to run again; it overwrites the scene.
/// </summary>
public static class MainSceneBuilder
{
    const string ScenePath = "Assets/_Forge/Core/Main.unity";
    const string CameraRigPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    const string MrukPath = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";
    const string TableMaterialPath = "Packages/com.meta.xr.mrutilitykit/Core/Materials/BLUE_Table.mat";

    [MenuItem("Forge/Build Main Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        EnableMixedRealityFeatures();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(60f, -30f, 0f);
        RenderSettings.skybox = null;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.65f);

        // Player rig with passthrough as the background
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPath));
        rig.GetComponent<OVRManager>().isInsightPassthroughEnabled = true;
        rig.AddComponent<OVRPassthroughLayer>();
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
        }

        PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(MrukPath));

        // Debug: draw tables so it's obvious the desk was detected
        var tables = new GameObject("TableHighlight").AddComponent<EffectMesh>();
        tables.MeshMaterial = AssetDatabase.LoadAssetAtPath<Material>(TableMaterialPath);
        tables.Labels = MRUKAnchor.SceneLabels.TABLE;

        var locator = new GameObject("DeskLocator").AddComponent<DeskLocator>();
        locator.head = rig.transform.Find("TrackingSpace/CenterEyeAnchor");

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddSceneToBuildSettings(ScenePath);
        Debug.Log("[MainSceneBuilder] Built " + ScenePath + ". Activate the Meta XR Simulator, pick a room, press Play.");
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
