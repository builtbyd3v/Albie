using System;
using UnityEngine;

/// <summary>
/// The arrival digest request and shared panel animation / placement helpers. The hub's notify.digest lets Bit tell
/// the user about new messages; there is no separate floating mentions notice.
/// The static helpers (<see cref="SpawnAtDesk"/>, <see cref="PopIn"/>, <see cref="FoldOut"/>) are shared with
/// <see cref="DraftPresenter"/>, so the Send / Cancel card appears and leaves the same way.
/// </summary>
public class DigestTrigger : MonoBehaviour
{
    const string DigestPath = "/messages/digest";

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

    // ---- shared placement / animation helpers ---------------------------------------------------------------

    /// <summary>
    /// Instantiate <paramref name="prefab"/> beside Bit: a holder parented to the desk frame at <paramref name="offset"/>
    /// (m: right of the front edge midpoint, up, into the desk), turned to face the user. <paramref name="configure"/>
    /// runs on the instance while the holder is still inactive, so components can be set up before their OnEnable.
    /// Returns the (active) holder; destroy it to remove the whole thing. The holder carries a <see cref="MessagesDeskZone"/>
    /// so the pet keeps its gators out from under floating cards.
    /// </summary>
    public static GameObject SpawnAtDesk(DeskPresence presence, GameObject prefab, Vector3 offset, string holderName,
        Action<GameObject> configure = null)
    {
        var holder = new GameObject(holderName);
        holder.SetActive(false);
        holder.AddComponent<MessagesDeskZone>();
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
