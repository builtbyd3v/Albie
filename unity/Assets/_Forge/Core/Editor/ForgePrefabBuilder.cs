using Oculus.Interaction.Editor.QuickActions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Builds ForgePanel, DeskDock, ForgeVolume and DesktopRig in Assets/_Forge/Core/Prefabs.
/// Canvases get Interaction SDK ray + poke through the SDK's Quick Actions, so they work with hands and controllers.
/// Forge > Build Core Prefabs only creates missing ones. Forge > Rebuild Core Prefabs overwrites them: the asset GUIDs
/// survive but the objects inside get new IDs, which breaks module prefab variants (CadViewer, CadParamsPanel, ...)
/// until their owners rebuild them. Only rebuild when the base prefab really has to change.
/// </summary>
public static class ForgePrefabBuilder
{
    public const string Dir = "Assets/_Forge/Core/Prefabs";
    public const string PanelPath = Dir + "/ForgePanel.prefab";
    public const string DockPath = Dir + "/DeskDock.prefab";
    public const string VolumePath = Dir + "/ForgeVolume.prefab";
    public const string DesktopRigPath = Dir + "/DesktopRig.prefab";
    public const string MaterialDir = "Assets/_Forge/Core/Materials";

    static readonly Color PanelBackground = new Color(0.07f, 0.09f, 0.13f, 0.85f);
    static readonly Color TitleBar = new Color(0.16f, 0.45f, 0.85f, 0.95f);

    [MenuItem("Forge/Build Core Prefabs (missing only)")]
    public static void BuildMenu() => RunMenu(overwrite: false);

    [MenuItem("Forge/Rebuild Core Prefabs (overwrite, breaks module variants)")]
    public static void RebuildMenu() => RunMenu(overwrite: true);

