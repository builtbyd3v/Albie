using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Forge > Desk > Build Desk Prefabs and Sandbox: (re)builds, all in Assets/_Forge/Desk:
/// - TimerStack.prefab: headerless ForgePanel variant + TimerStack (the timers' pills; root pivot at the bottom)
/// - NotesPanel.prefab: ForgePanel variant + NotesPanel
/// - MoviePanel.prefab: ForgePanel variant, title bar off (PanelManipulator puts a move bar above it), VideoPlayer +
///   AudioSource + MoviePanel
/// - DeskOrb.prefab: tiny frameless ForgePanel variant + PanelOrb (the dot a panel minimizes into)
/// - Resources/DeskExtras.prefab: DeskExtras + DeskChime with the prefabs above (Main's builder puts one under the
///   Workspace; DeskBootstrap adds it at runtime to any scene with a Workspace that has none)
/// - DeskSandbox.unity: a desk, the desktop rig, the mocks (Mocks/desk/sequence.json) and the number keys
/// The panels build their UI at runtime. Builds in an additive scratch scene; GUIDs are kept on rebuild.
/// </summary>
public static class DeskPrefabBuilder
{
    const string Root = "Assets/_Forge/Desk";
    const string PrefabDir = Root + "/Prefabs";
    public const string TimersPath = PrefabDir + "/TimerStack.prefab";
    public const string NotesPath = PrefabDir + "/NotesPanel.prefab";
    public const string MoviePath = PrefabDir + "/MoviePanel.prefab";
    public const string OrbPath = PrefabDir + "/DeskOrb.prefab";
    public const string ExtrasPath = Root + "/Resources/DeskExtras.prefab";  // Resources: DeskBootstrap loads it
    public const string SandboxPath = Root + "/DeskSandbox.unity";
    const string SequencePath = "Assets/_Forge/Mocks/desk/sequence.json";

