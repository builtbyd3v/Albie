using UnityEngine;

/// <summary>
/// Routes notify.draft (PROJECT_CONTEXT 8.8). With the matching provider panel open, the draft goes into its composer
/// as editable text, with Send / Cancel and the conversation switched to the draft's. Otherwise it pops
/// a <see cref="DraftCard"/> (Send / Cancel) floating at the desk's front-right beside Bit, a little closer to the user
/// near the user, or in front of the camera when there is no desk. One card at a time: a new draft replaces the
/// open one (the hub also emits notify.draft_cancelled with reason superseded for it).
/// Installs itself on scene load into any scene that has a DeskPresence.
/// </summary>
public class DraftPresenter : MonoBehaviour
{
    /// <summary>Resources path of the card prefab (Assets/_Forge/Messages/Resources/DraftCard.prefab).</summary>
    public const string CardResource = "DraftCard";

    [Tooltip("Floating card pose in the desk frame (m) when MSG mode is closed: right of the front edge midpoint, up, into the desk.")]
    public Vector3 floatingOffset = new Vector3(0.35f, 0.22f, 0.02f);

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
        SlackPanel.ActiveChanged += OnPanelsChanged;
    }

    void OnDestroy()
    {
        ForgeEvents.OnNotifyDraft -= OnDraft;
        SlackPanel.ActiveChanged -= OnPanelsChanged;
    }

    // The matching provider panel opened while a card floats: its composer takes over the draft, the card folds.
    void OnPanelsChanged()
    {
        if (card == null || card.Closing) return;
        var panel = SlackPanel.ForSource(card.Draft != null ? card.Draft.source : null);
        if (panel == null) return;
        panel.ReceiveDraft(card.Draft);
        DigestTrigger.FoldOut(this, card.Root != null ? card.Root : card.gameObject);
        card = null;
    }

    void OnDraft(NotifyDraft d)
    {
        if (d == null || string.IsNullOrEmpty(d.draftId)) return;
        if (SlackPanel.ForSource(d.source) != null) return;  // the matching panel handles it in its composer
        if (card != null)
        {
            if (card.Draft != null && card.Draft.draftId == d.draftId && d.edited)
            {
                card.Bind(d);  // the same draft, edited (PATCH): refresh the text
                return;
            }
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
        if (presence != null && presence.AtDesk && presence.Frame != null) root = DigestTrigger.SpawnAtDesk(presence, prefab, floatingOffset, "FloatingDraft");
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
