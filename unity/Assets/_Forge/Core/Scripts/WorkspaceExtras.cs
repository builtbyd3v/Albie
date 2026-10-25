using System;
using System.Collections.Generic;
using UnityEngine;

public enum ExtraState { Closed, Open, Minimized }

/// <summary>
/// Where an extra goes until the user moves it. Top: just above the open mode's middle panels, growing upward (give the
/// prefab's root a bottom pivot). Beside: outside the open panels on the right. Cinema: high over the desk and further
/// back, tilted down to the eyes, above whatever is at the Top.
/// </summary>
public enum ExtraAnchor { Top, Beside, Cinema }

/// <summary>
/// Panels that come and go outside the modes: Bit's desk extras (the timers, the notes, the movie). They stay open while
/// modes switch and follow the open mode's layout (until the user moves them), fold away when you stand up and come
/// back when you sit down. Minimized, a panel shrinks into a <see cref="PanelOrb"/> where it was: tap the orb and the
/// panel opens there again, hold it to carry it somewhere else. Where things were moved is kept for the session.
/// The owner (the Desk module, driven by the hub's ui.widgets) calls <see cref="Set"/>; a tapped orb asks it through
/// <see cref="Requested"/> instead of opening by itself, so the hub stays the one that decides.
/// Poses are in the desk frame, like <see cref="Workspace"/>'s. Without a workspace (a sandbox) it uses
/// <see cref="frame"/> and <see cref="head"/>.
/// </summary>
public class WorkspaceExtras : MonoBehaviour
{
    public Workspace workspace;
    [Header("Without a workspace (a sandbox)")]
    public Transform frame;
    public Transform head;

    [Header("Layout (m, deg)")]
    public float topGap = 0.02f;
    public float cinemaDistance = 2.1f;
    public float cinemaMinPitch = 22f, cinemaMaxPitch = 52f, cinemaMaxDistance = 3.5f;

    /// <summary>The user asked for <c>(id, state)</c>: tapped an orb (Open).</summary>
    public event Action<string, ExtraState> Requested;

    const float SeedScale = 0.35f;  // a panel grows from (and folds to) this share of its size
    const float OrbScale = 0.06f;   // a panel shrinks to this as it goes into its orb

    class Extra
    {
        public string id;
        public ExtraAnchor anchor;
        public bool movable;
        public GameObject prefab;
        public PanelOrb orbPrefab;
        public ExtraState state;            // what's wanted
        public GameObject go;               // the panel (kept, inactive, while minimized or away)
        public ForgePanel panel;
        public PanelManipulator handles;
        public CanvasGroup group;
        public Vector3 restScale;
        public PanelOrb orb;
        public Vector3 orbRestScale;
        public bool panelShown, orbShown;   // on screen (or on its way)
        public Coroutine motion, orbMotion;
        public bool placed;                 // the user moved the panel or its orb: keep that pose
        public bool hasPose;
        public Vector3 position;            // the panel's pose (desk frame)
        public Quaternion rotation = Quaternion.identity;
    }

    readonly Dictionary<string, Extra> extras = new Dictionary<string, Extra>();
    readonly List<Extra> all = new List<Extra>();

    Transform Frame => workspace != null ? workspace.presence.Frame : frame;
    Transform Head => workspace != null ? workspace.presence.head : head;
    bool AtDesk => workspace == null || workspace.presence.AtDesk;
    Vector3 Eye => workspace != null ? workspace.LayoutEye : Frame.InverseTransformPoint(Head.position);

    void OnEnable()
    {
        if (workspace == null) return;
        workspace.presence.stateChanged.AddListener(OnDeskState);
        workspace.modeChanged.AddListener(OnModeChanged);
        workspace.layoutChanged.AddListener(OnLayoutChanged);
    }

    void OnDisable()
    {
        if (workspace == null) return;
        workspace.presence.stateChanged.RemoveListener(OnDeskState);
        workspace.modeChanged.RemoveListener(OnModeChanged);
        workspace.layoutChanged.RemoveListener(OnLayoutChanged);
    }

    /// <summary>
    /// Show, minimize or close one extra. Spawns <paramref name="prefab"/> (a ForgePanel) the first time; minimizing
    /// needs <paramref name="orbPrefab"/>. <paramref name="movable"/>: give it the panel handles (move, resize, turn).
    /// Returns the panel instance (inactive while minimized or away), or null once closed.
    /// </summary>
    public GameObject Set(string id, ExtraState state, GameObject prefab, ExtraAnchor anchor, PanelOrb orbPrefab = null, bool movable = true)
    {
        if (!extras.TryGetValue(id, out var e))
        {
            extras[id] = e = new Extra { id = id };
            all.Add(e);
        }
        e.prefab = prefab;
        e.anchor = anchor;
        e.orbPrefab = orbPrefab;
        e.movable = movable;
        if (state == ExtraState.Minimized && orbPrefab == null) state = ExtraState.Open;  // nothing to shrink into
        e.state = state;
        if (state != ExtraState.Closed && e.go == null && prefab != null) Spawn(e);
        if (AtDesk || state == ExtraState.Closed) Apply(e);
        return e.go;
    }

