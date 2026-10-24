using UnityEngine;

/// <summary>
/// Shows the Send / Cancel card for notify.draft (PROJECT_CONTEXT 8.8). One card at a time: a new draft replaces the open
/// one (the hub also emits notify.draft_cancelled with reason superseded for it). Placement: docked just below the open
/// <see cref="MessagesPanel"/> when MSG mode is open; otherwise floating at the desk's front-right beside Bit, a little
/// closer to the user than the digest stack; with no desk in the scene, in front of the camera.
/// Installs itself like <see cref="DigestTrigger"/>: on scene load, into any scene that has a DeskPresence.
/// </summary>
public class DraftPresenter : MonoBehaviour
{
    /// <summary>Resources path of the card prefab (Assets/_Forge/Messages/Resources/DraftCard.prefab).</summary>
    public const string CardResource = "DraftCard";

    [Tooltip("Floating card pose in the desk frame (m) when MSG mode is closed: right of the front edge midpoint, up, into the desk.")]
    public Vector3 floatingOffset = new Vector3(0.35f, 0.22f, 0.02f);
    [Tooltip("Gap between the open Messages panel's bottom edge and the card (m).")]
    public float dockGap = 0.03f;

    DeskPresence presence;
    DraftCard card;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<DraftPresenter>() != null || FindAnyObjectByType<DeskPresence>() == null) return;
        new GameObject("MessagesDraftPresenter").AddComponent<DraftPresenter>();
    }

    void Start()
    {
        presence = FindAnyObjectByType<DeskPresence>();
        ForgeEvents.OnNotifyDraft += OnDraft;
    }

    void OnDestroy() => ForgeEvents.OnNotifyDraft -= OnDraft;

    void OnDraft(NotifyDraft d)
    {
        if (d == null || string.IsNullOrEmpty(d.draftId)) return;
        if (card != null)
        {
            Destroy(card.Root != null ? card.Root : card.gameObject);  // replaced; the hub cancels the old draft itself
            card = null;
        }

        var prefab = Resources.Load<GameObject>(CardResource);
        if (prefab == null)
        {
            Debug.LogWarning($"[DraftPresenter] Resources/{CardResource}.prefab not found; run Forge > Messages > Build Prefabs.");
            return;
        }

        GameObject root;
        MessagesPanel open = MessagesPanel.Active;
        if (open != null) root = DockBelow(prefab, open);
        else if (presence != null) root = DigestTrigger.SpawnAtDesk(presence, prefab, floatingOffset, "FloatingDraft");
        else root = InFrontOfCamera(prefab);
        if (root == null) return;

        card = root.GetComponentInChildren<DraftCard>();
        if (card == null)
        {
            Debug.LogError("[DraftPresenter] DraftCard.prefab has no DraftCard component.");
            Destroy(root);
            return;
        }
        card.Root = root;
        card.Bind(d);
        DigestTrigger.PopIn(this, root.transform);
    }

    // Under the open panel's bottom edge, same facing. Not parented to the panel (its root is a 1 mm-per-unit canvas);
    // parented to the desk frame when there is one so it follows the table.
    GameObject DockBelow(GameObject prefab, MessagesPanel panel)
    {
        Transform anchor = panel.transform;
        Vector2 panelSize = panel.Panel != null ? panel.Panel.SizeMeters : new Vector2(0.42f, 0.52f);
        float drop = panelSize.y * 0.5f + dockGap + DraftCard.SizeMeters.y * 0.5f;

        var holder = new GameObject("DockedDraft");
        if (presence != null) holder.transform.SetParent(presence.Frame, false);
        holder.transform.SetPositionAndRotation(anchor.position - anchor.up * drop, anchor.rotation);
        var go = Instantiate(prefab, holder.transform);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        return holder;
    }

    static GameObject InFrontOfCamera(GameObject prefab)
    {
        Camera cam = Camera.main;
        if (cam == null) return null;
        var holder = new GameObject("FloatingDraft");
        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        holder.transform.SetPositionAndRotation(cam.transform.position + forward * 0.6f - Vector3.up * 0.1f,
            Quaternion.LookRotation(forward, Vector3.up));
        var go = Instantiate(prefab, holder.transform);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        return holder;
    }
}
