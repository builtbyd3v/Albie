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
        StyleDesktopRig(root);
        Save(root, DesktopRigPath);
    }

    /// <summary>
    /// Restyle DesktopRig.prefab's HUD in place (Main.unity and PetMain.unity hold instances of it, so its objects keep
    /// their IDs).
    /// </summary>
    [MenuItem("Forge/Restyle Desktop HUD")]
    public static void RestyleDesktopRig()
    {
        ForgeLookBuilder.Build();
        var root = PrefabUtility.LoadPrefabContents(DesktopRigPath);
        try
        {
            StyleDesktopRig(root);
            PrefabUtility.SaveAsPrefabAsset(root, DesktopRigPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("[ForgePrefabBuilder] Restyled " + DesktopRigPath);
    }

    // The hint line becomes a Horizon-style toast (a dark translucent pill in the top left, sized to its text);
    // the crosshair a white dot on a dark halo, so it shows on light and dark alike.
    static void StyleDesktopRig(GameObject root)
    {
        var hud = (RectTransform)root.transform.Find("HUD");

        var crosshair = (RectTransform)hud.Find("Crosshair");
        var oldImage = crosshair.GetComponent<Image>();
        if (oldImage != null) Object.DestroyImmediate(oldImage);
        var halo = crosshair.GetComponent<ForgeSurface>();
        if (halo == null) halo = crosshair.gameObject.AddComponent<ForgeSurface>();
        halo.Pill = true;
        halo.color = new Color(0f, 0f, 0f, 0.35f);
        halo.raycastTarget = false;
        crosshair.sizeDelta = new Vector2(16f, 16f);
        var dotTransform = crosshair.Find("Dot");
        var dot = dotTransform != null ? dotTransform.GetComponent<ForgeSurface>() : null;
        if (dot == null)
        {
            var dotRect = (RectTransform)new GameObject("Dot", typeof(RectTransform)).transform;
            dotRect.SetParent(crosshair, false);
            dot = dotRect.gameObject.AddComponent<ForgeSurface>();
        }
        dot.Pill = true;
        dot.Glow = 0.4f;
        dot.color = Color.white;
        dot.raycastTarget = false;
        ((RectTransform)dot.transform).sizeDelta = new Vector2(8f, 8f);

        var hint = (hud.Find("Hint") ?? hud.Find("HintPill/Hint")).GetComponent<TMP_Text>();
        var pillTransform = hud.Find("HintPill");
        RectTransform pill;
        if (pillTransform == null)
        {
            pill = (RectTransform)new GameObject("HintPill", typeof(RectTransform)).transform;
            pill.SetParent(hud, false);
            pill.gameObject.AddComponent<ForgeSurface>();
            var layout = pill.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var fit = pill.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            hint.transform.SetParent(pill, false);
        }
        else pill = (RectTransform)pillTransform;
        pill.anchorMin = pill.anchorMax = pill.pivot = new Vector2(0f, 1f);  // top left, clear of the dock
        pill.anchoredPosition = new Vector2(28f, -28f);
        var surface = pill.GetComponent<ForgeSurface>();
        surface.Pill = true;
        surface.color = new Color(0.07f, 0.08f, 0.1f, 0.78f);
        surface.raycastTarget = false;
        pill.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(26, 26, 12, 12);
        StyleText(hint, 21f, new Color(1f, 1f, 1f, 0.88f), FontStyles.Normal, TextAlignmentOptions.Center);
        hint.overflowMode = TextOverflowModes.Overflow;
        hint.characterSpacing = 1f;
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
        Stretch(NewImage("Background", root.transform, null, Color.white).rectTransform, 0f, 0f, 0f, 0f);

        var bar = NewImage("TitleBar", root.transform, null, Color.clear).rectTransform;
        bar.anchorMin = new Vector2(0f, 1f);
        bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0.5f, 1f);
        bar.anchoredPosition = Vector2.zero;
        var title = NewText("Title", bar, "Panel", 16f, TextAlignmentOptions.Left);

        var content = (RectTransform)new GameObject("Content", typeof(RectTransform)).transform;
        content.SetParent(root.transform, false);
        var placeholder = NewText("Placeholder", content, "Module UI goes here", 16f, TextAlignmentOptions.Center);
        Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);

        var panel = root.AddComponent<ForgePanel>();
        SetRefs(panel, ("titleText", title), ("content", content));
        StylePanel(root);
        AddCanvasInteraction(root);
        Save(root, PanelPath);
    }

    /// <summary>
    /// Restyle ForgePanel.prefab in place (graphite frame, header, badge). Edits the saved prefab's own objects, so the
    /// module variants built on it (CadParamsPanel, ScreenForgePanel) stay linked and pick the look up.
    /// </summary>
    [MenuItem("Forge/Restyle Panel Prefab (keeps module variants)")]
    public static void RestylePanel()
    {
        ForgeLookBuilder.Build();
        var root = PrefabUtility.LoadPrefabContents(PanelPath);
        try
        {
            StylePanel(root);
            PrefabUtility.SaveAsPrefabAsset(root, PanelPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("[ForgePrefabBuilder] Restyled " + PanelPath);
    }

    // The graphite look on a ForgePanel hierarchy (fresh, or the saved prefab's contents). Finds its objects by name
    // and adds what's missing: Background becomes a ForgeSurface, the header is an invisible 64 mm move handle with the
    // badge, title and subtitle, and Content sits inside the new insets.
    static void StylePanel(GameObject root)
    {
        var rt = (RectTransform)root.transform;
        var panel = root.GetComponent<ForgePanel>();

        var background = rt.Find("Background");
        var oldImage = background.GetComponent<Image>();
        if (oldImage != null) Object.DestroyImmediate(oldImage);
        var surface = background.GetComponent<ForgeSurface>();
        if (surface == null) surface = background.gameObject.AddComponent<ForgeSurface>();
        surface.Style = ForgeSurface.Kind.Panel;
        surface.Corner = ForgeTheme.PanelCorner;
        surface.Header = ForgeTheme.PanelHeader;
        surface.Shadow = 1f;
        surface.color = Color.white;
        surface.raycastTarget = true;

        var bar = (RectTransform)rt.Find("TitleBar");
        var barImage = bar.GetComponent<Image>();
        barImage.sprite = null;
        barImage.type = Image.Type.Simple;
        barImage.color = Color.clear;
        bar.sizeDelta = new Vector2(0f, ForgeTheme.PanelHeader);

        var title = bar.Find("Title").GetComponent<TMP_Text>();
        StyleText(title, 17f, ForgeTheme.PanelText, FontStyles.Bold, TextAlignmentOptions.Left);
        PlaceInHeader(title.rectTransform, 20f, ForgeTheme.PanelHeader * 0.5f, 22f);

        var subtitleTransform = bar.Find("Subtitle");
        var subtitle = subtitleTransform != null ? subtitleTransform.GetComponent<TMP_Text>() : NewText("Subtitle", bar, "", 11.5f, TextAlignmentOptions.Left);
        StyleText(subtitle, 11.5f, ForgeTheme.PanelSubtext, FontStyles.Normal, TextAlignmentOptions.Left);
        PlaceInHeader(subtitle.rectTransform, 20f, 44f, 16f);
        subtitle.gameObject.SetActive(false);

        var badgeTransform = bar.Find("Badge");
        ForgeSurface badge;
        TMP_Text badgeLabel;
        if (badgeTransform == null)
        {
            var badgeRect = (RectTransform)new GameObject("Badge", typeof(RectTransform)).transform;
            badgeRect.SetParent(bar, false);
            badge = badgeRect.gameObject.AddComponent<ForgeSurface>();
            badgeLabel = NewText("Label", badgeRect, "", 11.5f, TextAlignmentOptions.Center);
            Stretch(badgeLabel.rectTransform, 0f, 0f, 0f, 0f);
        }
        else
        {
            badge = badgeTransform.GetComponent<ForgeSurface>();
            badgeLabel = badgeTransform.Find("Label").GetComponent<TMP_Text>();
        }
        var badgeRt = (RectTransform)badge.transform;
        badgeRt.anchorMin = badgeRt.anchorMax = new Vector2(0f, 1f);
        badgeRt.pivot = new Vector2(0.5f, 0.5f);
        badgeRt.sizeDelta = new Vector2(40f, 40f);
        badgeRt.anchoredPosition = new Vector2(20f + 20f, -ForgeTheme.PanelHeader * 0.5f);
        badge.Style = ForgeSurface.Kind.Fill;
        badge.Corner = 12f;
        badge.Outline = 0.4f;
        badge.color = ForgeTheme.Tint(Color.white, 0.2f);
        badge.raycastTarget = false;
        StyleText(badgeLabel, 11.5f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
        badgeLabel.characterSpacing = 3f;
        badge.gameObject.SetActive(false);

        var content = (RectTransform)rt.Find("Content");
        Stretch(content, ForgeTheme.PanelInset, 16f, ForgeTheme.PanelInset, ForgeTheme.PanelHeader + 14f);
        var placeholder = content.Find("Placeholder");
        if (placeholder != null)
            StyleText(placeholder.GetComponent<TMP_Text>(), 16f, new Color(1f, 1f, 1f, 0.35f), FontStyles.Normal, TextAlignmentOptions.Center);

        SetRefs(panel, ("subtitleText", subtitle), ("badge", badge), ("badgeText", badgeLabel));
    }

    static void StyleText(TMP_Text text, float size, Color color, FontStyles style, TextAlignmentOptions align)
    {
        if (ForgeTheme.Font != null)
        {
            text.font = ForgeTheme.Font;
            text.fontSharedMaterial = ForgeTheme.Font.material;
        }
        text.fontSize = size;
        text.color = color;
        text.fontStyle = style;
        text.alignment = align;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
    }

    // Same placement ForgePanel.LayoutHeader does at runtime: a line centered this far below the top of the header.
    static void PlaceInHeader(RectTransform rt, float left, float centerFromTop, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(-(left + 180f), height);
        rt.anchoredPosition = new Vector2((left - 180f) * 0.5f, -centerFromTop);
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

        var ring = new GameObject("Base", typeof(MeshFilter), typeof(MeshRenderer));
        ring.transform.SetParent(root.transform, false);

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
        StyleVolume(root);
        Save(root, VolumePath);
    }

    /// <summary>
    /// Restyle ForgeVolume.prefab in place (the base as a ring of light, Inter label). Keeps the prefab's objects, so
    /// CadViewer and other variants stay linked and pick the look up.
    /// </summary>
    [MenuItem("Forge/Restyle Volume Prefab (keeps module variants)")]
    public static void RestyleVolume()
    {
        ForgeLookBuilder.Build();
        var root = PrefabUtility.LoadPrefabContents(VolumePath);
        try
        {
            StyleVolume(root);
            PrefabUtility.SaveAsPrefabAsset(root, VolumePath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("[ForgePrefabBuilder] Restyled " + VolumePath);
    }

    // "Base" (found by name) becomes a flat quad drawing the ring of light, "BasePool" beside it the pool and contrast
    // wash under it; ForgeVolume sizes and tints both. The label gets Inter with a soft glow.
    static void StyleVolume(GameObject root)
    {
        var volume = root.GetComponent<ForgeVolume>();
        var quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");

        var ring = root.transform.Find("Base");
        ring.GetComponent<MeshFilter>().sharedMesh = quad;
        LightRenderer(ring.GetComponent<MeshRenderer>(), ForgeTheme.RingLight);
        ring.localPosition = new Vector3(0f, 0.004f, 0f);

        var pool = root.transform.Find("BasePool");
        if (pool == null)
        {
            pool = new GameObject("BasePool", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            pool.SetParent(root.transform, false);
            pool.SetSiblingIndex(ring.GetSiblingIndex());
        }
        pool.GetComponent<MeshFilter>().sharedMesh = quad;
        LightRenderer(pool.GetComponent<MeshRenderer>(), ForgeTheme.PoolLight);
        pool.localPosition = new Vector3(0f, 0.003f, 0f);

        var label = root.transform.Find("Label/Text").GetComponent<TMP_Text>();
        StyleText(label, 15f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
        if (ForgeTheme.GlowText != null) label.fontSharedMaterial = ForgeTheme.GlowText;
        label.overflowMode = TextOverflowModes.Overflow;
        label.characterSpacing = 2f;

        SetRefs(volume, ("basePool", pool.GetComponent<MeshRenderer>()));
        volume.SetSize(volume.size);  // applies base + label placement
    }

    static void LightRenderer(MeshRenderer renderer, Material material)
    {
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
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

    static Sprite Knob() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

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
