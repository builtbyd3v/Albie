using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Forge > Messages > Build Prefabs: (re)builds, in Assets/_Forge/Messages/Resources (Resources so DigestTrigger and
/// DraftPresenter can load them at runtime without scene references), with no manual steps:
/// - MessageCard.prefab: one Slack message on the module's slate card (MessageCard; pooled by the panels)
/// - SlackPanel.prefab: ForgePanel variant, panel id "messages", slot Main; SlackPanel builds its UI at runtime
/// - MentionsPanel.prefab: ForgePanel variant, panel id "messages-mentions", slot Left ("New for you"; also the floating digest)
/// - DraftCard.prefab: ForgePanel variant, panel id "messages-draft", the Send / Cancel card while the Slack panel is closed
/// GUIDs are kept on rebuild (SaveAsPrefabAsset over the same path). Same technique as CAD/Editor/CadPrefabBuilder.cs;
/// no scene is touched. The old MessagesPanel.prefab is deleted if it is still around.
/// </summary>
public static class MessagesPrefabBuilder
{
    const string Root = "Assets/_Forge/Messages";
    const string ResDir = Root + "/Resources";
    public const string CardPath = ResDir + "/MessageCard.prefab";
    public const string SlackPanelPath = ResDir + "/SlackPanel.prefab";
    public const string MentionsPanelPath = ResDir + "/MentionsPanel.prefab";
    public const string DraftPath = ResDir + "/DraftCard.prefab";
    const string LegacyPanelPath = ResDir + "/MessagesPanel.prefab";

    /// <summary>Panel sizes (m). The Workspace keeps a prefab's own size, so these are what Main shows.</summary>
    public static readonly Vector2 SlackPanelSize = new Vector2(0.88f, 0.56f);
    public static readonly Vector2 MentionsPanelSize = new Vector2(0.36f, 0.44f);

    [MenuItem("Forge/Messages/Build Prefabs")]
    public static void BuildAll()
    {
        EnsureFolder(ResDir);
        if (!ForgePrefabBuilder.Exist()) ForgePrefabBuilder.BuildAll();
        ForgeLookBuilder.Build();  // Inter + the PanelSurface material the cards draw with (idempotent)
        BuildCard();
        BuildPanel(SlackPanelPath, "SlackPanel", "messages", "Slack", SlackPanelSize, go => go.AddComponent<SlackPanel>());
        BuildPanel(MentionsPanelPath, "MentionsPanel", "messages-mentions", "Mentions", MentionsPanelSize, go => go.AddComponent<MentionsPanel>());
        BuildDraftCard();
        if (AssetDatabase.LoadAssetAtPath<GameObject>(LegacyPanelPath) != null) AssetDatabase.DeleteAsset(LegacyPanelPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[MessagesPrefabBuilder] Built {CardPath}, {SlackPanelPath}, {MentionsPanelPath}, {DraftPath}");
    }

    // One message on a slate card: shadow + fill, accent strip, initials bubble, name, time, text, replies line. The
    // owner (SlackPanel / MentionsPanel) sizes the card and calls MessageCard.Layout, so nothing is anchored here.
    static void BuildCard()
    {
        var root = new GameObject("MessageCard", typeof(RectTransform));
        var rect = (RectTransform)root.transform;
        rect.sizeDelta = new Vector2(360f, MessageCard.InboxHeight);

        var frame = SlackUi.Card(rect, SlackUi.CardCorner, raycast: true);
        var shadow = rect.Find("Shadow").GetComponent<ForgeSurface>();

        var accent = SlackUi.PillSurface(rect, "Accent", SlackUi.Accent);
        SlackUi.Place(accent.rectTransform, 2f, 10f, 3f, MessageCard.InboxHeight - 20f);
        accent.gameObject.SetActive(false);

        var bubble = SlackUi.PillSurface(rect, "Bubble", SlackUi.BubbleColors[0]);
        SlackUi.Place(bubble.rectTransform, 10f, 10f, 32f, 32f);
        var initial = SlackUi.Text(bubble.transform, "Initial", 12f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        initial.text = "HM";
        SlackUi.Stretch(initial.rectTransform, 0f, 0f, 0f, 0f);

        var name = SlackUi.Text(rect, "Name", SlackUi.Name, SlackUi.TextColor, bold: true);
        name.overflowMode = TextOverflowModes.Ellipsis;
        name.text = "Hudson  <alpha=#80><size=85%>#forge-team";
        SlackUi.Place(name.rectTransform, 52f, 8f, 220f, 16f);

        var time = SlackUi.Text(rect, "Time", SlackUi.Time, SlackUi.Subtext, align: TextAlignmentOptions.Right);
        time.text = "2m";
        SlackUi.PlaceRight(time.rectTransform, 10f, 9f, 74f, 16f);

        var body = SlackUi.Text(rect, "Body", SlackUi.Body, SlackUi.TextColor, wrap: true);
        body.overflowMode = TextOverflowModes.Ellipsis;
        body.text = "is the demo build ready by 3?";
        SlackUi.Place(body.rectTransform, 52f, 26f, 298f, 38f);

        var meta = SlackUi.Text(rect, "Meta", SlackUi.Overline, SlackUi.Accent, bold: true);
        meta.text = "2 replies";
        SlackUi.Place(meta.rectTransform, 52f, 60f, 200f, 15f);
        meta.gameObject.SetActive(false);

        var card = root.AddComponent<MessageCard>();
        SetRefs(card, ("shadow", shadow), ("frame", frame), ("accent", accent), ("bubble", bubble), ("initial", initial),
            ("nameText", name), ("timeText", time), ("body", body), ("meta", meta));
        PrefabUtility.SaveAsPrefabAsset(root, CardPath);
        Object.DestroyImmediate(root);
    }

    // A ForgePanel variant whose module component builds its UI at runtime under Content (like CadParamsPanel).
    static void BuildPanel(string path, string name, string panelId, string title, Vector2 size, System.Action<GameObject> addComponent)
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
        addComponent(go);
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }

    // The Send / Cancel card: a small ForgePanel with a slate sheet, opaque Inter text and two pills.
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

        var sheetRect = SlackUi.Rect(content, "Sheet");
        SlackUi.Stretch(sheetRect, 0f, 0f, 0f, 0f);
        var sheet = SlackUi.Card(sheetRect, SlackUi.CardCorner, raycast: false);

        var header = SlackUi.Text(content, "Header", SlackUi.Name, SlackUi.TextColor, bold: true);
        header.overflowMode = TextOverflowModes.Ellipsis;
        header.text = "#forge-team";
        SlackUi.Band(header.rectTransform, 10f, 18f, 14f, 14f);

        var replying = SlackUi.Text(content, "Replying", SlackUi.Small, SlackUi.Subtext);
        replying.overflowMode = TextOverflowModes.Ellipsis;
        replying.text = "Replying to Hudson: is the demo build ready by 3?";
        SlackUi.Band(replying.rectTransform, 30f, 16f, 14f, 14f);

        var body = SlackUi.Text(content, "Body", SlackUi.Body, SlackUi.TextColor, wrap: true);
        body.overflowMode = TextOverflowModes.Ellipsis;
        body.text = "Yes, I'll ping you when it's pushed.";
        SlackUi.Stretch(body.rectTransform, 14f, 50f, 14f, 50f);

        var status = SlackUi.Text(content, "Status", SlackUi.Small, SlackUi.Subtext);
        status.overflowMode = TextOverflowModes.Ellipsis;
        BottomLeft(status.rectTransform, 14f, 12f, 150f, 26f);

        var cancel = SlackButton.CreatePill(content, "Cancel", SlackButton.Pill, null);
        var send = SlackButton.CreatePill(content, "Send", SlackButton.Primary, null);
        BottomRight(send.Rect, 14f, 10f, send.Rect.sizeDelta.x, ForgeTheme.PillHeight);
        BottomRight(cancel.Rect, 14f + send.Rect.sizeDelta.x + 8f, 10f, cancel.Rect.sizeDelta.x, ForgeTheme.PillHeight);

        var card = go.AddComponent<DraftCard>();
        SetRefs(card, ("sheet", sheet), ("header", header), ("replying", replying), ("body", body), ("status", status),
            ("send", send.Button), ("cancel", cancel.Button));
        PrefabUtility.SaveAsPrefabAsset(go, DraftPath);
        Object.DestroyImmediate(go);
    }

    // ---- helpers ----

    static void BottomRight(RectTransform rt, float right, float bottom, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(-right, bottom);
    }

    static void BottomLeft(RectTransform rt, float left, float bottom, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(left, bottom);
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

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
