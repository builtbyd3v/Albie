using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Picks the MSG mode's layout from what the hub can do. The scene lists three items: Mentions (Left), iMessage
/// (<c>messages</c>, Main) and Slack (<c>messages-slack</c>, Right). Until the hub's <c>/health</c> says iMessage is
/// available (a Mac signed into Messages), and on any hub without it (Windows), MSG is Slack in the middle with
/// notifications on the left and no iMessage panel. Asked again every time the hub connects; an open MSG mode is
/// reopened when the layout changes. Runtime only: the scene's serialized modes are not touched.
/// </summary>
public static class MessagesLayout
{
    const string ModeId = "messages", IMessageId = "messages", SlackId = "messages-slack";

    /// <summary>True when the hub reported iMessage available and MSG shows the iMessage panel in the middle.</summary>
    public static bool IMessage { get; private set; }

    static WorkspaceItem iMessageItem;   // the scene's iMessage item, held while it is out of the mode
    static WorkspaceSlot slackSlot;      // where the scene put Slack beside iMessage
    static bool hooked;

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
        hooked = false;
        IMessage = false;
        iMessageItem = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (Workspace.Current == null) return;
        Apply(false);   // Slack in the middle until the hub says otherwise
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
        if (mode == null) return;
        var slack = mode.items.Find(i => i.id == SlackId);
        var im = mode.items.Find(i => i.id == IMessageId);
        if (im != null && iMessageItem == null)
        {
            iMessageItem = im;
            if (slack != null) slackSlot = slack.slot;
        }
        bool changed = imessage != IMessage;
        IMessage = imessage;

        if (imessage)
        {
            if (im == null && iMessageItem != null) mode.items.Add(iMessageItem);
            if (slack != null && iMessageItem != null) slack.slot = slackSlot;
        }
        else
        {
            if (im != null)
            {
                mode.items.Remove(im);
                changed = true;
            }
            if (slack != null && slack.slot != WorkspaceSlot.Main)
            {
                slack.slot = WorkspaceSlot.Main;
                changed = true;
            }
        }
        if (changed && ws.ActiveMode == ModeId) ws.Open(ModeId);   // re-lay out what is on screen
    }
}
