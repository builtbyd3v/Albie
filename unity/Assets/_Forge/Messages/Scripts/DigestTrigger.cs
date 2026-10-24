using System.Collections;
using UnityEngine;

/// <summary>
/// The arrival digest (PROJECT_CONTEXT 8.8). When <see cref="DeskPresence"/> goes Seated it POSTs /messages/digest
/// once per sit; the hub answers with notify.digest and a pet.say line, which Bit speaks. If the digest has messages
/// and MSG mode is closed (no <see cref="MessagesPanel"/> open), a floating copy of the panel appears beside Bit at
/// the front-right of the desk and folds away after <see cref="floatingSeconds"/>, when a real panel opens, or when
/// the user leaves the desk.
/// Installs itself: on scene load it adds one to any scene that has a DeskPresence (so Main needs no edit); dropping
/// the component into a scene by hand also works and stops the auto-install.
/// </summary>
public class DigestTrigger : MonoBehaviour
{
    /// <summary>Resources path of the panel prefab (Assets/_Forge/Messages/Resources/MessagesPanel.prefab).</summary>
    public const string PanelResource = "MessagesPanel";
    const string DigestPath = "/messages/digest";

    [Tooltip("Seconds the floating cards stay up after the digest.")]
    public float floatingSeconds = 20f;
    [Tooltip("Floating panel pose in the desk frame (m): right of the front edge midpoint, up, into the desk.")]
    public Vector3 floatingOffset = new Vector3(0.35f, 0.25f, 0.10f);

    DeskPresence presence;
    bool sentThisSit;
    GameObject floatingPanel;
    Coroutine fold;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindAnyObjectByType<DigestTrigger>() != null || FindAnyObjectByType<DeskPresence>() == null) return;
        new GameObject("MessagesDigestTrigger").AddComponent<DigestTrigger>();
    }

    void Start()
    {
        presence = FindAnyObjectByType<DeskPresence>();
        if (presence == null)
        {
            Debug.LogWarning("[DigestTrigger] No DeskPresence in the scene; the arrival digest is off.");
            enabled = false;
            return;
        }
        presence.stateChanged.AddListener(OnDeskState);
        ForgeNet.StatusChanged += OnNetStatus;
        ForgeEvents.OnNotifyDigest += OnDigest;
        MessagesPanel.ActiveChanged += OnPanelsChanged;
        OnDeskState(presence.State);
    }

    void OnDestroy()
    {
        if (presence != null) presence.stateChanged.RemoveListener(OnDeskState);
        ForgeNet.StatusChanged -= OnNetStatus;
        ForgeEvents.OnNotifyDigest -= OnDigest;
        MessagesPanel.ActiveChanged -= OnPanelsChanged;
    }

    void OnDeskState(DeskState state)
    {
        if (state == DeskState.Away)
        {
            sentThisSit = false;
            Fold();
            return;
        }
        if (state == DeskState.Seated) RequestDigest();
    }

    // Sat down before the socket was up: ask as soon as it is.
    void OnNetStatus(ForgeNetStatus status)
    {
        if (status == ForgeNetStatus.Live && presence.State == DeskState.Seated) RequestDigest();
    }

    void RequestDigest()
    {
        if (sentThisSit || ForgeNet.Status != ForgeNetStatus.Live) return;
        sentThisSit = true;
        ForgeNet.Post(DigestPath, null);  // the answer comes back as notify.digest (+ pet.say) on /ws
    }

    void OnDigest(NotifyDigest d)
    {
        if (d == null || d.count == 0 || MessagesPanel.ActiveCount > 0 || presence.State != DeskState.Seated) return;
        ShowFloating();
    }

    void OnPanelsChanged()
    {
        if (MessagesPanel.ActiveCount > 0) Fold();
    }

    void ShowFloating()
    {
        Fold(immediate: true);
        var prefab = Resources.Load<GameObject>(PanelResource);
        if (prefab == null)
        {
            Debug.LogWarning($"[DigestTrigger] Resources/{PanelResource}.prefab not found; run Forge > Messages > Build Prefabs.");
            return;
        }

        // Instantiate under an inactive holder so the panel's OnEnable sees floating = true.
        var holder = new GameObject("FloatingMessages");
        holder.SetActive(false);
        holder.transform.SetParent(presence.Frame, false);
        holder.transform.localPosition = floatingOffset;
        var go = Instantiate(prefab, holder.transform);
        var panel = go.GetComponent<MessagesPanel>();
        if (panel != null) panel.floating = true;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;

        // Face the user: a world-space canvas reads right when its +Z points away from the viewer.
        Transform head = presence.head != null ? presence.head : (Camera.main != null ? Camera.main.transform : null);
        if (head != null)
        {
            Vector3 toPanel = holder.transform.position - head.position;
            toPanel.y *= 0.3f;  // mostly upright
            if (toPanel.sqrMagnitude > 1e-4f) holder.transform.rotation = Quaternion.LookRotation(toPanel, Vector3.up);
        }

        holder.SetActive(true);
        floatingPanel = holder;

        Transform t = holder.transform;
        Vector3 scale = t.localScale;
        t.localScale = scale * 0.02f;
        StartCoroutine(ForgeTween.Run(0.35f, 0f, k =>
        {
            if (t != null) t.localScale = scale * Mathf.Max(0.02f, ForgeTween.EaseOutBack(k));
        }));
        fold = StartCoroutine(FoldLater());
    }

    IEnumerator FoldLater()
    {
        yield return new WaitForSeconds(floatingSeconds);
        fold = null;
        Fold();
    }

    void Fold(bool immediate = false)
    {
        if (fold != null) { StopCoroutine(fold); fold = null; }
        if (floatingPanel == null) return;
        GameObject go = floatingPanel;
        floatingPanel = null;
        if (immediate)
        {
            Destroy(go);
            return;
        }
        Transform t = go.transform;
        Vector3 s0 = t.localScale;
        StartCoroutine(ForgeTween.Run(0.25f, 0f, k =>
        {
            if (t != null) t.localScale = s0 * (1f - ForgeTween.EaseInCubic(k) * 0.98f);
        }, () => { if (go != null) Destroy(go); }));
    }
}