    static void RunMenu(bool overwrite)
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = SceneManager.GetActiveScene().path;
        BuildAll(overwrite);
        if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous);
    }

    public static bool Exist() =>
        AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath) != null &&
        AssetDatabase.LoadAssetAtPath<GameObject>(DockPath) != null &&
        AssetDatabase.LoadAssetAtPath<GameObject>(VolumePath) != null &&
        AssetDatabase.LoadAssetAtPath<GameObject>(DesktopRigPath) != null;

    /// <summary>
    /// Builds in a throwaway scene (the SDK wizards work on scene objects). Replaces the open scene.
    /// Without <paramref name="overwrite"/>, existing prefabs are left alone (see the class summary for why).
    /// </summary>
    public static void BuildAll(bool overwrite = false)
    {
        ForgeLookBuilder.Build();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EnsureFolder(Dir);
        EnsureFolder(MaterialDir);
        var built = new System.Collections.Generic.List<string>();
        void Build(string path, System.Action build)
        {
            if (!overwrite && AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
            build();
            built.Add(System.IO.Path.GetFileNameWithoutExtension(path));
        }
        Build(PanelPath, BuildPanel);
        Build(DockPath, BuildDock);
        Build(VolumePath, BuildVolume);
        Build(DesktopRigPath, BuildDesktopRig);
        AssetDatabase.SaveAssets();
        Debug.Log(built.Count == 0 ? "[ForgePrefabBuilder] All core prefabs exist; nothing built."
                                   : $"[ForgePrefabBuilder] Built {string.Join(", ", built)} in {Dir}");
    }

    static void BuildDesktopRig()
    {
        var root = new GameObject("DesktopRig");
        var body = root.AddComponent<CharacterController>();
        body.height = 1.8f;
        body.radius = 0.25f;
        body.center = new Vector3(0f, 0.9f, 0f);
        body.stepOffset = 0.3f;

        var head = new GameObject("Head", typeof(Camera), typeof(AudioListener));
        head.tag = "MainCamera";
        head.transform.SetParent(root.transform, false);
        var cam = head.GetComponent<Camera>();
        cam.nearClipPlane = 0.03f;
        cam.fieldOfView = 70f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);

        var hud = new GameObject("HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        hud.transform.SetParent(root.transform, false);
        hud.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = hud.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var crosshair = NewImage("Crosshair", hud.transform, Knob(), new Color(1f, 1f, 1f, 0.9f));
        crosshair.type = Image.Type.Simple;
        crosshair.raycastTarget = false;
        crosshair.rectTransform.sizeDelta = new Vector2(10f, 10f);

        var hint = NewText("Hint", hud.transform, "", 30f, TextAlignmentOptions.BottomLeft);
        hint.color = new Color(1f, 1f, 1f, 0.8f);
        Stretch(hint.rectTransform, 24f, 18f, 24f, 0f);

        var rig = root.AddComponent<DesktopRig>();
        SetRefs(rig, ("cam", cam), ("crosshair", crosshair.gameObject), ("hint", hint));
        Save(root, DesktopRigPath);
    }

    /// <summary>Plain lit material (Materials/Room{name}.mat) for drawing the room in desktop mode.</summary>
    public static Material RoomMaterial(string name, Color color)
    {
        string path = $"{MaterialDir}/Room{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        EnsureFolder(MaterialDir);
        mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", 0.15f);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static void BuildPanel()
    {
        var root = NewCanvas("ForgePanel", new Vector2(600f, 400f));
        Stretch(NewImage("Background", root.transform, Rounded(), PanelBackground).rectTransform, 0f, 0f, 0f, 0f);

        var bar = NewImage("TitleBar", root.transform, Rounded(), TitleBar).rectTransform;
        bar.anchorMin = new Vector2(0f, 1f);
        bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0.5f, 1f);
        bar.sizeDelta = new Vector2(0f, 44f);
        bar.anchoredPosition = Vector2.zero;
        var title = NewText("Title", bar, "Panel", 24f, TextAlignmentOptions.MidlineLeft);
        title.fontStyle = FontStyles.Bold;
        Stretch(title.rectTransform, 16f, 0f, 16f, 0f);

        var content = (RectTransform)new GameObject("Content", typeof(RectTransform)).transform;
        content.SetParent(root.transform, false);
        Stretch(content, 16f, 16f, 16f, 60f);
        var placeholder = NewText("Placeholder", content, "Module UI goes here", 20f, TextAlignmentOptions.Center);
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);

        var panel = root.AddComponent<ForgePanel>();
        SetRefs(panel, ("titleText", title), ("content", content));
        AddCanvasInteraction(root);
        Save(root, PanelPath);
    }

    /// <summary>Rebuild only DeskDock.prefab (no module prefab is based on it), keeping the open scene.</summary>
    [MenuItem("Forge/Rebuild Desk Dock")]
    public static void RebuildDockMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = SceneManager.GetActiveScene().path;
        RebuildDock();
        if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous);
    }

    /// <summary>Overwrites DeskDock.prefab in a throwaway scene (replaces the open scene).</summary>
    public static void RebuildDock()
    {
        ForgeLookBuilder.Build();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        BuildDock();
        AssetDatabase.SaveAssets();
        Debug.Log("[ForgePrefabBuilder] Rebuilt " + DockPath);
    }

    // The dock's visuals are light drawn at runtime (DeskDock.Build); the prefab holds only the input: a canvas with
    // Interaction SDK ray + poke, an invisible plate that catches rays between buttons, and an invisible button.
    static void BuildDock()
    {
        var root = NewCanvas("DeskDock", new Vector2(595f, ForgeTheme.DockHeight));
        var group = root.AddComponent<CanvasGroup>();
        var plate = NewImage("Plate", root.transform, null, Color.clear);
        plate.type = Image.Type.Simple;
        plate.canvasRenderer.cullTransparentMesh = true;
        Stretch(plate.rectTransform, 0f, 0f, 0f, 0f);

        var row = (RectTransform)new GameObject("Row", typeof(RectTransform)).transform;
        row.SetParent(root.transform, false);
        Stretch(row, 0f, 0f, 0f, 0f);

        var hit = NewImage("ButtonTemplate", row, null, Color.clear);
        hit.type = Image.Type.Simple;
        hit.canvasRenderer.cullTransparentMesh = true;
        hit.rectTransform.sizeDelta = new Vector2(80f, 80f);
        var button = hit.gameObject.AddComponent<Button>();
        button.targetGraphic = hit;
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        hit.gameObject.AddComponent<DockButton>();
        hit.gameObject.SetActive(false);

        var dock = root.AddComponent<DeskDock>();
        SetRefs(dock, ("row", row), ("buttonTemplate", button), ("group", group));
        AddCanvasInteraction(root);
        Save(root, DockPath);
    }

    static void BuildVolume()
    {
        var root = new GameObject("ForgeVolume");
        var volume = root.AddComponent<ForgeVolume>();

        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "Base";
        Object.DestroyImmediate(ring.GetComponent<Collider>());
        ring.transform.SetParent(root.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.004f, 0f);
        ring.GetComponent<MeshRenderer>().sharedMaterial = BaseMaterial();

        var spin = new GameObject("Spin").transform;
        spin.SetParent(root.transform, false);
        var content = new GameObject("Content").transform;
        content.SetParent(spin, false);

        var labelCanvas = NewCanvas("Label", new Vector2(300f, 40f));
        labelCanvas.transform.SetParent(root.transform, false);
        labelCanvas.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        var label = NewText("Text", labelCanvas.transform, "Volume", 22f, TextAlignmentOptions.Center);
        Stretch(label.rectTransform, 0f, 0f, 0f, 0f);

        SetRefs(volume, ("spin", spin), ("contentRoot", content), ("baseRing", ring.transform), ("label", label));
        volume.SetSize(volume.size);  // applies base ring + label placement
        Save(root, VolumePath);
    }

    static void AddCanvasInteraction(GameObject canvas)
    {
        QuickActionsAPI.AddRayCanvasInteraction(canvas);
        QuickActionsAPI.AddPokeCanvasInteraction(canvas);
    }

    static GameObject NewCanvas(string name, Vector2 sizeMm)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
        go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = sizeMm;
        rt.localScale = Vector3.one * ForgePanel.MetersPerUnit;
        return go;
    }

    static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = 0.6f;
        image.color = color;
        return image;
    }

    static TMP_Text NewText(string name, Transform parent, string text, float size, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        return tmp;
    }

    static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    static void SetRefs(Object target, params (string field, Object value)[] refs)
    {
        var so = new SerializedObject(target);
        foreach (var (field, value) in refs) so.FindProperty(field).objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static Sprite Rounded() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
    static Sprite Knob() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

    static Material BaseMaterial()
    {
        const string path = MaterialDir + "/ForgeVolumeBase.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;
        mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.SetColor("_BaseColor", new Color(0.15f, 0.5f, 0.85f));
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static void Save(GameObject root, string path)
    {
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
