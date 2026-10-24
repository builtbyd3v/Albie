using System.IO;
using System.Linq;
using Oculus.Interaction;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Forge > CAD > Build CAD Prefabs: (re)builds, all in Assets/_Forge/CAD (and the mock set in Mocks/cad):
/// - Mocks/cad/CadMocks.asset from Mocks/cad/vN/ (refresh those with hub: python -m cad.make_mocks, then copy)
/// - CadViewer.prefab: ForgeVolume variant + CadViewer (the rocket on the desk, slot cad-viewer)
/// - CadParamsPanel.prefab: ForgePanel variant, UI under Content (slot cad-params)
/// - CADSandbox.unity: both prefabs on a desk, mouse UI, hub-off mocks, keyboard shortcuts
/// Builds in an additive scratch scene, so whatever scene is open is left alone. GUIDs are kept on rebuild.
/// </summary>
public static class CadPrefabBuilder
{
    const string Root = "Assets/_Forge/CAD";
    const string PrefabDir = Root + "/Prefabs";
    const string MaterialDir = Root + "/Materials";
    const string MockDir = "Assets/_Forge/Mocks/cad";
    public const string ViewerPath = PrefabDir + "/CadViewer.prefab";
    public const string PanelPath = PrefabDir + "/CadParamsPanel.prefab";
    public const string SandboxPath = Root + "/CADSandbox.unity";
    const string MockSetPath = MockDir + "/CadMocks.asset";

    static readonly Color RowBackground = new Color(1f, 1f, 1f, 0.05f);
    static readonly Color ButtonColor = new Color(0.16f, 0.45f, 0.85f, 0.95f);
    static readonly Color Muted = new Color(1f, 1f, 1f, 0.6f);

