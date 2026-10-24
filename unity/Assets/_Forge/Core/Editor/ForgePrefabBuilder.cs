using Oculus.Interaction.Editor.QuickActions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Forge > Build Core Prefabs: (re)builds ForgePanel, DeskDock and ForgeVolume in Assets/_Forge/Core/Prefabs.
/// Canvases get Interaction SDK ray + poke through the SDK's Quick Actions, so they work with hands and controllers.
/// Overwrites the prefabs (GUIDs are kept), so tweak them through this script rather than by hand.
/// </summary>
public static class ForgePrefabBuilder
{
    public const string Dir = "Assets/_Forge/Core/Prefabs";
    public const string PanelPath = Dir + "/ForgePanel.prefab";
    public const string DockPath = Dir + "/DeskDock.prefab";
    public const string VolumePath = Dir + "/ForgeVolume.prefab";
    const string MaterialDir = "Assets/_Forge/Core/Materials";

    static readonly Color PanelBackground = new Color(0.07f, 0.09f, 0.13f, 0.85f);
    static readonly Color TitleBar = new Color(0.16f, 0.45f, 0.85f, 0.95f);
    static readonly Color Accent = new Color(0.25f, 0.65f, 1f);

    [MenuItem("Forge/Build Core Prefabs")]
    public static void BuildMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string previous = SceneManager.GetActiveScene().path;
        BuildAll();
        if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous);
    }

    public static bool Exist() =>
        AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath) != null &&
        AssetDatabase.LoadAssetAtPath<GameObject>(DockPath) != null &&
        AssetDatabase.LoadAssetAtPath<GameObject>(VolumePath) != null;

    /// <summary>Builds in a throwaway scene (the SDK wizards work on scene objects). Replaces the open scene.</summary>
    public static void BuildAll()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EnsureFolder(Dir);
        EnsureFolder(MaterialDir);
        BuildPanel();
        BuildDock();
        BuildVolume();
        AssetDatabase.SaveAssets();
        Debug.Log("[ForgePrefabBuilder] Built ForgePanel, DeskDock and ForgeVolume in " + Dir);
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

    static void BuildDock()
    {
        var root = NewCanvas("DeskDock", new Vector2(400f, 110f));
        var group = root.AddComponent<CanvasGroup>();
        Stretch(NewImage("Plate", root.transform, Rounded(), new Color(0.05f, 0.07f, 0.1f, 0.55f)).rectTransform, 0f, 0f, 0f, 0f);

        var row = (RectTransform)new GameObject("Row", typeof(RectTransform)).transform;
        row.SetParent(root.transform, false);
        Stretch(row, 0f, 0f, 0f, 0f);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 22f;
        layout.padding = new RectOffset(18, 18, 19, 19);
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = layout.childControlHeight = false;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;

        var knob = NewImage("ButtonTemplate", row, Knob(), Accent);
        knob.type = Image.Type.Simple;
        knob.rectTransform.sizeDelta = new Vector2(72f, 72f);
        var button = knob.gameObject.AddComponent<Button>();
        button.targetGraphic = knob;
        var colors = button.colors;
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        colors.colorMultiplier = 1.3f;
        button.colors = colors;
        var label = NewText("Label", knob.rectTransform, "ML", 22f, TextAlignmentOptions.Center);
        label.fontStyle = FontStyles.Bold;
        Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
        knob.gameObject.SetActive(false);

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