    public ExtraState State(string id) => extras.TryGetValue(id, out var e) ? e.state : ExtraState.Closed;
    public GameObject Panel(string id) => extras.TryGetValue(id, out var e) ? e.go : null;
    public PanelOrb Orb(string id) => extras.TryGetValue(id, out var e) ? e.orb : null;

    /// <summary>Put every extra the user hasn't moved back on its anchor (after a size change that moves anchors).</summary>
    public void Relayout()
    {
        foreach (var e in all)
        {
            if (Placed(e)) continue;
            e.hasPose = false;
            if (e.panelShown && e.motion == null) Settle(e);
            else if (e.orbShown && e.orbMotion == null) SettleOrb(e);
        }
    }

    // ---------------------------------------------------------------- reacting

    void OnDeskState(DeskState state)
    {
        foreach (var e in all)
        {
            if (state == DeskState.Away) HideAll(e);
            else Apply(e);
        }
    }

    void OnModeChanged(string modeId) => Relayout();
    void OnLayoutChanged(WorkspaceLayout layout) => Relayout();

    // Bring what's on screen to what's wanted.
    void Apply(Extra e)
    {
        switch (e.state)
        {
            case ExtraState.Closed:
                Discard(e);
                break;
            case ExtraState.Open:
                if (e.go == null) return;
                if (e.orbShown) ExpandFromOrb(e);
                else if (!e.panelShown) Unfold(e);
                break;
            case ExtraState.Minimized:
                if (e.go == null) return;
                if (e.panelShown) ShrinkIntoOrb(e);
                else if (!e.orbShown) ShowOrb(e, OrbPoint(e), delay: 0f);
                break;
        }
    }

    // ---------------------------------------------------------------- the panel

    void Spawn(Extra e)
    {
        e.go = Instantiate(e.prefab, Frame, false);
        e.go.name = e.prefab.name;
        e.restScale = e.go.transform.localScale;
        e.panel = e.go.GetComponent<ForgePanel>();
        e.group = e.go.GetComponent<CanvasGroup>();
        if (e.group == null) e.group = e.go.AddComponent<CanvasGroup>();
        if (e.movable && e.panel != null)
        {
            e.handles = e.go.GetComponent<PanelManipulator>();
            if (e.handles == null) e.handles = e.go.AddComponent<PanelManipulator>();
            e.handles.Init(Head, Frame);
        }
        e.go.SetActive(false);
    }

