using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// The arrival digest's cards (PROJECT_CONTEXT 8.8). The POST /messages/digest itself is <see cref="RequestDigest"/>,
/// called by the pet's ArrivalDirector once per sit, after Bit has landed on the desk; the hub answers with
/// notify.digest and a pet.say line, which Bit speaks. If the digest has messages and MSG mode is closed (no
/// <see cref="MessagesPanel"/> open), a floating copy of the panel appears beside Bit at the front-right of the desk
/// and folds away after <see cref="floatingSeconds"/>, when a real panel opens, or when the user leaves the desk.
/// Installs itself: on scene load it adds one to any scene that has a DeskPresence (so Main needs no edit); dropping
/// the component into a scene by hand also works and stops the auto-install.
/// The static helpers (<see cref="SpawnAtDesk"/>, <see cref="PopIn"/>, <see cref="FoldOut"/>) are shared with
/// <see cref="DraftPresenter"/>, so the digest stack and the Send / Cancel card appear and leave the same way.
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
        ForgeEvents.OnNotifyDigest += OnDigest;
        MessagesPanel.ActiveChanged += OnPanelsChanged;
    }

    void OnDestroy()
    {
        if (presence != null) presence.stateChanged.RemoveListener(OnDeskState);
        ForgeEvents.OnNotifyDigest -= OnDigest;
        MessagesPanel.ActiveChanged -= OnPanelsChanged;
    }

    void OnDeskState(DeskState state)
    {
        if (state == DeskState.Away) Fold();
    }

    /// <summary>
    /// POST /messages/digest: the hub answers on /ws with notify.digest (+ a pet.say line Bit speaks) and marks everything
    /// seen. Called by the pet's ArrivalDirector once per sit; no-op (false) unless the hub is live.
    /// </summary>
    public static bool RequestDigest()
    {
        if (ForgeNet.Status != ForgeNetStatus.Live) return false;
        ForgeNet.Post(DigestPath, null);
        return true;
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
        floatingPanel = SpawnAtDesk(presence, prefab, floatingOffset, "FloatingMessages", go =>
        {
            var panel = go.GetComponentInChildren<MessagesPanel>();
            if (panel != null) panel.floating = true;  // set before OnEnable: a floating copy doesn't count as MSG mode open
        });
        PopIn(this, floatingPanel.transform);
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
        if (immediate) Destroy(go);
        else FoldOut(this, go);
    }

    // ---- shared placement / animation helpers ---------------------------------------------------------------

    /// <summary>
    /// Instantiate <paramref name="prefab"/> beside Bit: a holder parented to the desk frame at <paramref name="offset"/>
    /// (m: right of the front edge midpoint, up, into the desk), turned to face the user. <paramref name="configure"/>
    /// runs on the instance while the holder is still inactive, so components can be set up before their OnEnable.
    /// Returns the (active) holder; destroy it to remove the whole thing.
    /// </summary>
    public static GameObject SpawnAtDesk(DeskPresence presence, GameObject prefab, Vector3 offset, string holderName,
        Action<GameObject> configure = null)
    {
        var holder = new GameObject(holderName);
        holder.SetActive(false);
        holder.transform.SetParent(presence.Frame, false);
        holder.transform.localPosition = offset;
        var go = Instantiate(prefab, holder.transform);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        configure?.Invoke(go);
        FaceUser(holder.transform, presence);
        holder.SetActive(true);
        return holder;
    }

    /// <summary>Turn a world-space canvas toward the user: it reads right when its +Z points away from the viewer.</summary>
    public static void FaceUser(Transform t, DeskPresence presence)
    {
        Transform head = presence != null && presence.head != null ? presence.head : (Camera.main != null ? Camera.main.transform : null);
        if (head == null) return;
        Vector3 toPanel = t.position - head.position;
        toPanel.y *= 0.3f;  // mostly upright
        if (toPanel.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(toPanel, Vector3.up);
    }

    /// <summary>Scale-in with a little overshoot, 0.35 s.</summary>
    public static Coroutine PopIn(MonoBehaviour runner, Transform t)
    {
        Vector3 scale = t.localScale;
        t.localScale = scale * 0.02f;
        return runner.StartCoroutine(ForgeTween.Run(0.35f, 0f, k =>
        {
            if (t != null) t.localScale = scale * Mathf.Max(0.02f, ForgeTween.EaseOutBack(k));
        }));
    }

    /// <summary>Scale-out over 0.25 s, then Destroy. <paramref name="runner"/> may live inside <paramref name="go"/>.</summary>
    public static void FoldOut(MonoBehaviour runner, GameObject go)
    {
        if (go == null) return;
        Transform t = go.transform;
        Vector3 s0 = t.localScale;
        runner.StartCoroutine(ForgeTween.Run(0.25f, 0f, k =>
        {
            if (t != null) t.localScale = s0 * (1f - ForgeTween.EaseInCubic(k) * 0.98f);
        }, () => { if (go != null) Destroy(go); }));
    }
}