    [MenuItem("Forge/Desk/Build Desk Prefabs and Sandbox")]
    public static void BuildAll()
    {
        var active = SceneManager.GetActiveScene();
        bool replace = (string.IsNullOrEmpty(active.path) || active.path == SandboxPath) && SceneManager.sceneCount == 1;
        if (replace && active.isDirty && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        BuildPrefabs();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, replace ? NewSceneMode.Single : NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try
        {
            FillSandbox(scene);
            EditorSceneManager.SaveScene(scene, SandboxPath);
        }
        finally
        {
            if (!replace)
            {
                if (active.IsValid()) SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(scene, true);
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[DeskPrefabBuilder] Built {TimersPath}, {NotesPath}, {MoviePath}, {OrbPath}, {ExtrasPath}, {SandboxPath}");
    }

    [MenuItem("Forge/Desk/Build Desk Prefabs")]
    public static void BuildPrefabs()
    {
        EnsureFolder(Root);
        EnsureFolder(PrefabDir);
        EnsureFolder(Root + "/Resources");

        var timers = PanelVariant("TimerStack", "timer", "Timers", new Vector2(0.46f, 0.064f));
        Bare(timers);
        ((RectTransform)timers.transform).pivot = new Vector2(0.5f, 0f);  // grows upward from the main panel's top
        timers.AddComponent<TimerStack>();
        var timersPrefab = Save(timers, TimersPath);

        var notes = PanelVariant("NotesPanel", "notes", "Notes", new Vector2(0.36f, 0.42f));
        notes.AddComponent<NotesPanel>();
        var notesPrefab = Save(notes, NotesPath);

        var movie = PanelVariant("MoviePanel", "movie", "Movie", new Vector2(1.44f, 0.96f));
        // A screen, not a window: no title bar (renamed, so PanelManipulator adds a move bar above instead), no
        // header line, a thin bezel round the picture.
        var bar = movie.transform.Find("TitleBar");
        bar.gameObject.SetActive(false);
        bar.name = "TitleBar (off)";
        var frame = movie.transform.Find("Background").GetComponent<ForgeSurface>();
        frame.Header = 0f;
        frame.Corner = 24f;
        var moviePanel = movie.GetComponent<ForgePanel>();
        Stretch(moviePanel.Content, 6f, 6f, 6f, 6f);
        movie.AddComponent<AudioSource>();
        movie.AddComponent<VideoPlayer>();
        movie.AddComponent<MoviePanel>();
        var moviePrefab = Save(movie, MoviePath);

        var orb = PanelVariant("DeskOrb", "orb", "", new Vector2(0.1f, 0.1f));
        Bare(orb);
        orb.AddComponent<PanelOrb>();
        var orbPrefab = Save(orb, OrbPath).GetComponent<PanelOrb>();

        var extras = new GameObject("DeskExtras", typeof(AudioSource));
        var chime = extras.AddComponent<DeskChime>();
        var desk = extras.AddComponent<DeskExtras>();
        desk.timersPrefab = timersPrefab;
        desk.notesPrefab = notesPrefab;
        desk.moviePrefab = moviePrefab;
        desk.orbPrefab = orbPrefab;
        desk.chime = chime;
        PrefabUtility.SaveAsPrefabAsset(extras, ExtrasPath);
        Object.DestroyImmediate(extras);
        AssetDatabase.SaveAssets();
    }

    static GameObject PanelVariant(string name, string panelId, string title, Vector2 size)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = name;
        var panel = go.GetComponent<ForgePanel>();
        panel.panelId = panelId;
        panel.Title = title;
        panel.SizeMeters = size;
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
        var placeholder = panel.Content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        return go;
    }

    // No frame, no header: the content is the whole rect (the timers' pills, an orb).
    static void Bare(GameObject go)
    {
        go.transform.Find("Background").gameObject.SetActive(false);
        go.transform.Find("TitleBar").gameObject.SetActive(false);
        Stretch(go.GetComponent<ForgePanel>().Content, 0f, 0f, 0f, 0f);
    }

    static GameObject Save(GameObject go, string path)
    {
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    // ---------------------------------------------------------------- sandbox

    static void FillSandbox(Scene scene)
    {
        var sun = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.65f);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.localScale = Vector3.one * 2f;
        floor.GetComponent<Renderer>().sharedMaterial = ForgePrefabBuilder.RoomMaterial("Floor", new Color(0.34f, 0.31f, 0.29f));
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Wall";
        wall.transform.position = new Vector3(0f, 1.5f, 3.2f);
        wall.transform.localScale = new Vector3(8f, 3f, 0.1f);
        wall.GetComponent<Renderer>().sharedMaterial = ForgePrefabBuilder.RoomMaterial("Walls", new Color(0.86f, 0.86f, 0.88f));
        var desk = GameObject.CreatePrimitive(PrimitiveType.Cube);
        desk.name = "Desk";
        desk.transform.position = new Vector3(0f, 0.72f, 0.45f);
        desk.transform.localScale = new Vector3(1.4f, 0.04f, 0.7f);
        desk.GetComponent<Renderer>().sharedMaterial = ForgePrefabBuilder.RoomMaterial("Table", new Color(0.7f, 0.53f, 0.36f));

        // The desktop rig standing at the desk, eyes at seated height.
        var rigObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.DesktopRigPath), scene);
        rigObject.transform.position = new Vector3(0f, 0f, -0.25f);
        var rig = rigObject.GetComponent<DesktopRig>();
        rig.standingEyeHeight = rig.seatedEyeHeight;
        var camera = rigObject.GetComponentInChildren<Camera>(true);
        // Plain desktop camera: keep it out of XR, which would drive it from the simulator's head (as in CAD's sandbox).
        var cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
        if (cameraData == null) cameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.allowXRRendering = false;
        var head = camera.transform;

        // Mouse UI. ForgePanel's ray/poke canvas asserts a PointableCanvasModule exists; not exclusive (see CAD's sandbox).
        var events = new GameObject("EventSystem", typeof(EventSystem), typeof(PointableCanvasModule));
        events.GetComponent<PointableCanvasModule>().ExclusiveMode = false;
        new GameObject("ForgeNet", typeof(ForgeNet));
        var player = new GameObject("Mock events").AddComponent<ForgeMockPlayer>();
        player.sequence = AssetDatabase.LoadAssetAtPath<TextAsset>(SequencePath);
        if (player.sequence == null) Debug.LogWarning($"[DeskPrefabBuilder] {SequencePath} missing: run python -m desk.make_mocks in hub/ and copy it");

        // The desk frame (front edge, +Z into the desk) the extras lay out in, as Main's DeskPresence makes it.
        var deskFrame = new GameObject("Desk frame").transform;
        deskFrame.position = new Vector3(0f, 0.74f, 0.1f);
        var host = new GameObject("Workspace extras").AddComponent<WorkspaceExtras>();
        host.frame = deskFrame;
        host.head = head;
        var extras = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ExtrasPath), scene);
        extras.GetComponent<DeskExtras>().host = host;
        extras.AddComponent<DeskSandboxKeys>();
    }

    // ---------------------------------------------------------------- helpers

    static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