    [MenuItem("Forge/CAD/Build CAD Prefabs and Sandbox")]
    public static void BuildAll()
    {
        // Builds inside the sandbox scene. Unity allows one untitled scene at a time, so an Untitled scene (or the
        // sandbox itself) is replaced; any other open scene stays open and the sandbox is added and closed.
        var active = SceneManager.GetActiveScene();
        bool replace = (string.IsNullOrEmpty(active.path) || active.path == SandboxPath) && SceneManager.sceneCount == 1;
        if (replace && active.isDirty && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EnsureFolder(PrefabDir);
        EnsureFolder(MaterialDir);
        var mocks = BuildMockSet();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, replace ? NewSceneMode.Single : NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try
        {
            var viewer = BuildViewer(mocks);
            var panel = BuildPanel(mocks);
            FillSandbox(scene, viewer, panel, mocks);
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
        Debug.Log($"[CadPrefabBuilder] Built {ViewerPath}, {PanelPath}, {SandboxPath} ({mocks.versions.Count} mock versions)");
    }

    // Mocks

    static CadMockSet BuildMockSet()
    {
        var set = AssetDatabase.LoadAssetAtPath<CadMockSet>(MockSetPath);
        if (set == null)
        {
            set = ScriptableObject.CreateInstance<CadMockSet>();
            AssetDatabase.CreateAsset(set, MockSetPath);
        }
        set.versions.Clear();
        var folders = AssetDatabase.GetSubFolders(MockDir)
            .Where(f => Path.GetFileName(f).StartsWith("v"))
            .OrderBy(f => int.TryParse(Path.GetFileName(f).Substring(1), out int n) ? n : int.MaxValue);
        foreach (var folder in folders)
        {
            set.versions.Add(new CadMockSet.Version
            {
                label = Path.GetFileName(folder),
                glb = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/model.glb.bytes"),
                metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/metadata.json"),
                parameters = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/params.json"),
                modelUpdated = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/model_updated.json"),
                flight = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/flight.json"),
            });
        }
        set.regenFailed = AssetDatabase.LoadAssetAtPath<TextAsset>(MockDir + "/regen_failed.json");
        foreach (var v in set.versions)
            if (v.glb == null || v.metadata == null || v.parameters == null || v.modelUpdated == null)
                Debug.LogError($"[CadPrefabBuilder] Mock {v.label} is missing files in {MockDir}/{v.label}");
        EditorUtility.SetDirty(set);
        return set;
    }

    // Viewer

    static GameObject BuildViewer(CadMockSet mocks)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.VolumePath));
        go.name = "CadViewer";
        var volume = go.GetComponent<ForgeVolume>();
        volume.volumeId = "cad-viewer";
        volume.Title = "Rocket";
        volume.turntable = true;
        volume.turntableDegreesPerSecond = 12f;
        volume.SetSize(new Vector3(0.26f, 0.5f, 0.26f));
        var viewer = go.AddComponent<CadViewer>();
        viewer.mocks = mocks;
        viewer.ghostMaterial = GetMaterial("CadGhost", "Forge/CadGhost", m => m.SetColor("_Color", new Color(0.55f, 0.8f, 1f, 0.4f)));
        viewer.outlineMask = GetMaterial("CadOutlineMask", "CADVision/SelectionMask", null);
        viewer.outline = GetMaterial("CadGlow", "CADVision/SelectionOutline", m =>
        {
            m.SetColor("_OutlineColor", new Color(1f, 0.72f, 0.2f, 1f));
            m.SetFloat("_OutlineWidth", 0.006f);
        });
        viewer.cgMaterial = GetMaterial("CadMarkerCG", "Universal Render Pipeline/Unlit", m => m.SetColor("_BaseColor", new Color(0.25f, 0.6f, 1f)));
        viewer.cpMaterial = GetMaterial("CadMarkerCP", "Universal Render Pipeline/Unlit", m => m.SetColor("_BaseColor", new Color(0.95f, 0.3f, 0.25f)));
        PrefabUtility.SaveAsPrefabAsset(go, ViewerPath);
        Object.DestroyImmediate(go);
        return AssetDatabase.LoadAssetAtPath<GameObject>(ViewerPath);
    }

    // Parameters panel

    static GameObject BuildPanel(CadMockSet mocks)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = "CadParamsPanel";
        var panel = go.GetComponent<ForgePanel>();
        panel.panelId = "cad-params";
        panel.Title = "Rocket parameters";
        panel.SizeMeters = new Vector2(0.48f, 0.8f);
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig, sandbox)
        var content = panel.Content;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        var ui = NewRect("CadUI", content);
        Stretch(ui, 0f, 0f, 0f, 0f);
        var column = ui.gameObject.AddComponent<VerticalLayoutGroup>();
        column.spacing = 8f;
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;

        // Header: "rocket v3" + stability chip
        var headerRow = Row("Header", ui, 40f);
        var header = NewText("Title", headerRow, "rocket", 24f, TextAlignmentOptions.MidlineLeft);
        header.fontStyle = FontStyles.Bold;
        Flexible(header.gameObject);
        var chip = NewImage("Stability", headerRow, Rounded(), CadParamsPanel.Stable);
        Fixed(chip.gameObject, 150f, 34f);
        var chipText = NewText("Text", chip.transform, "STABLE", 18f, TextAlignmentOptions.Center);
        chipText.fontStyle = FontStyles.Bold;
        chipText.color = new Color(0.05f, 0.07f, 0.1f);
        Stretch(chipText.rectTransform, 0f, 0f, 0f, 0f);

        // Gauge: 0 to 3 calibers, bands at 1.0 / 1.5 / 2.5
        var gauge = NewRect("Gauge", ui);
        Fixed(gauge.gameObject, -1f, 22f);
        float max = CadParamsPanel.GaugeMaxCal;
        Band(gauge, 0f, 1f / max, CadParamsPanel.Unstable);
        Band(gauge, 1f / max, 1.5f / max, CadParamsPanel.Marginal);
        Band(gauge, 1.5f / max, 2.5f / max, CadParamsPanel.Stable);
        Band(gauge, 2.5f / max, 1f, CadParamsPanel.Marginal);
        var needle = NewImage("Needle", gauge, null, Color.white).rectTransform;
        needle.anchorMin = needle.anchorMax = new Vector2(0.5f, 0.5f);
        needle.sizeDelta = new Vector2(6f, 34f);
        var gaugeLabel = NewText("GaugeLabel", ui, "Static margin", 19f, TextAlignmentOptions.MidlineLeft);
        Fixed(gaugeLabel.gameObject, -1f, 26f);
        var stats = NewText("Stats", ui, "", 16f, TextAlignmentOptions.MidlineLeft);
        stats.color = Muted;
        Fixed(stats.gameObject, -1f, 24f);

        // Parameter rows (built at runtime from GET /cad/params)
        var rows = NewRect("Rows", ui);
        Flexible(rows.gameObject);
        var rowLayout = rows.gameObject.AddComponent<VerticalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childControlWidth = rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = true;
        rowLayout.childForceExpandHeight = false;
        var rowTemplate = BuildRowTemplate(rows);

        // Buttons
        var buttons = Row("Buttons", ui, 48f);
        buttons.GetComponent<HorizontalLayoutGroup>().spacing = 8f;
        var prev = NewButton("Prev", buttons, "<", 44f);
        var undo = NewButton("Undo", buttons, "Undo", -1f);
        var reset = NewButton("Reset", buttons, "Reset", -1f);
        var explode = NewButton("Explode", buttons, "Explode", -1f);
        var fly = NewButton("Fly", buttons, "Fly", -1f);
        var next = NewButton("Next", buttons, ">", 44f);

        var status = NewText("Status", ui, "", 15f, TextAlignmentOptions.TopLeft);
        status.color = Muted;
        status.textWrappingMode = TextWrappingModes.Normal;
        Fixed(status.gameObject, -1f, 40f);

        var component = go.AddComponent<CadParamsPanel>();
        component.mocks = mocks;
        SetRefs(component, ("header", header), ("stabilityChip", chip), ("stabilityText", chipText),
            ("gaugeNeedle", needle), ("gaugeLabel", gaugeLabel), ("stats", stats), ("rows", rows),
            ("rowTemplate", rowTemplate), ("undoButton", undo), ("resetButton", reset), ("explodeButton", explode),
            ("flyButton", fly), ("prevButton", prev), ("nextButton", next),
            ("explodeLabel", explode.GetComponentInChildren<TMP_Text>()), ("status", status));
        PrefabUtility.SaveAsPrefabAsset(go, PanelPath);
        Object.DestroyImmediate(go);
        return AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath);
    }

    static CadParamRow BuildRowTemplate(RectTransform parent)
    {
        var row = NewImage("RowTemplate", parent, Rounded(), RowBackground);
        Fixed(row.gameObject, -1f, 42f);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 4, 4);
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;

        var label = NewText("Label", row.transform, "Param", 18f, TextAlignmentOptions.MidlineLeft);
        Fixed(label.gameObject, 150f, 34f);

        var slider = DefaultControls.CreateSlider(UiResources()).GetComponent<Slider>();
        slider.name = "Slider";
        slider.transform.SetParent(row.transform, false);
        Flexible(slider.gameObject, 26f);
        slider.fillRect.GetComponent<Image>().color = new Color(0.25f, 0.65f, 1f);
        slider.handleRect.GetComponent<Image>().color = Color.white;
        ((RectTransform)slider.handleRect).sizeDelta = new Vector2(26f, 0f);
        slider.transform.Find("Background").GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        slider.gameObject.AddComponent<CadSliderRelease>();

        var options = NewRect("Options", row.transform);
        Flexible(options.gameObject, 34f);
        var optionLayout = options.gameObject.AddComponent<HorizontalLayoutGroup>();
        optionLayout.spacing = 8f;
        optionLayout.childControlWidth = optionLayout.childControlHeight = true;
        optionLayout.childForceExpandWidth = true;
        var optionTemplate = NewButton("OptionTemplate", options, "option", -1f);
        optionTemplate.gameObject.SetActive(false);

        var value = NewText("Value", row.transform, "0 mm", 18f, TextAlignmentOptions.MidlineRight);
        Fixed(value.gameObject, 86f, 34f);

        var component = row.gameObject.AddComponent<CadParamRow>();
        SetRefs(component, ("label", label), ("valueText", value), ("slider", slider), ("options", options),
            ("optionTemplate", optionTemplate));
        row.gameObject.SetActive(false);
        return component;
    }

    // Sandbox

    static void FillSandbox(Scene scene, GameObject viewerPrefab, GameObject panelPrefab, CadMockSet mocks)
    {
        var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        camera.tag = "MainCamera";
        camera.fieldOfView = 50f;
        camera.nearClipPlane = 0.05f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.1f, 0.11f, 0.14f);
        // Plain desktop camera: keep it out of XR, which would otherwise drive it from the headset/simulator pose.
        camera.gameObject.AddComponent<UniversalAdditionalCameraData>().allowXRRendering = false;
        camera.transform.position = new Vector3(0.12f, 1.2f, -0.95f);
        camera.transform.LookAt(new Vector3(0.12f, 1.0f, 0.3f));

        var light = new GameObject("Directional Light", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

        var desk = GameObject.CreatePrimitive(PrimitiveType.Cube);
        desk.name = "Desk";
        desk.transform.position = new Vector3(0.1f, 0.73f, 0.3f);
        desk.transform.localScale = new Vector3(1.3f, 0.04f, 0.7f);

        var viewer = (GameObject)PrefabUtility.InstantiatePrefab(viewerPrefab, scene);
        viewer.transform.position = new Vector3(-0.12f, 0.75f, 0.25f);
        var panel = (GameObject)PrefabUtility.InstantiatePrefab(panelPrefab, scene);
        // A root RectTransform's x/y are serialized as m_AnchoredPosition; setting .position moves it live but
        // doesn't record that override on a prefab instance, so set the serialized value directly.
        ((RectTransform)panel.transform).anchoredPosition3D = new Vector3(0.36f, 1.12f, 0.32f);
        panel.transform.rotation = Quaternion.Euler(0f, 20f, 0f);

        // Mouse UI. ForgePanel's ray/poke canvas asserts a PointableCanvasModule exists (as in Main), but adding one
        // in the Editor turns on its exclusive mode (Reset), which disables every other input module at Start.
        var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule), typeof(PointableCanvasModule));
        events.GetComponent<PointableCanvasModule>().ExclusiveMode = false;
        var session = new GameObject("CadSession").AddComponent<CadSession>();
        session.mode = CadSession.Mode.Auto;
        session.mocks = mocks;
        session.gameObject.AddComponent<CadSandboxKeys>();
    }

    // Helpers (same idiom as ForgePrefabBuilder)

    static Material GetMaterial(string name, string shaderName, System.Action<Material> setup)
    {
        string path = $"{MaterialDir}/{name}.mat";
        var shader = Shader.Find(shaderName);
        if (shader == null) Debug.LogError($"[CadPrefabBuilder] Shader {shaderName} not found");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (shader != null) mat.shader = shader;
        setup?.Invoke(mat);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static RectTransform Row(string name, Transform parent, float height)
    {
        var row = NewRect(name, parent);
        Fixed(row.gameObject, -1f, height);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        return row;
    }

    static void Band(RectTransform gauge, float from, float to, Color color)
    {
        var band = NewImage("Band", gauge, null, color).rectTransform;
        band.anchorMin = new Vector2(from, 0f);
        band.anchorMax = new Vector2(to, 1f);
        band.offsetMin = band.offsetMax = Vector2.zero;
    }

    static Button NewButton(string name, Transform parent, string text, float width)
    {
        var image = NewImage(name, parent, Rounded(), ButtonColor);
        if (width > 0f) Fixed(image.gameObject, width, 40f);
        else Flexible(image.gameObject, 40f);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
        colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        button.colors = colors;
        var label = NewText("Label", image.transform, text, 18f, TextAlignmentOptions.Center);
        label.fontStyle = FontStyles.Bold;
        Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
        return button;
    }

    static void Fixed(GameObject go, float width, float height)
    {
        var e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        if (width > 0f) e.preferredWidth = e.minWidth = width;
        e.preferredHeight = e.minHeight = height;
    }

    static void Flexible(GameObject go, float height = -1f)
    {
        var e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        e.flexibleWidth = 1f;
        e.flexibleHeight = height > 0f ? 0f : 1f;
        if (height > 0f) e.preferredHeight = e.minHeight = height;
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
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
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
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
        foreach (var (field, value) in refs)
        {
            var property = so.FindProperty(field);
            if (property == null) { Debug.LogError($"[CadPrefabBuilder] {target.GetType().Name} has no field {field}"); continue; }
            property.objectReferenceValue = value;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static DefaultControls.Resources UiResources() => new DefaultControls.Resources
    {
        standard = Rounded(),
        background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
        knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
    };

    static Sprite Rounded() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
