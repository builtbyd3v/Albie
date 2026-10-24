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
    const string DesktopRoomJsonPath = "Assets/_Forge/Core/Rooms/SimLivingRoom.json";
    const string DeskExtrasPath = "Assets/_Forge/Desk/Prefabs/DeskExtras.prefab";

    // Module prefabs slotted into the workspace modes, by item id (the panel IDs of PROJECT_CONTEXT section 7).
    // Items without one get a placeholder ForgePanel/ForgeVolume.
    static readonly (string id, string path)[] ModulePrefabs =
    {
        ("cad-viewer", "Assets/_Forge/CAD/Prefabs/CadViewer.prefab"),
        ("cad-params", "Assets/_Forge/CAD/Prefabs/CadParamsPanel.prefab"),
        ("cad-drawing", "Assets/_Forge/CAD/Prefabs/CadDrawingPanel.prefab"),
        ("cad-timeline", "Assets/_Forge/CAD/Prefabs/CadVersionsPanel.prefab"),
        ("screen-main", "Assets/_Forge/Screens/ScreenForgePanel.prefab"),
    };

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

        // Hub connection (live, or mocks when the hub isn't running)
        new GameObject("ForgeNet", typeof(ForgeNet));

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
        foreach (var (id, path) in ModulePrefabs)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Debug.LogWarning($"[MainSceneBuilder] {path} not found; {id} stays a placeholder."); continue; }
            foreach (var mode in workspace.modes)
                foreach (var item in mode.items)
                    if (item.id == id) item.prefab = prefab;
        }

        // Bit's desk extras (timers, notes, the movie), on top of any mode: the Desk module's prefab, hosted by
        // WorkspaceExtras (its host field set by name, so Core doesn't need the module's types).
        var extras = root.AddComponent<WorkspaceExtras>();
        extras.workspace = workspace;
        var deskPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DeskExtrasPath);
        if (deskPrefab == null) Debug.LogWarning($"[MainSceneBuilder] {DeskExtrasPath} not found (Forge > Desk > Build Desk Prefabs); no desk extras.");
        else
        {
            var desk = (GameObject)PrefabUtility.InstantiatePrefab(deskPrefab, root.transform);
            var link = new SerializedObject(desk.GetComponent("DeskExtras"));
            link.FindProperty("host").objectReferenceValue = extras;
            link.ApplyModifiedPropertiesWithoutUndo();
        }

        // Desktop mode (no headset or simulator): first-person rig, and the room drawn from MRUK's data since
        // there's no passthrough. Saved inactive; RigSelector turns on whichever rig applies when Play starts.
        var desktop = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DesktopRigPath));
        var desktopRig = desktop.GetComponent<DesktopRig>();
        desktopRig.presence = presence;
        desktop.SetActive(false);
        // The room as clean blocks (the scanned global mesh is too lumpy to read), no ceiling so the sun gets in.
        var desktopRoom = new GameObject("DesktopRoom");
        const MRUKAnchor.SceneLabels Furniture = MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.STORAGE
            | MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT | MRUKAnchor.SceneLabels.SCREEN;
        const MRUKAnchor.SceneLabels Solid = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE
            | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.STORAGE;
        AddEffectMesh(desktopRoom.transform, "RoomFloor", MRUKAnchor.SceneLabels.FLOOR, new Color(0.34f, 0.31f, 0.29f));
        AddEffectMesh(desktopRoom.transform, "RoomWalls", MRUKAnchor.SceneLabels.WALL_FACE, new Color(0.86f, 0.86f, 0.88f));
        AddEffectMesh(desktopRoom.transform, "RoomTable", MRUKAnchor.SceneLabels.TABLE, new Color(0.7f, 0.53f, 0.36f));
        AddEffectMesh(desktopRoom.transform, "RoomFurniture", Furniture, new Color(0.58f, 0.6f, 0.64f));
        AddEffectMesh(desktopRoom.transform, "RoomColliders", Solid, Color.white, colliders: true, hide: true);
        desktopRoom.SetActive(false);

        var selector = new GameObject("RigSelector").AddComponent<RigSelector>();
        selector.xrRig = rig;
        selector.desktopRig = desktopRig;
        selector.desktopRoom = desktopRoom;
        selector.desktopRoomJson = AssetDatabase.LoadAssetAtPath<TextAsset>(DesktopRoomJsonPath);
        selector.locator = locator;
        selector.presence = presence;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddSceneToBuildSettings(ScenePath);
        Debug.Log("[MainSceneBuilder] Built " + ScenePath + ". Activate the Meta XR Simulator, pick a room, press Play, walk up to a table.");
    }

    // MRUK EffectMesh: a mesh for every room surface with these labels, spawned when the room loads.
    static void AddEffectMesh(Transform parent, string name, MRUKAnchor.SceneLabels labels, Color color, bool colliders = false, bool hide = false)
    {
        var effectMesh = new GameObject(name).AddComponent<EffectMesh>();
        effectMesh.transform.SetParent(parent, false);
        effectMesh.MeshMaterial = ForgePrefabBuilder.RoomMaterial(name.Replace("Room", ""), color);
        effectMesh.Labels = labels;
        effectMesh.Colliders = colliders;
        // Backing fields directly; the properties touch spawned meshes, which don't exist yet.
        var so = new SerializedObject(effectMesh);
        so.FindProperty("castShadows").boolValue = false;
        so.FindProperty("hideMesh").boolValue = hide;
        so.ApplyModifiedPropertiesWithoutUndo();
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
