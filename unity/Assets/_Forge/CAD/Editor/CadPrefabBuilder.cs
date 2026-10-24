using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
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
/// - Mocks/cad/CadMocks.asset from Mocks/cad/parts.json + Mocks/cad/&lt;part&gt;/vN/ (refresh those with
///   hub: python -m cad.make_mocks, then copy; see the CAD README)
/// - CadViewer.prefab: ForgeVolume variant + CadViewer (the open design on the desk, slot cad-viewer)
/// - CadParamsPanel.prefab: ForgePanel variant, UI under Content (slot cad-params), for any design
/// - CadDrawingPanel.prefab: ForgePanel variant, the version's engineering drawing (slot cad-drawing)
/// - CADSandbox.unity: the prefabs on a desk, mouse UI, hub-off mocks, keyboard shortcuts
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
    public const string DrawingPath = PrefabDir + "/CadDrawingPanel.prefab";
    public const string SandboxPath = Root + "/CADSandbox.unity";
    const string MockSetPath = MockDir + "/CadMocks.asset";

    static readonly Color RowBackground = new Color(1f, 1f, 1f, 0.05f);
    static readonly Color ButtonColor = new Color(0.16f, 0.45f, 0.85f, 0.95f);
    static readonly Color Muted = new Color(1f, 1f, 1f, 0.6f);
    static readonly Color PresetColor = new Color(1f, 1f, 1f, 0.12f);

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
            var drawing = BuildDrawingPanel(mocks);
            FillSandbox(scene, viewer, panel, drawing, mocks);
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
        Debug.Log($"[CadPrefabBuilder] Built {ViewerPath}, {PanelPath}, {DrawingPath}, {SandboxPath} (mocks: " +
                  string.Join(", ", mocks.parts.Select(p => $"{p.part} x{p.versions.Count}")) + ")");
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
        set.parts.Clear();
        set.partList = AssetDatabase.LoadAssetAtPath<TextAsset>(MockDir + "/parts.json");
        set.regenFailed = AssetDatabase.LoadAssetAtPath<TextAsset>(MockDir + "/regen_failed.json");
        if (set.partList == null) { Debug.LogError($"[CadPrefabBuilder] {MockDir}/parts.json is missing"); return set; }
        // Designs in parts.json order (its first one opens first), each with its vN folders in order.
        foreach (var entry in JObject.Parse(set.partList.text)["parts"])
        {
            string part = (string)entry["part"], dir = $"{MockDir}/{part}";
            if (!AssetDatabase.IsValidFolder(dir)) { Debug.LogError($"[CadPrefabBuilder] No mocks in {dir}"); continue; }
            var mock = new CadMockSet.Part { part = part };
            var folders = AssetDatabase.GetSubFolders(dir)
                .Where(f => Path.GetFileName(f).StartsWith("v"))
                .OrderBy(f => int.TryParse(Path.GetFileName(f).Substring(1), out int n) ? n : int.MaxValue);
            foreach (var folder in folders)
            {
                var v = new CadMockSet.Version
                {
                    label = $"{part}/{Path.GetFileName(folder)}",
                    glb = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/model.glb.bytes"),
                    metadata = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/metadata.json"),
                    parameters = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/params.json"),
                    modelUpdated = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/model_updated.json"),
                    flight = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/flight.json"),
                    drawing = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/drawing.png.bytes"),
                };
                if (v.glb == null || v.metadata == null || v.parameters == null || v.modelUpdated == null)
                    Debug.LogError($"[CadPrefabBuilder] Mock {v.label} is missing files in {folder}");
                mock.versions.Add(v);
            }
            set.parts.Add(mock);
        }
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
        volume.Title = "Design";  // CadViewer puts the open design's title here
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
        go.AddComponent<CadTakeApart>();  // Inspect view: take parts off and put them back
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
        panel.Title = "Design";
        panel.SizeMeters = new Vector2(0.48f, 0.6f);  // taller pokes through the wall in desktop mode's corner desk
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

        // Header: "Model rocket v3" + the Designs menu button
        var headerRow = Row("Header", ui, 40f);
        headerRow.GetComponent<HorizontalLayoutGroup>().spacing = 8f;
        var header = NewText("Title", headerRow, "Design", 24f, TextAlignmentOptions.MidlineLeft);
        header.fontStyle = FontStyles.Bold;
        header.textWrappingMode = TextWrappingModes.NoWrap;
        header.overflowMode = TextOverflowModes.Ellipsis;
        Flexible(header.gameObject);
        var designs = NewButton("Designs", headerRow, "Designs", 120f);

        // Presets: one button each, built at runtime
        var presets = Row("Presets", ui, 32f);
        presets.GetComponent<HorizontalLayoutGroup>().spacing = 6f;
        presets.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
        var presetTemplate = NewButton("PresetTemplate", presets, "Preset", -1f);
        Flexible(presetTemplate.gameObject, 32f);
        presetTemplate.GetComponent<Image>().color = PresetColor;
        var presetLabel = presetTemplate.GetComponentInChildren<TMP_Text>();
        presetLabel.fontStyle = FontStyles.Normal;
        presetLabel.textWrappingMode = TextWrappingModes.NoWrap;
        presetLabel.enableAutoSizing = true;  // long names shrink to fit their button instead of wrapping
        presetLabel.fontSizeMin = 10f;
        presetLabel.fontSizeMax = 15f;
        Stretch(presetLabel.rectTransform, 4f, 0f, 4f, 0f);
        presetTemplate.gameObject.SetActive(false);

        // Metrics: gauges for the ones with a range, then a wrapping stats line
        var gauges = NewRect("Gauges", ui);
        var gaugeLayout = gauges.gameObject.AddComponent<VerticalLayoutGroup>();
        gaugeLayout.spacing = 6f;
        gaugeLayout.childControlWidth = gaugeLayout.childControlHeight = true;
        gaugeLayout.childForceExpandWidth = true;
        gaugeLayout.childForceExpandHeight = false;
        var gaugeTemplate = BuildGaugeTemplate(gauges);

        // The stats line and the parameter rows (built at runtime from GET /cad/params) scroll together, so a
        // panel resized small still fits everything.
        var scroll = BuildScroll(ui, out var rows);
        var stats = NewText("Stats", rows, "", 16f, TextAlignmentOptions.TopLeft);
        stats.textWrappingMode = TextWrappingModes.Normal;
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

        // The big view: Inspect / Back to desk, and Reassemble while it's up
        var view = Row("View", ui, 48f);
        view.GetComponent<HorizontalLayoutGroup>().spacing = 8f;
        var inspect = NewButton("Inspect", view, "Inspect", -1f);
        var reassemble = NewButton("Reassemble", view, "Reassemble", -1f);

        var status = NewText("Status", ui, "", 15f, TextAlignmentOptions.TopLeft);
        status.color = Muted;
        status.textWrappingMode = TextWrappingModes.Normal;
        status.overflowMode = TextOverflowModes.Ellipsis;
        Fixed(status.gameObject, -1f, 38f);

        var picker = BuildPicker(ui, out var pickerEntry);

        var component = go.AddComponent<CadParamsPanel>();
        component.mocks = mocks;
        SetRefs(component, ("header", header), ("designsButton", designs), ("picker", picker),
            ("pickerEntry", pickerEntry), ("presetRow", presets), ("presetTemplate", presetTemplate),
            ("gauges", gauges), ("gaugeTemplate", gaugeTemplate), ("stats", stats), ("scroll", scroll),
            ("rows", rows), ("rowTemplate", rowTemplate), ("undoButton", undo), ("resetButton", reset),
            ("explodeButton", explode), ("flyButton", fly), ("prevButton", prev), ("nextButton", next),
            ("explodeLabel", explode.GetComponentInChildren<TMP_Text>()), ("inspectButton", inspect),
            ("reassembleButton", reassemble), ("inspectLabel", inspect.GetComponentInChildren<TMP_Text>()), ("status", status));
        PrefabUtility.SaveAsPrefabAsset(go, PanelPath);
        Object.DestroyImmediate(go);
        return AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath);
    }

    // Drawing panel

    static GameObject BuildDrawingPanel(CadMockSet mocks)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = "CadDrawingPanel";
        var panel = go.GetComponent<ForgePanel>();
        panel.panelId = "cad-drawing";
        panel.Title = "Drawing";
        panel.SizeMeters = new Vector2(0.64f, 0.54f);  // an A3 sheet (1.41 : 1) under the title bar, plus a status line
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig, sandbox)
        var content = panel.Content;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        // The sheet keeps its shape inside the frame however the panel is resized.
        var frame = NewRect("Frame", content);
        Stretch(frame, 0f, 30f, 0f, 0f);
        var sheet = new GameObject("Sheet", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
        sheet.transform.SetParent(frame, false);
        var fitter = sheet.GetComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = 2048f / 1448f;

        var status = NewText("Status", content, "", 16f, TextAlignmentOptions.MidlineLeft);
        status.color = Muted;
        var rt = status.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = new Vector2(0f, 26f);

        var component = go.AddComponent<CadDrawingPanel>();
        component.mocks = mocks;
        SetRefs(component, ("sheet", sheet.GetComponent<RawImage>()), ("status", status));
        PrefabUtility.SaveAsPrefabAsset(go, DrawingPath);
        Object.DestroyImmediate(go);
        return AssetDatabase.LoadAssetAtPath<GameObject>(DrawingPath);
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

    /// <summary>"Static margin 1.57 cal" + a STABLE chip, then the banded bar with its needle.</summary>
    static CadMetricGauge BuildGaugeTemplate(RectTransform parent)
    {
        var gauge = NewRect("GaugeTemplate", parent);
        var layout = gauge.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var line = Row("Line", gauge, 28f);
        line.GetComponent<HorizontalLayoutGroup>().spacing = 8f;
        var label = NewText("Label", line, "Metric", 19f, TextAlignmentOptions.MidlineLeft);
        Flexible(label.gameObject);
        var chip = NewImage("Chip", line, Rounded(), CadMetricGauge.Ok);
        Fixed(chip.gameObject, 130f, 28f);
        var chipText = NewText("Text", chip.transform, "OK", 16f, TextAlignmentOptions.Center);
        chipText.fontStyle = FontStyles.Bold;
        chipText.color = new Color(0.05f, 0.07f, 0.1f);
        Stretch(chipText.rectTransform, 0f, 0f, 0f, 0f);

        var bar = NewRect("Bar", gauge);
        Fixed(bar.gameObject, -1f, 16f);
        var needle = NewImage("Needle", bar, null, Color.white).rectTransform;
        needle.anchorMin = needle.anchorMax = new Vector2(0.5f, 0.5f);
        needle.sizeDelta = new Vector2(6f, 26f);

        var component = gauge.gameObject.AddComponent<CadMetricGauge>();
        SetRefs(component, ("label", label), ("chip", chip), ("chipText", chipText), ("bar", bar), ("needle", needle));
        gauge.gameObject.SetActive(false);
        return component;
    }

    /// <summary>A vertical ScrollRect taking the panel's spare height; rows go in <paramref name="rows"/>.
    /// Drag the rows' background or the scrollbar (desktop mode sends no wheel events to uGUI).</summary>
    static ScrollRect BuildScroll(RectTransform parent, out RectTransform rows)
    {
        var area = NewImage("Scroll", parent, null, new Color(1f, 1f, 1f, 0f));  // invisible, catches drags between rows
        Flexible(area.gameObject);
        area.GetComponent<LayoutElement>().minHeight = 84f;  // two rows stay visible on a panel resized small
        var scroll = area.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        var viewport = NewRect("Viewport", area.transform);
        Stretch(viewport, 0f, 0f, 16f, 0f);
        viewport.gameObject.AddComponent<RectMask2D>();
        rows = NewRect("Rows", viewport);
        rows.anchorMin = new Vector2(0f, 1f);
        rows.anchorMax = Vector2.one;
        rows.pivot = new Vector2(0.5f, 1f);
        rows.sizeDelta = Vector2.zero;
        var rowLayout = rows.gameObject.AddComponent<VerticalLayoutGroup>();
        rowLayout.spacing = 4f;
        rowLayout.childControlWidth = rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = true;
        rowLayout.childForceExpandHeight = false;
        rows.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scrollbar = DefaultControls.CreateScrollbar(UiResources()).GetComponent<Scrollbar>();
        scrollbar.name = "Scrollbar";
        var bar = (RectTransform)scrollbar.transform;
        bar.SetParent(area.transform, false);
        bar.anchorMin = new Vector2(1f, 0f);
        bar.anchorMax = Vector2.one;
        bar.pivot = new Vector2(1f, 0.5f);
        bar.sizeDelta = new Vector2(10f, 0f);
        bar.anchoredPosition = Vector2.zero;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);
        scrollbar.handleRect.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.45f);

        scroll.viewport = viewport;
        scroll.content = rows;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        return scroll;
    }

    /// <summary>The Designs menu: a list over the panel under the header, one button per design.</summary>
    static RectTransform BuildPicker(RectTransform parent, out Button entry)
    {
        var picker = NewImage("Picker", parent, Rounded(), new Color(0.17f, 0.19f, 0.25f, 1f)).rectTransform;
        picker.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        picker.anchorMin = new Vector2(0f, 1f);
        picker.anchorMax = Vector2.one;
        picker.pivot = new Vector2(0.5f, 1f);
        picker.anchoredPosition = new Vector2(0f, -46f);
        picker.sizeDelta = Vector2.zero;
        var layout = picker.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.spacing = 6f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        picker.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        entry = NewButton("DesignTemplate", picker, "Design", -1f);
        Fixed(entry.gameObject, -1f, 58f);
        entry.GetComponent<Image>().color = new Color(0.25f, 0.28f, 0.36f, 1f);
        var text = entry.GetComponentInChildren<TMP_Text>();
        text.fontStyle = FontStyles.Normal;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(text.rectTransform, 12f, 4f, 12f, 4f);
        entry.gameObject.SetActive(false);
        picker.gameObject.SetActive(false);
        return picker;
    }

    // Sandbox

    static void FillSandbox(Scene scene, GameObject viewerPrefab, GameObject panelPrefab, GameObject drawingPrefab, CadMockSet mocks)
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
        var drawing = (GameObject)PrefabUtility.InstantiatePrefab(drawingPrefab, scene);
        ((RectTransform)drawing.transform).anchoredPosition3D = new Vector3(-0.45f, 1.1f, 0.42f);
        drawing.transform.rotation = Quaternion.Euler(0f, -18f, 0f);

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
        e.flexibleHeight = 0f;  // or a flexible child (a title) makes the whole row soak up spare height
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
