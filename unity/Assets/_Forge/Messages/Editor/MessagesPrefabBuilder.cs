using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Forge > Messages > Build Prefabs: (re)builds, in Assets/_Forge/Messages/Resources (Resources so that
/// DigestTrigger can load the panel at runtime for the floating digest copy):
/// - MessageCard.prefab: one Slack message (MessageCard)
/// - MessagesPanel.prefab: ForgePanel variant, panel id "messages", UI under Content (MessagesPanel)
/// - DraftCard.prefab: ForgePanel variant, panel id "messages-draft", the Send / Cancel card (DraftCard)
/// GUIDs are kept on rebuild. Same technique as CAD/Editor/CadPrefabBuilder.cs; no scene is touched.
/// </summary>
public static class MessagesPrefabBuilder
{
    const string Root = "Assets/_Forge/Messages";
    const string ResDir = Root + "/Resources";
    public const string CardPath = ResDir + "/MessageCard.prefab";
    public const string PanelPath = ResDir + "/MessagesPanel.prefab";
    public const string DraftPath = ResDir + "/DraftCard.prefab";
    static readonly Color SendAccent = new Color(0.85f, 0.4f, 0.9f);  // the MSG mode color (Workspace.DefaultModes)
    static readonly Color CancelBack = new Color(1f, 1f, 1f, 0.14f);

    static readonly Color CardBackground = new Color(0.12f, 0.15f, 0.21f, MessageCard.RestAlpha);
    static readonly Color Muted = new Color(1f, 1f, 1f, 0.6f);
    static readonly Color Ink = new Color(0.05f, 0.07f, 0.1f);

    [MenuItem("Forge/Messages/Build Prefabs")]
    public static void BuildAll()
    {
        EnsureFolder(ResDir);
        if (!ForgePrefabBuilder.Exist()) ForgePrefabBuilder.BuildAll();
        var card = BuildCard();
        BuildPanel(card);
        BuildDraftCard();
        AssetDatabase.SaveAssets();
        Debug.Log($"[MessagesPrefabBuilder] Built {CardPath}, {PanelPath}, {DraftPath}");
    }

    // One message. Sizes in canvas units (1 unit = 1 mm on a ForgePanel).
    static GameObject BuildCard()
    {
        var frame = NewImage("MessageCard", null, Rounded(), CardBackground);
        var root = frame.gameObject;
        var layout = root.AddComponent<LayoutElement>();
        layout.preferredHeight = MessageCard.CollapsedHeightMm;
        layout.flexibleWidth = 1f;

        var accent = NewImage("Accent", root.transform, Rounded(), new Color(1f, 0.6f, 0.2f));
        var accentRt = accent.rectTransform;
        accentRt.anchorMin = new Vector2(0f, 0.12f);
        accentRt.anchorMax = new Vector2(0f, 0.88f);
        accentRt.pivot = new Vector2(0f, 0.5f);
        accentRt.sizeDelta = new Vector2(5f, 0f);
        accentRt.anchoredPosition = new Vector2(6f, 0f);
        accent.raycastTarget = false;
        accent.enabled = false;

        var bubble = NewImage("Bubble", root.transform, Knob(), new Color(0.25f, 0.55f, 1f));
        bubble.type = Image.Type.Simple;
        bubble.preserveAspect = true;
        bubble.raycastTarget = false;
        var bubbleRt = bubble.rectTransform;
        bubbleRt.anchorMin = bubbleRt.anchorMax = new Vector2(0f, 1f);
        bubbleRt.pivot = new Vector2(0.5f, 0.5f);
        bubbleRt.sizeDelta = new Vector2(48f, 48f);
        bubbleRt.anchoredPosition = new Vector2(42f, -38f);
        var initial = NewText("Initial", bubble.transform, "H", 24f, TextAlignmentOptions.Center);
        initial.fontStyle = FontStyles.Bold;
        initial.color = Ink;
        Stretch(initial.rectTransform, 0f, 0f, 0f, 0f);

        var header = NewText("Header", root.transform, "Hudson  #forge-team", 19f, TextAlignmentOptions.MidlineLeft);
        header.fontStyle = FontStyles.Bold;
        header.overflowMode = TextOverflowModes.Ellipsis;
        var headerRt = header.rectTransform;
        headerRt.anchorMin = new Vector2(0f, 1f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0f, 1f);
        headerRt.offsetMin = new Vector2(76f, -38f);
        headerRt.offsetMax = new Vector2(-70f, -10f);

        var time = NewText("Time", root.transform, "2m", 15f, TextAlignmentOptions.MidlineRight);
        time.color = Muted;
        var timeRt = time.rectTransform;
        timeRt.anchorMin = timeRt.anchorMax = new Vector2(1f, 1f);
        timeRt.pivot = new Vector2(1f, 1f);
        timeRt.sizeDelta = new Vector2(60f, 28f);
        timeRt.anchoredPosition = new Vector2(-12f, -10f);

        var body = NewText("Body", root.transform, "is the demo build ready by 3?", 17f, TextAlignmentOptions.TopLeft);
        body.color = new Color(1f, 1f, 1f, 0.92f);
        body.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(body.rectTransform, 76f, 10f, 12f, 42f);

        var card = root.AddComponent<MessageCard>();
        SetRefs(card, ("frame", frame), ("accent", accent), ("bubble", bubble), ("initial", initial),
            ("header", header), ("time", time), ("body", body), ("layout", layout));
        PrefabUtility.SaveAsPrefabAsset(root, CardPath);
        Object.DestroyImmediate(root);
        return AssetDatabase.LoadAssetAtPath<GameObject>(CardPath);
    }

    static void BuildPanel(GameObject cardPrefab)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = "MessagesPanel";
        var panel = go.GetComponent<ForgePanel>();
        panel.panelId = "messages";
        panel.Title = "Messages";
        panel.SizeMeters = new Vector2(0.42f, 0.52f);
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
        var content = panel.Content;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        // Cards, newest at the top. Heights come from each card's LayoutElement (hover expands it).
        var list = NewRect("List", content);
        Stretch(list, 0f, 36f, 0f, 0f);
        var column = list.gameObject.AddComponent<VerticalLayoutGroup>();
        column.spacing = 8f;
        column.childAlignment = TextAnchor.UpperCenter;
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;

        var template = (GameObject)PrefabUtility.InstantiatePrefab(cardPrefab, list);
        template.name = "CardTemplate";
        template.SetActive(false);

        var empty = NewText("Empty", content, "No new messages", 20f, TextAlignmentOptions.Center);
        empty.color = new Color(1f, 1f, 1f, 0.35f);
        Stretch(empty.rectTransform, 0f, 36f, 0f, 0f);

        var footer = NewText("Footer", content, "", 16f, TextAlignmentOptions.Center);
        footer.color = Muted;
        var footerRt = footer.rectTransform;
        footerRt.anchorMin = new Vector2(0f, 0f);
        footerRt.anchorMax = new Vector2(1f, 0f);
        footerRt.pivot = new Vector2(0.5f, 0f);
        footerRt.sizeDelta = new Vector2(0f, 30f);
        footerRt.anchoredPosition = Vector2.zero;

        var component = go.AddComponent<MessagesPanel>();
        SetRefs(component, ("list", list), ("cardTemplate", template.GetComponent<MessageCard>()), ("footer", footer), ("empty", empty));
        PrefabUtility.SaveAsPrefabAsset(go, PanelPath);
        Object.DestroyImmediate(go);
    }

    // The Send / Cancel card: a small ForgePanel with a translucent sheet, opaque text and two buttons.
    static void BuildDraftCard()
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath));
        go.name = "DraftCard";
        var panel = go.GetComponent<ForgePanel>();
        panel.panelId = "messages-draft";
        panel.Title = "Send this?";
        panel.SizeMeters = DraftCard.SizeMeters;
        if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
        var content = panel.Content;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        var sheet = NewImage("Sheet", content, Rounded(), CardBackground);
        sheet.raycastTarget = false;
        Stretch(sheet.rectTransform, 0f, 0f, 0f, 0f);

        var header = NewText("Header", content, "#forge-team", 19f, TextAlignmentOptions.MidlineLeft);
        header.fontStyle = FontStyles.Bold;
        header.overflowMode = TextOverflowModes.Ellipsis;
        var headerRt = header.rectTransform;
        headerRt.anchorMin = new Vector2(0f, 1f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0f, 1f);
        headerRt.offsetMin = new Vector2(14f, -40f);
        headerRt.offsetMax = new Vector2(-14f, -10f);

        var replying = NewText("Replying", content, "Replying to Hudson: is the demo build ready by 3?", 14f, TextAlignmentOptions.MidlineLeft);
        replying.color = Muted;
        replying.overflowMode = TextOverflowModes.Ellipsis;
        var replyingRt = replying.rectTransform;
        replyingRt.anchorMin = new Vector2(0f, 1f);
        replyingRt.anchorMax = new Vector2(1f, 1f);
        replyingRt.pivot = new Vector2(0f, 1f);
        replyingRt.offsetMin = new Vector2(14f, -64f);
        replyingRt.offsetMax = new Vector2(-14f, -42f);

        var body = NewText("Body", content, "Yes, I'll ping you when it's pushed.", 18f, TextAlignmentOptions.TopLeft);
        body.textWrappingMode = TextWrappingModes.Normal;
        body.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(body.rectTransform, 14f, 70f, 14f, 68f);

        var status = NewText("Status", content, "", 15f, TextAlignmentOptions.MidlineLeft);
        status.color = Muted;
        var statusRt = status.rectTransform;
        statusRt.anchorMin = new Vector2(0f, 0f);
        statusRt.anchorMax = new Vector2(0.5f, 0f);
        statusRt.pivot = new Vector2(0f, 0f);
        statusRt.offsetMin = new Vector2(14f, 14f);
        statusRt.offsetMax = new Vector2(0f, 58f);

        var cancel = NewButton("Cancel", content, "Cancel", CancelBack, Color.white, new Vector2(110f, 44f), new Vector2(-134f, 14f));
        var send = NewButton("Send", content, "Send", SendAccent, Ink, new Vector2(110f, 44f), new Vector2(-14f, 14f));

        var card = go.AddComponent<DraftCard>();
        SetRefs(card, ("sheet", sheet), ("header", header), ("replying", replying), ("body", body), ("status", status),
            ("send", send), ("cancel", cancel));
        PrefabUtility.SaveAsPrefabAsset(go, DraftPath);
        Object.DestroyImmediate(go);
    }

    // ---- helpers (same shapes as CadPrefabBuilder / ForgePrefabBuilder) ----

    // A uGUI Button anchored to the content's bottom-right corner (pivot bottom-right, offsets in mm).
    static Button NewButton(string name, Transform parent, string label, Color back, Color ink, Vector2 size, Vector2 anchoredPos)
    {
        var image = NewImage(name, parent, Rounded(), back);
        image.raycastTarget = true;
        var rt = image.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var text = NewText("Label", image.transform, label, 18f, TextAlignmentOptions.Center);
        text.fontStyle = FontStyles.Bold;
        text.color = ink;
        Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        return button;
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
        if (parent != null) go.transform.SetParent(parent, false);
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
            if (property == null) { Debug.LogError($"[MessagesPrefabBuilder] {target.GetType().Name} has no field {field}"); continue; }
            property.objectReferenceValue = value;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static Sprite Rounded() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
    static Sprite Knob() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
