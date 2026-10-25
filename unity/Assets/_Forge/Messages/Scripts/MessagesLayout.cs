using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Picks the MSG mode's layout from what the hub can do. The scene's MSG mode is the existing one: Mentions
/// (<c>messages-mentions</c>, Left) and Slack (<c>messages</c>, Main). Only when the hub's <c>/health</c> says iMessage
/// is available (IMESSAGE_ENABLED=1 on a Mac with Full Disk Access) does this add the iMessage panel
/// (<c>messages-imessage</c>, Resources/IMessagePanel.prefab) in the middle and move Slack to the right. Any other hub
/// keeps the old layout, so no empty panel ever shows. Asked again every time the hub connects; an open MSG mode is
/// reopened when the layout changes. Runtime only: the scene's serialized modes are not saved.
/// </summary>
public static class MessagesLayout
{
    const string ModeId = "messages", SlackId = "messages", IMessageId = "messages-imessage";

    /// <summary>True when the hub reported iMessage available and MSG shows the iMessage panel in the middle.</summary>
    public static bool IMessage { get; private set; }

    static WorkspaceItem iMessageItem;   // built once from Resources/IMessagePanel.prefab
    static WorkspaceSlot slackHome;      // where the scene put Slack (Main)
    static bool slackHomeKnown, hooked;

    class Health
    {
        [JsonProperty("imessage")] public IMessageHealth imessage;
    }

    class IMessageHealth
    {
        [JsonProperty("available")] public bool available;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        if (hooked) ForgeNet.StatusChanged -= OnStatus;
        hooked = slackHomeKnown = false;
        IMessage = false;
        iMessageItem = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (Workspace.Current == null) return;
        ForgeNet.StatusChanged += OnStatus;
        hooked = true;
        if (ForgeNet.Status == ForgeNetStatus.Live) Ask();
    }

    static void OnStatus(ForgeNetStatus status)
    {
        if (status == ForgeNetStatus.Live) Ask();
    }

    static void Ask() => ForgeNet.Get<Health>("/health",
        h => Apply(h != null && h.imessage != null && h.imessage.available),
        err => Debug.LogWarning($"[MessagesLayout] /health failed ({err}); MSG stays Slack-only."));

    static void Apply(bool imessage)
    {
        var ws = Workspace.Current;
        var mode = ws != null ? ws.modes.Find(m => m.id == ModeId) : null;
        if (mode == null || imessage == IMessage) return;
        var slack = mode.items.Find(i => i.id == SlackId);
        if (slack != null && !slackHomeKnown)
        {
            slackHome = slack.slot;
            slackHomeKnown = true;
        }

        if (imessage)
        {
            if (iMessageItem == null)
            {
                var prefab = Resources.Load<GameObject>("IMessagePanel");
                if (prefab == null)
                {
                    Debug.LogWarning("[MessagesLayout] Resources/IMessagePanel.prefab missing (Forge > Messages > Build Prefabs); MSG stays Slack-only.");
                    return;
                }
                iMessageItem = new WorkspaceItem(IMessageId, "iMessage", WorkspaceSlot.Main) { prefab = prefab };
            }
            if (!mode.items.Contains(iMessageItem)) mode.items.Add(iMessageItem);
            if (slack != null) slack.slot = WorkspaceSlot.Right;
        }
        else
        {
            if (iMessageItem != null) mode.items.Remove(iMessageItem);
            if (slack != null && slackHomeKnown) slack.slot = slackHome;
        }
        IMessage = imessage;
        if (ws.ActiveMode == ModeId) ws.Open(ModeId);   // re-lay out what is on screen
    }
}