    void Unfold(Extra e)
    {
        Pose(e, out Vector3 pos, out Quaternion rot);
        Transform t = e.go.transform;
        t.localPosition = pos;
        t.localRotation = rot;
        t.localScale = e.restScale * SeedScale;
        e.group.alpha = 0f;
        e.group.blocksRaycasts = true;
        e.go.SetActive(true);
        if (e.handles != null) e.handles.SetPlacement(PanelFacing.FaceMe, byUser: Placed(e));
        e.panelShown = true;
        Animate(e, ForgeTheme.UnfoldTime, 0f, k =>
        {
            t.localScale = e.restScale * Mathf.LerpUnclamped(SeedScale, 1f, ForgeTween.SpringDamped(k, 0.74f, 1.35f));
            e.group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, ForgeTheme.UnfoldFade, k));
        }, null);
    }

    // Fold it away where it is; destroyed (closed) or kept for later (away).
    void Fold(Extra e, bool destroy)
    {
        if (e.go == null) return;
        if (!e.panelShown)
        {
            if (destroy) DestroyPanel(e);
            return;
        }
        Capture(e);
        if (e.handles != null) e.handles.Cancel();
        e.panelShown = false;
        e.group.blocksRaycasts = false;
        Transform t = e.go.transform;
        Vector3 s0 = t.localScale;
        float a0 = e.group.alpha;
        Animate(e, ForgeTheme.FoldTime, 0f, k =>
        {
            float f = ForgeTween.EaseInOutCubic(k);
            t.localScale = Vector3.LerpUnclamped(s0, e.restScale * SeedScale, f);
            e.group.alpha = a0 * (1f - f);
        }, () =>
        {
            if (destroy) DestroyPanel(e);
            else if (e.go != null) e.go.SetActive(false);
        });
    }

    // Spring from wherever it is to its anchor (the layout moved under it).
    void Settle(Extra e)
    {
        Pose(e, out Vector3 pos, out Quaternion rot);
        Transform t = e.go.transform;
        Vector3 p0 = t.localPosition;
        Quaternion r0 = t.localRotation;
        Animate(e, ForgeTheme.LayoutTime, 0f, k =>
        {
            float f = ForgeTween.SpringDamped(k, 0.9f, 1.3f);
            t.localPosition = Vector3.LerpUnclamped(p0, pos, f);
            t.localRotation = Quaternion.SlerpUnclamped(r0, rot, f);
        }, () => { if (e.handles != null) e.handles.SetPlacement(PanelFacing.FaceMe, byUser: false); });
    }

    // ---------------------------------------------------------------- minimizing

    // The panel shrinks into the point it's centred on, fading, and the orb pops out there.
    void ShrinkIntoOrb(Extra e)
    {
        Capture(e);
        Vector3 point = OrbPoint(e);
        if (e.handles != null) e.handles.Cancel();
        e.panelShown = false;
        e.group.blocksRaycasts = false;
        Transform t = e.go.transform;
        Vector3 p0 = t.localPosition, s0 = t.localScale;
        Vector3 toCenter = t.localPosition - point;  // the panel's pivot from its centre
        float a0 = e.group.alpha;
        Animate(e, 0.35f, 0f, k =>
        {
            float f = ForgeTween.EaseInOutCubic(k);
            float s = Mathf.LerpUnclamped(1f, OrbScale, f);
            t.localScale = Vector3.LerpUnclamped(s0, e.restScale * OrbScale, f);
            t.localPosition = point + toCenter * s;
            e.group.alpha = a0 * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 1f, k)));
        }, () => { if (e.go != null) e.go.SetActive(false); });
        ShowOrb(e, point, delay: 0.15f);
    }

    // The orb goes and the panel grows out of where it was: there if the orb was carried, else back on its anchor.
    void ExpandFromOrb(Extra e)
    {
        Vector3 from = e.orb != null ? e.orb.transform.localPosition : OrbPoint(e);
        Pose(e, out Vector3 pos, out Quaternion rot);
        Transform t = e.go.transform;
        t.localRotation = rot;
        t.localScale = e.restScale * OrbScale;
        Vector3 toCenter = pos - CenterOf(e, pos, rot);
        t.localPosition = from + toCenter * OrbScale;
        e.group.alpha = 0f;
        e.group.blocksRaycasts = true;
        e.go.SetActive(true);
        if (e.handles != null) e.handles.SetPlacement(PanelFacing.FaceMe, byUser: Placed(e));
        e.panelShown = true;
        Vector3 c0 = from, c1 = pos - toCenter;
        Animate(e, ForgeTheme.UnfoldTime, 0.05f, k =>
        {
            float g = ForgeTween.SpringDamped(k, 0.74f, 1.35f);
            float s = Mathf.LerpUnclamped(OrbScale, 1f, g);
            t.localScale = e.restScale * s;
            t.localPosition = Vector3.LerpUnclamped(c0, c1, ForgeTween.SpringDamped(k, 1f, 1.5f)) + toCenter * s;
            e.group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, ForgeTheme.UnfoldFade, k));
        }, null);
        HideOrb(e, destroy: true);
    }

    void ShowOrb(Extra e, Vector3 point, float delay)
    {
        if (e.orbPrefab == null) return;
        if (e.orb == null)
        {
            e.orb = Instantiate(e.orbPrefab, Frame, false);
            e.orb.name = e.id + " orb";
            e.orbRestScale = e.orb.transform.localScale;
            e.orb.Init(Head);
            string id = e.id;
            e.orb.Tapped += () => Requested?.Invoke(id, ExtraState.Open);
            e.orb.Moved += () => OrbMoved(e);
            e.orb.transform.localPosition = point;
        }
        Transform t = e.orb.transform;
        if (!e.orbShown) t.localPosition = point;
        t.localRotation = Face(t.localPosition);
        e.orb.gameObject.SetActive(true);
        e.orb.Busy = true;
        e.orbShown = true;
        CanvasGroup group = e.orb.Group;
        group.blocksRaycasts = true;
        float a0 = group.alpha;
        Vector3 s0 = t.localScale.x > 0.001f * e.orbRestScale.x ? t.localScale : e.orbRestScale * 0.4f;
        AnimateOrb(e, 0.4f, delay, k =>
        {
            t.localScale = Vector3.LerpUnclamped(s0, e.orbRestScale, ForgeTween.SpringDamped(k, 0.6f, 1.4f));
            group.alpha = Mathf.Lerp(a0, 1f, Mathf.InverseLerp(0f, 0.4f, k));
        }, () => e.orb.Busy = false);
    }

    void HideOrb(Extra e, bool destroy)
    {
        if (e.orb == null) return;
        e.orbShown = false;
        e.orb.Busy = true;
        PanelOrb orb = e.orb;
        if (destroy) e.orb = null;
        Transform t = orb.transform;
        CanvasGroup group = orb.Group;
        group.blocksRaycasts = false;
        Vector3 s0 = t.localScale;
        float a0 = group.alpha;
        Action<float> step = k =>
        {
            if (t == null) return;
            t.localScale = Vector3.LerpUnclamped(s0, s0 * 0.4f, ForgeTween.EaseInCubic(k));
            group.alpha = a0 * (1f - k);
        };
        Action done = () =>
        {
            if (orb == null) return;
            if (destroy) Destroy(orb.gameObject);
            else orb.gameObject.SetActive(false);
        };
        // One on its way out runs on its own, so a new orb's motion can't cut it short and leave it behind.
        if (destroy)
        {
            if (e.orbMotion != null) StopCoroutine(e.orbMotion);
            e.orbMotion = null;
            StartCoroutine(ForgeTween.Run(0.22f, 0f, step, done));
        }
        else AnimateOrb(e, 0.22f, 0f, step, done);
    }

    // Spring the orb to the middle of its panel's anchor.
    void SettleOrb(Extra e)
    {
        if (e.orb == null) return;
        Pose(e, out Vector3 pos, out Quaternion rot);
        Vector3 to = CenterOf(e, pos, rot);
        Transform t = e.orb.transform;
        Vector3 from = t.localPosition;
        AnimateOrb(e, ForgeTheme.LayoutTime, 0f, k =>
        {
            t.localPosition = Vector3.LerpUnclamped(from, to, ForgeTween.SpringDamped(k, 0.9f, 1.3f));
            t.localRotation = Face(t.localPosition);
        }, null);
    }

    // Carried somewhere: the panel opens there next time, facing the eyes.
    void OrbMoved(Extra e)
    {
        if (e.orb == null || e.go == null) return;
        e.placed = true;
        Vector3 center = e.orb.transform.localPosition;
        Quaternion rot = Face(center);
        e.rotation = rot;
        e.position = center - (CenterOf(e, Vector3.zero, rot));  // the pivot that puts the panel's centre there
        e.hasPose = true;
        e.orb.transform.localRotation = rot;
    }

    // ---------------------------------------------------------------- away / closed

    void HideAll(Extra e)
    {
        Fold(e, destroy: false);
        if (e.orbShown) HideOrb(e, destroy: false);
    }

    void Discard(Extra e)
    {
        Fold(e, destroy: true);
        if (e.orb != null) HideOrb(e, destroy: true);
    }

    void DestroyPanel(Extra e)
    {
        if (e.go != null) Destroy(e.go);
        e.go = null;
        e.panel = null;
        e.handles = null;
        e.group = null;
        e.panelShown = false;
        e.motion = null;
    }

    // ---------------------------------------------------------------- poses

    static bool Placed(Extra e) => e.placed || (e.handles != null && e.handles.UserPlaced);

    // Where the user left it, kept for next time (folding, minimizing).
    void Capture(Extra e)
    {
        if (e.go == null || !Placed(e)) return;
        e.position = e.go.transform.localPosition;
        e.rotation = e.go.transform.localRotation;
        e.placed = e.hasPose = true;
    }

    void Pose(Extra e, out Vector3 pos, out Quaternion rot)
    {
        if (Placed(e) && e.hasPose)
        {
            pos = e.position;
            rot = e.rotation;
            return;
        }
        Vector2 size = e.panel != null ? e.panel.SizeMeters : new Vector2(0.4f, 0.3f);
        Vector3 eye = Eye;
        switch (e.anchor)
        {
            case ExtraAnchor.Top:
                TopEdge(out Vector3 edge, out rot);
                pos = edge + rot * Vector3.up * topGap;
                break;
            case ExtraAnchor.Beside:
                if (workspace != null) workspace.BesidePose(+1, size, out pos, out rot);
                else Around(eye, 32f, -0.05f, 0.8f, out pos, out rot);  // a sandbox: in a desktop camera's view
                break;
            default:
                Cinema(eye, size, out pos, out rot);
                break;
        }
        // Out of the room's walls (a Top extra rides on the main panel, which is already clear).
        if (e.anchor != ExtraAnchor.Top && RoomFit.Begin(Frame, eye) && RoomFit.InWall(pos, rot, size, false))
            RoomFit.Fit(ref pos, ref rot, size, false, true);
        e.position = pos;
        e.rotation = rot;
        e.hasPose = true;
    }

    void TopEdge(out Vector3 pos, out Quaternion rot)
    {
        if (workspace != null) workspace.TopEdge(out pos, out rot);
        else Around(Eye, 0f, 0.2f, 0.8f, out pos, out rot);
    }

    // High over the desk, straight ahead, its bottom edge a little above whatever stands at the Top, tilted down to face
    // the eyes. When that would mean looking up past cinemaMaxPitch (ML's Training panel and a few timers), it goes
    // further back instead, so it looks smaller and still clears them.
    void Cinema(Vector3 eye, Vector2 size, out Vector3 pos, out Quaternion rot)
    {
        TopEdge(out Vector3 top, out Quaternion topRot);
        top += topRot * Vector3.up * (topGap + TopHeight());
        Vector3 d = top - eye;
        float bottom = Mathf.Atan2(d.y, Mathf.Max(0.3f, new Vector2(d.x, d.z).magnitude)) * Mathf.Rad2Deg + 3f;
        float distance = cinemaDistance;
        float half = Mathf.Atan2(size.y * 0.5f, distance) * Mathf.Rad2Deg;
        if (bottom + half > cinemaMaxPitch)
        {
            half = Mathf.Max(6f, cinemaMaxPitch - bottom);
            distance = Mathf.Min(cinemaMaxDistance, Mathf.Max(distance, size.y * 0.5f / Mathf.Tan(half * Mathf.Deg2Rad)));
            half = Mathf.Atan2(size.y * 0.5f, distance) * Mathf.Rad2Deg;
        }
        float pitch = Mathf.Clamp(bottom + half, cinemaMinPitch, cinemaMaxPitch);
        pos = eye + Quaternion.Euler(-pitch, 0f, 0f) * Vector3.forward * distance;
        rot = Quaternion.LookRotation(pos - eye, Vector3.up);
    }

    // How tall the open Top extras are (the timers' stack), so the Cinema clears them.
    float TopHeight()
    {
        float h = 0f;
        foreach (var e in all)
            if (e.anchor == ExtraAnchor.Top && e.panelShown && e.panel != null) h = Mathf.Max(h, e.panel.SizeMeters.y);
        return h;
    }

    static void Around(Vector3 eye, float yaw, float up, float radius, out Vector3 pos, out Quaternion rot)
    {
        float a = yaw * Mathf.Deg2Rad;
        pos = eye + new Vector3(Mathf.Sin(a) * radius, up, Mathf.Cos(a) * radius);
        rot = Quaternion.LookRotation(pos - eye, Vector3.up);
    }

    // The panel's centre (its rect's middle) when its pivot is at pos, turned rot. Desk frame.
    Vector3 CenterOf(Extra e, Vector3 pos, Quaternion rot)
    {
        if (e.go == null) return pos;
        var rect = (RectTransform)e.go.transform;
        Vector3 local = Vector3.Scale((Vector3)rect.rect.center, e.restScale);
        return pos + rot * local;
    }

    // Where its orb goes: the middle of the panel as it is now (or will be).
    Vector3 OrbPoint(Extra e)
    {
        if (e.go != null && e.go.activeSelf) return CenterOf(e, e.go.transform.localPosition, e.go.transform.localRotation);
        Pose(e, out Vector3 pos, out Quaternion rot);
        return CenterOf(e, pos, rot);
    }

    Quaternion Face(Vector3 point)
    {
        Vector3 d = point - Eye;
        return Quaternion.LookRotation(d.sqrMagnitude > 1e-6f ? d : Vector3.forward, Vector3.up);
    }

    // ---------------------------------------------------------------- motion

    void Animate(Extra e, float duration, float delay, Action<float> step, Action done)
    {
        if (e.motion != null) StopCoroutine(e.motion);
        if (e.handles != null) e.handles.Busy = true;
        e.motion = StartCoroutine(ForgeTween.Run(duration, delay, k => { if (e.go != null) step(k); }, () =>
        {
            e.motion = null;
            if (e.handles != null) e.handles.Busy = false;
            done?.Invoke();
        }));
    }

    void AnimateOrb(Extra e, float duration, float delay, Action<float> step, Action done)
    {
        if (e.orbMotion != null) StopCoroutine(e.orbMotion);
        e.orbMotion = StartCoroutine(ForgeTween.Run(duration, delay, step, () =>
        {
            e.orbMotion = null;
            done?.Invoke();
        }));
    }
}
