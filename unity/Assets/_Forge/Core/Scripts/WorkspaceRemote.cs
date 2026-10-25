using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Bit opens and closes the workspace views (docs/PET_AGENT_CONTRACT.md Addendum H). The hub's
/// <c>ui.mode {mode}</c> opens that mode (<c>""</c> closes the open one), then this answers at once with
/// <c>workspace.mode {pet_id, mode, reason?}</c>, so Bit's tool result says what really happened: <c>mode</c> is
/// what is open now, <c>reason</c> why it isn't what was asked (<c>not_at_desk</c>: DeskPresence is Away, so
/// <see cref="Workspace.Open"/> would refuse; <c>unknown_mode</c>: no such mode). Asking for the open mode keeps it open
/// (Open, not Toggle). Installs itself on the scene's Workspace when Play starts, so neither Main nor a prefab needs
/// setting up; a scene without a Workspace (the module sandboxes) gets nothing. ArrivalDirector still sends its own
/// workspace.mode on every change; the hub takes either.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Workspace))]
public class WorkspaceRemote : MonoBehaviour
{
    const string WorkspaceModeType = "workspace.mode";
    const string PetId = "bit";

    Workspace workspace;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        var ws = Workspace.Current != null ? Workspace.Current : FindAnyObjectByType<Workspace>();
        if (ws != null && ws.GetComponent<WorkspaceRemote>() == null) ws.gameObject.AddComponent<WorkspaceRemote>();
    }

    void Awake() => workspace = GetComponent<Workspace>();

    void OnEnable() => ForgeEvents.OnUiMode += OnUiMode;

    void OnDisable() => ForgeEvents.OnUiMode -= OnUiMode;

    void OnUiMode(UiMode m)
    {
        string want = (m?.mode ?? "").Trim().ToLowerInvariant();
        string reason = null;
        if (want.Length == 0) workspace.Close();
        else if (workspace.modes.Find(x => x.id == want) == null) reason = "unknown_mode";
        else if (workspace.presence == null || !workspace.presence.AtDesk) reason = "not_at_desk";
        else if (workspace.ActiveMode != want) workspace.Open(want);

        var answer = new JObject { ["pet_id"] = PetId, ["mode"] = workspace.ActiveMode ?? "" };
        if (reason != null) answer["reason"] = reason;
        ForgeNet.Send(WorkspaceModeType, answer);
        Debug.Log($"[WorkspaceRemote] Bit asked for '{want}': {(reason ?? "open: '" + (workspace.ActiveMode ?? "") + "'")}.");
    }
}
