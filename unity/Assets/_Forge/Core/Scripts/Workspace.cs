using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public enum WorkspaceSlot { Main, Left, Right, FarLeft, FarRight, Above, DeskCenter, DeskLeft, DeskRight }
public enum WorkspaceItemKind { Panel, Volume }
/// <summary>Curved: panels in an arc, each facing the eyes. Flat: one straight row, like a wall of monitors.</summary>
public enum WorkspaceLayout { Curved, Flat }

/// <summary>One thing that unfolds in a mode: a flat panel or a 3D volume at a slot.</summary>
[Serializable]
public class WorkspaceItem
{
    public string id;
    public string title;
    public WorkspaceSlot slot;
    public WorkspaceItemKind kind;
    [Tooltip("Module prefab to place here. Empty = a placeholder ForgePanel/ForgeVolume.")]
    public GameObject prefab;

    public WorkspaceItem(string id, string title, WorkspaceSlot slot, WorkspaceItemKind kind = WorkspaceItemKind.Panel)
    {
        this.id = id;
        this.title = title;
        this.slot = slot;
        this.kind = kind;
    }
}

/// <summary>A desk mode (ML, CAD, ...): a button on the desk dock and the items it unfolds.</summary>
[Serializable]
public class WorkspaceMode
{
    public string id;
    public string label;
    public Color color = new Color(0.2f, 0.6f, 1f);
    public List<WorkspaceItem> items = new List<WorkspaceItem>();
}

/// <summary>
/// The desk workspace. When the user is at a desk (<see cref="DeskPresence"/>) the dock shows mode buttons on
/// the desk; opening a mode unfolds its panels in an arc around the user's eyes (or a flat row, see
/// <see cref="layout"/>) and its volumes on the desk. Slots are laid out in the desk frame (front edge, +Z into the
/// desk), so they follow whichever table is used; side slots are spaced by the panels' real widths.
/// Every panel gets a <see cref="PanelManipulator"/> (move, resize, turn). What the user changes is remembered per
/// mode for the session, so reopening a mode puts things back; <see cref="ResetLayout"/> forgets it.
/// Pet / voice: call <see cref="Open"/>, <see cref="Toggle"/> or <see cref="Close"/> with a mode id; <see cref="Current"/>
/// is the scene's workspace and <see cref="TryGetItemTransform"/> finds an open panel or volume by item id.
/// <see cref="HoldDock"/> keeps the dock hidden through the pet's first-arrival greeting (it shows on release).
/// </summary>
public class Workspace : MonoBehaviour
{
    public DeskPresence presence;
    public DeskDock dock;
    public ForgePanel panelPrefab;
    public ForgeVolume volumePrefab;
    public List<WorkspaceMode> modes = new List<WorkspaceMode>();

    public UnityEvent<string> modeChanged = new UnityEvent<string>();
    public UnityEvent<WorkspaceLayout> layoutChanged = new UnityEvent<WorkspaceLayout>();

    [Header("Layout (m)")]
    public WorkspaceLayout layout = WorkspaceLayout.Curved;
    public float panelRadius = 0.8f;
    public float sideAngle = 48f;
    public float farAngle = 90f;
    [Tooltip("Space between neighbouring panels (m).")]
    public float panelGap = 0.05f;

    // One unfolded item: its object, and the pose the layout gives it (desk frame).
    class OpenItem
    {
        public WorkspaceItem item;
        public GameObject go;
        public ForgePanel panel;          // panels only
        public PanelManipulator handles;  // panels only
        public ForgeVolume volume;        // volumes only
        public VolumeManipulator grips;   // volumes only
        public Vector2 defaultSize;
        public float defaultScale = 1f;
        public Vector3 position;
        public Quaternion rotation;
        public PanelFacing facing;
        // Motion: the running tween (null at rest) and the scale (panels: size) it lands on. `shown` holds what's on
        // screen while Rearrange lays out at rest. Panels fade through `group`.
        public Coroutine motion;
        public Vector3 restScale, shownScale;
        public Vector2 restSize, shownSize;
        public CanvasGroup group;
    }

    // Where the user left a panel or volume, in the desk frame.
    struct SavedPose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector2 size;         // panels
        public PanelFacing facing;   // panels
        public float scale;          // volumes
        public bool turned;          // volumes: the user turned the model (else it keeps its turntable)
        public float viewYaw, viewTilt;
    }

    readonly List<OpenItem> openItems = new List<OpenItem>();
    readonly Dictionary<string, SavedPose> saved = new Dictionary<string, SavedPose>();  // "mode/item"
    // The outer edge of the open items on each side (left, right) after the last layout: an angle (deg, curved) or an
    // x offset (m, flat), for BesidePose.
    readonly float[] sideEdge = new float[2];

    /// <summary>Id of the open mode, or null when everything is folded.</summary>
    public string ActiveMode { get; private set; }

    /// <summary>The scene's workspace (one per scene), for modules that would otherwise FindAnyObjectByType.</summary>
    public static Workspace Current { get; private set; }

    /// <summary>While held, arriving at the desk doesn't show the dock (<see cref="Open"/> still works). Cleared on Away.</summary>
    public bool DockHeld { get; private set; }

    void Reset() => modes = DefaultModes();

    void Awake() => Current = this;

    void OnEnable()
    {
        Current = this;
        presence.stateChanged.AddListener(OnDeskState);
        presence.frameChanged.AddListener(OnFrameChanged);
    }

    void OnDisable()
    {
        if (Current == this) Current = null;
        presence.stateChanged.RemoveListener(OnDeskState);
        presence.frameChanged.RemoveListener(OnFrameChanged);
    }

    void Start()
    {
        dock.Build(this);
        dock.Hide(immediate: true);
    }

    void OnDeskState(DeskState state)
    {
        if (state == DeskState.Away)
        {
            DockHeld = false;
            Close();
            dock.Hide();
        }
        else if (!dock.IsShown && !DockHeld)
        {
            dock.Show(presence.Frame, presence.FrameWidth, presence.FrameDepth);
        }
    }

    // The user went round to another side of the desk: move the dock to the edge they're at.
    void OnFrameChanged()
    {
        if (dock.IsShown) dock.Show(presence.Frame, presence.FrameWidth, presence.FrameDepth, reposition: true);
    }

    /// <summary>
    /// Hold the dock back (the pet's greeting owns the arrival until START) or let it go: releasing while at the desk
    /// shows it right away.
    /// </summary>
    public void HoldDock(bool hold)
    {
        if (DockHeld == hold) return;
        DockHeld = hold;
        // Taking hold in the same Near that just showed it (handler order): nothing has rendered yet, so no flash.
        if (hold && dock.IsShown && ActiveMode == null) dock.Hide(immediate: true);
        if (!hold && presence.AtDesk && !dock.IsShown) dock.Show(presence.Frame, presence.FrameWidth, presence.FrameDepth);
    }

    public void Toggle(string modeId)
    {
        if (ActiveMode == modeId) Close();
        else Open(modeId);
    }

    public void Open(string modeId)
    {
        var mode = modes.Find(m => m.id == modeId);
        if (mode == null)
        {
            Debug.LogWarning($"[Workspace] No mode '{modeId}'.");
            return;
        }
        if (!presence.AtDesk)
        {
            Debug.LogWarning($"[Workspace] Can't open '{modeId}': not at a desk.");
            return;
        }

        // Switching modes: the old one folds into its ring while this one unfolds out of its own, a beat behind.
        float start = openItems.Count > 0 ? ForgeTheme.SwitchDelay : 0f;
        FoldAll();
        foreach (var item in mode.items) openItems.Add(Spawn(item, mode));
        Vector3 eye = ClampEye(presence.EyeInFrame());
        LayOut(eye);
        for (int i = 0; i < openItems.Count; i++)
        {
            var o = openItems[i];
            bool restored = saved.TryGetValue(Key(modeId, o.item.id), out SavedPose pose);
            if (restored) Restore(o, pose);
            if (o.handles != null) o.handles.SetPlacement(o.facing, byUser: restored);
            if (o.grips != null) o.grips.SetPlacement(byUser: restored);
            Unfold(o, modeId, start + StaggerIndex(o, outermostFirst: false) * ForgeTheme.UnfoldStagger, eye);
        }

        ActiveMode = modeId;
        presence.lockFrame = true;
        dock.SetActiveMode(modeId);
        modeChanged?.Invoke(modeId);
        Debug.Log($"[Workspace] Opened {modeId}: {mode.items.Count} item(s).");
    }

    public void Close()
    {
        if (ActiveMode == null) return;
        FoldAll();
        ActiveMode = null;
        presence.lockFrame = false;
        dock.SetActiveMode(null);
        modeChanged?.Invoke(null);
    }

    /// <summary>
    /// The root transform of an open item (panel or volume) by item id, e.g. for the pet to walk to a panel.
    /// False when no mode is open or the open mode has no such item. Items count as open from the moment
    /// <see cref="Open"/> spawns them, while they are still unfolding.
    /// </summary>
    public bool TryGetItemTransform(string itemId, out Transform t)
    {
        foreach (var o in openItems)
        {
            if (o.go == null || o.item.id != itemId) continue;
            t = o.go.transform;
            return true;
        }
        t = null;
        return false;
    }

    /// <summary>Switch between the curved arc and the flat row. Re-lays out the open mode (keeps panel sizes).</summary>
    public void SetLayout(WorkspaceLayout next)
    {
        layout = next;
        Rearrange(resetSizes: false);
        layoutChanged?.Invoke(layout);
    }

    public void ToggleLayout() => SetLayout(layout == WorkspaceLayout.Curved ? WorkspaceLayout.Flat : WorkspaceLayout.Curved);

    /// <summary>Put the open mode's panels and volumes back where and how big they started.</summary>
    public void ResetLayout() => Rearrange(resetSizes: true);

    // Forget the open mode's saved poses and spring every item to its slot in the current layout.
    void Rearrange(bool resetSizes)
    {
        if (ActiveMode == null) return;
        foreach (var o in openItems) saved.Remove(Key(ActiveMode, o.item.id));
        // Lay out at the size each item ends up: where a running animation was taking it, else as it is now (the user
        // may have resized it), or for Reset, as it started. Settle then animates from what's on screen.
        foreach (var o in openItems)
        {
            if (o.go == null) continue;
            Transform t = o.go.transform;
            o.shownScale = t.localScale;
            if (o.motion == null) o.restScale = o.shownScale;
            if (resetSizes && o.volume != null) o.restScale = Vector3.one * o.defaultScale;
            t.localScale = o.restScale;
            if (o.panel == null) continue;
            o.shownSize = o.panel.SizeMeters;
            if (o.motion == null) o.restSize = o.shownSize;
            if (resetSizes) o.restSize = o.defaultSize;
            o.panel.SizeMeters = o.restSize;
        }
        Vector3 eye = ClampEye(presence.EyeInFrame());
        LayOut(eye);
        foreach (var o in openItems)
        {
            if (o.go == null) continue;
            if (o.handles != null) o.handles.SetPlacement(o.facing, byUser: false);
            if (o.grips != null) o.grips.SetPlacement(byUser: false);
            // Caught mid-flight, it carries straight on; the rest go centre out.
            float delay = o.motion != null ? 0f : StaggerIndex(o, outermostFirst: false) * ForgeTheme.LayoutStagger;
            Settle(o, delay, eye);
        }
    }

    // ---- Motion ----
    // Each item runs at most one tween (OpenItem.motion). A new one stops the old and starts from whatever is on screen,
    // so opening another mode, Reset or walking away mid-animation never leaves an item half-grown or Busy.

    // Spring shapes for ForgeTween.SpringDamped (damping, oscillations per tween).
    const float RiseFrequency = 1.6f, SwingFrequency = 1.15f;  // unfold, critically damped: height and turn lead the swing
    const float GrowDamping = 0.74f, GrowFrequency = 1.35f;    // unfold scale: one soft ~3% overshoot, no wobble
    const float SettleDamping = 0.9f, SettleFrequency = 1.3f;  // re-layout: next to no overshoot
    const float SeedScale = 0.01f;  // volumes have no fade, so they start (and fold down to) smaller than panels

    // Unfold: a panel comes out of the ring that was pressed, tiny, see-through and lying flat like the ring, then rises
    // and swings round and out to its place, turning to face you; it grows with one soft overshoot and fades in early.
    // A floating volume comes out the same way, upright. A desk volume grows out of the desk where it stands instead:
    // its base spreads, then the model rises out of it.
    void Unfold(OpenItem o, string modeId, float delay, Vector3 eye)
    {
        StopMotion(o);
        Transform t = o.go.transform;
        o.restScale = t.localScale;
        if (o.panel != null) o.restSize = o.panel.SizeMeters;
        Vector3 pos = o.position, scale = o.restScale;
        Quaternion rot = o.rotation;
        SetBusy(o, true);

        if (o.panel == null && IsDeskSlot(o.item.slot))
        {
            t.localPosition = pos;
            t.localRotation = rot;
            t.localScale = scale * SeedScale;
            o.motion = StartCoroutine(ForgeTween.Run(ForgeTheme.UnfoldTime, delay, k =>
            {
                if (t == null) return;
                float spread = ForgeTween.SpringDamped(Mathf.InverseLerp(0f, 0.7f, k), 1f, 1.1f);
                float rise = ForgeTween.SpringDamped(Mathf.InverseLerp(0.15f, 1f, k), GrowDamping, GrowFrequency);
                spread = Mathf.LerpUnclamped(SeedScale, 1f, spread);
                rise = Mathf.LerpUnclamped(SeedScale, 1f, rise);
                t.localScale = Vector3.Scale(scale, new Vector3(spread, rise, spread));
            }, () => Landed(o)));
            return;
        }

        CanvasGroup group = Group(o);
        float seed = group != null ? ForgeTheme.UnfoldScale : SeedScale;
        Vector3 from = RingPoint(modeId, DeskEdge(pos));
        Quaternion fromRotation = o.panel != null ? Quaternion.LookRotation(Vector3.down, Vector3.forward) : rot;
        t.localPosition = from;
        t.localRotation = fromRotation;
        t.localScale = scale * seed;
        if (group != null) group.alpha = 0f;
        o.motion = StartCoroutine(ForgeTween.Run(ForgeTheme.UnfoldTime, delay, k =>
        {
            if (t == null) return;
            float rise = ForgeTween.SpringDamped(k, 1f, RiseFrequency);
            t.localPosition = ForgeTween.Orbit(eye, from, pos, ForgeTween.SpringDamped(k, 1f, SwingFrequency), rise);
            t.localRotation = Quaternion.SlerpUnclamped(fromRotation, rot, rise);
            t.localScale = scale * Mathf.LerpUnclamped(seed, 1f, ForgeTween.SpringDamped(k, GrowDamping, GrowFrequency));
            if (group != null) group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, ForgeTheme.UnfoldFade, k));
        }, () => Landed(o)));
    }

    // Fold the open mode away into its ring, outermost first, remembering what the user moved, resized or turned to put
    // it back next time this mode opens.
    void FoldAll()
    {
        if (openItems.Count == 0) return;
        string modeId = ActiveMode;
        Vector3 eye = ClampEye(presence.EyeInFrame());
        foreach (var o in openItems)
        {
            if (o.go == null) continue;
            bool placed = (o.handles != null && o.handles.UserPlaced) || (o.grips != null && o.grips.UserPlaced);
            if (modeId != null && placed) saved[Key(modeId, o.item.id)] = CaptureSettled(o);
            if (o.handles != null) o.handles.Cancel();
            if (o.grips != null) o.grips.Cancel();
            // Caught mid-flight, it turns straight back; the rest go outermost first.
            float delay = o.motion != null ? 0f : StaggerIndex(o, outermostFirst: true) * ForgeTheme.FoldStagger;
            Fold(o, modeId, delay, eye);
        }
        openItems.Clear();
    }

    // Fold: quicker than the unfold, and the other way round. A panel swings in, lies down and shrinks, fading out over
    // the second half, and sinks into the ring last (following it down as it lowers). A desk volume's model sinks into
    // its base, which then draws in. Destroyed at the end.
    void Fold(OpenItem o, string modeId, float delay, Vector3 eye)
    {
        StopMotion(o);
        SetBusy(o, true);
        GameObject go = o.go;
        Transform t = go.transform;
        Vector3 p0 = t.localPosition, s0 = t.localScale;
        Quaternion r0 = t.localRotation;

        if (o.panel == null && IsDeskSlot(o.item.slot))
        {
            o.motion = StartCoroutine(ForgeTween.Run(ForgeTheme.FoldTime, delay, k =>
            {
                if (t == null) return;
                float sink = Mathf.LerpUnclamped(1f, SeedScale, ForgeTween.EaseInOutCubic(Mathf.InverseLerp(0f, 0.65f, k)));
                float draw = Mathf.LerpUnclamped(1f, SeedScale, ForgeTween.EaseInOutCubic(Mathf.InverseLerp(0.3f, 1f, k)));
                t.localScale = Vector3.Scale(s0, new Vector3(draw, sink, draw));
            }, () => { if (go != null) Destroy(go); }));
            return;
        }

        CanvasGroup group = Group(o);
        float a0 = group != null ? group.alpha : 1f;
        if (group != null) group.blocksRaycasts = false;  // on its way out: clicks go past it
        float end = group != null ? ForgeTheme.UnfoldScale : SeedScale;
        Quaternion r1 = o.panel != null ? Quaternion.LookRotation(Vector3.down, Vector3.forward) : r0;
        Vector3 ring = RingPoint(modeId, DeskEdge(p0));
        o.motion = StartCoroutine(ForgeTween.Run(ForgeTheme.FoldTime, delay, k =>
        {
            if (t == null) return;
            ring = RingPoint(modeId, ring);
            float swing = ForgeTween.EaseInOutCubic(k);
            t.localPosition = ForgeTween.Orbit(eye, p0, ring, swing, ForgeTween.EaseInOutCubic(Mathf.InverseLerp(0.15f, 1f, k)));
            t.localRotation = Quaternion.SlerpUnclamped(r0, r1, swing);
            t.localScale = s0 * Mathf.LerpUnclamped(1f, end, swing);
            if (group != null) group.alpha = a0 * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, k)));
        }, () => { if (go != null) Destroy(go); }));
    }

    // Re-layout: from wherever it is (even caught mid-unfold) to its slot, its rest scale and size, fully shown.
    void Settle(OpenItem o, float delay, Vector3 eye)
    {
        StopMotion(o);
        Transform t = o.go.transform;
        ForgePanel panel = o.panel;
        t.localScale = o.shownScale;
        if (panel != null) panel.SizeMeters = o.shownSize;
        Vector3 p0 = t.localPosition, p1 = o.position, s0 = o.shownScale, s1 = o.restScale;
        Quaternion r0 = t.localRotation, r1 = o.rotation;
        Vector2 z0 = o.shownSize, z1 = o.restSize;
        bool resize = panel != null && (z1 - z0).sqrMagnitude > 1e-8f;
        CanvasGroup group = Group(o);
        float a0 = group != null ? group.alpha : 1f;
        SetBusy(o, true);
        o.motion = StartCoroutine(ForgeTween.Run(ForgeTheme.LayoutTime, delay, k =>
        {
            if (t == null) return;
            float e = ForgeTween.SpringDamped(k, SettleDamping, SettleFrequency);
            t.localPosition = ForgeTween.Orbit(eye, p0, p1, e, e);
            t.localRotation = Quaternion.SlerpUnclamped(r0, r1, e);
            t.localScale = Vector3.LerpUnclamped(s0, s1, e);
            if (resize) panel.SizeMeters = Vector2.LerpUnclamped(z0, z1, e);
            if (group != null) group.alpha = Mathf.Lerp(a0, 1f, e);
        }, () => Landed(o)));
    }

    // At rest: exactly its size, fully shown, and grabs work again.
    static void Landed(OpenItem o)
    {
        o.motion = null;
        if (o.go != null) o.go.transform.localScale = o.restScale;
        if (o.group != null) o.group.alpha = 1f;
        SetBusy(o, false);
    }

    // Stop whatever the item was doing; the next motion starts from wherever that left it.
    void StopMotion(OpenItem o)
    {
        if (o.motion != null) StopCoroutine(o.motion);
        o.motion = null;
    }

    // A panel fades as a whole, frame and content together, through a CanvasGroup on it.
    static CanvasGroup Group(OpenItem o)
    {
        if (o.group != null || o.panel == null) return o.group;
        o.group = o.panel.GetComponent<CanvasGroup>();
        if (o.group == null) o.group = o.panel.gameObject.AddComponent<CanvasGroup>();
        return o.group;
    }

    // The mode's dock ring in the desk frame; `fallback` while the dock isn't showing.
    Vector3 RingPoint(string modeId, Vector3 fallback)
    {
        if (dock == null || modeId == null || !dock.TryGetRingPoint(modeId, out Vector3 world)) return fallback;
        return presence.Frame.InverseTransformPoint(world);
    }

    // Where items come out of with no ring to use: the desk's front edge, toward their side.
    static Vector3 DeskEdge(Vector3 pos) => new Vector3(Mathf.Clamp(pos.x * 0.3f, -0.25f, 0.25f), 0.02f, 0.12f);

    // Centre out: Main, then what's beside it (and the desk volume under it), then the outer pair, then Above.
    static int Tier(WorkspaceSlot slot)
    {
        switch (slot)
        {
            case WorkspaceSlot.Main: return 0;
            case WorkspaceSlot.Left:
            case WorkspaceSlot.Right:
            case WorkspaceSlot.DeskCenter: return 1;
            case WorkspaceSlot.Above: return 3;
            default: return 2;  // FarLeft, FarRight, DeskLeft, DeskRight
        }
    }

    // An item's turn in the stagger: how many of the open items' tiers come before its own (after it, folding).
    int StaggerIndex(OpenItem o, bool outermostFirst)
    {
        int used = 0;
        foreach (var other in openItems) used |= 1 << Tier(other.item.slot);
        int tier = Tier(o.item.slot), index = 0;
        for (int i = 0; i < 4; i++)
            if ((used & (1 << i)) != 0 && (outermostFirst ? i > tier : i < tier)) index++;
        return index;
    }

    // What to remember of an item: where the user left it, or if it's still unfolding, where it was going.
    static SavedPose CaptureSettled(OpenItem o)
    {
        SavedPose pose = Capture(o);
        if (o.motion == null) return pose;
        pose.position = o.position;
        pose.rotation = o.rotation;
        pose.scale = o.restScale.x;
        if (o.panel != null) pose.size = o.restSize;
        return pose;
    }

    OpenItem Spawn(WorkspaceItem item, WorkspaceMode mode)
    {
        var o = new OpenItem { item = item };
        string title = string.IsNullOrEmpty(item.title) ? item.id : item.title;
        if (item.prefab != null)
        {
            o.go = Instantiate(item.prefab);
            o.panel = o.go.GetComponentInChildren<ForgePanel>();
            if (o.panel != null && !string.IsNullOrEmpty(item.title)) o.panel.Title = item.title;
            o.volume = o.go.GetComponentInChildren<ForgeVolume>();
            if (o.volume != null && !string.IsNullOrEmpty(item.title)) o.volume.Title = item.title;
            // Handles move the panel's own transform, so only a panel at the prefab's root can have them.
            if (o.panel != null && o.panel.gameObject != o.go)
            {
                Debug.LogWarning($"[Workspace] {item.id}: ForgePanel isn't the prefab root, so it can't be moved or resized.");
                o.panel = null;
            }
        }
        else if (item.kind == WorkspaceItemKind.Panel)
        {
            o.panel = Instantiate(panelPrefab);
            o.panel.panelId = item.id;
            o.panel.Title = title;
            o.panel.SizeMeters = PanelSize(item.slot);
            o.panel.name = $"Panel {item.id}";
            o.go = o.panel.gameObject;
        }
        else
        {
            var volume = Instantiate(volumePrefab);
            volume.volumeId = item.id;
            volume.Title = title;
            volume.name = $"Volume {item.id}";
            float s = IsDeskSlot(item.slot) ? 0.22f : 0.3f;
            volume.SetSize(new Vector3(s, s, s));
            // Until the module's prefab is assigned: an empty lit base with its label and what will stand on it.
            volume.SetCaption(PlaceholderCaption(item.id));
            o.volume = volume;
            o.go = volume.gameObject;
        }

        o.go.transform.SetParent(presence.Frame, false);
        if (o.panel != null)
        {
            o.panel.SetBadge(mode.label, mode.color);
            if (item.prefab == null) o.panel.ShowEmptyState(title, PlaceholderCaption(item.id));
            o.defaultSize = o.panel.SizeMeters;
            o.handles = o.panel.GetComponent<PanelManipulator>();
            if (o.handles == null) o.handles = o.panel.gameObject.AddComponent<PanelManipulator>();
            o.handles.Init(presence.head, presence.Frame);
        }
        else if (o.volume != null && o.volume.gameObject == o.go)
        {
            o.volume.SetAccent(mode.color);
            o.defaultScale = o.go.transform.localScale.x;
            o.grips = o.go.GetComponent<VolumeManipulator>();
            if (o.grips == null) o.grips = o.go.AddComponent<VolumeManipulator>();
            o.grips.Init(presence.head, presence.Frame);
        }
        return o;
    }

    // Grabs wait while the workspace animates an item.
    static void SetBusy(OpenItem o, bool busy)
    {
        if (o.handles != null) o.handles.Busy = busy;
        if (o.grips != null) o.grips.Busy = busy;
    }

    static SavedPose Capture(OpenItem o)
    {
        Transform t = o.go.transform;
        var pose = new SavedPose { position = t.localPosition, rotation = t.localRotation, scale = t.localScale.x };
        if (o.panel != null)
        {
            pose.size = o.panel.SizeMeters;
            pose.facing = o.handles.Facing;
        }
        if (o.volume != null)
        {
            pose.turned = !o.volume.turntable;
            pose.viewYaw = o.volume.ViewYaw;
            pose.viewTilt = o.volume.ViewTilt;
        }
        return pose;
    }

    static void Restore(OpenItem o, SavedPose pose)
    {
        o.position = pose.position;
        o.rotation = pose.rotation;
        if (o.panel != null)
        {
            o.panel.SizeMeters = pose.size;
            o.facing = pose.facing;
        }
        if (o.volume != null)
        {
            o.go.transform.localScale = Vector3.one * pose.scale;
            if (pose.turned) o.volume.SetView(pose.viewYaw, pose.viewTilt);
        }
    }

    // Default poses for every open item. Side slots go outward from Main in order (Right then FarRight, Left then
    // FarLeft), each at least at its nominal angle and far enough out not to overlap its neighbour.
    void LayOut(Vector3 eye)
    {
        // The desk first: the main panel stands behind what's in the middle of it (CAD's model), not through it.
        foreach (var o in openItems)
        {
            if (IsDeskSlot(o.item.slot))
            {
                WorkspaceSlot slot = o.item.slot;
                float x = Mathf.Min(0.3f, Mathf.Max(0f, presence.FrameWidth * 0.5f - 0.12f));
                float z = Mathf.Clamp(presence.FrameDepth * 0.5f, 0.12f, 0.3f);
                // Far enough back that the base and its label clear the dock, but still on the desk.
                float depth = o.volume != null ? o.volume.size.z * o.go.transform.localScale.z : 0.3f;
                float clear = dock.inset + ForgeTheme.DockHeight * 0.0005f + 0.06f + depth * 0.5f;
                z = Mathf.Min(Mathf.Max(z, clear), Mathf.Max(0.12f, presence.FrameDepth - depth * 0.5f));
                o.position = new Vector3(slot == WorkspaceSlot.DeskLeft ? -x : slot == WorkspaceSlot.DeskRight ? x : 0f, 0f, z);
                o.rotation = Quaternion.LookRotation(Flat(o.position - eye), Vector3.up);
                o.facing = PanelFacing.FaceMe;
            }
        }

        OpenItem main = Find(WorkspaceSlot.Main);
        float mainY = 0f;
        if (main != null) mainY = PlaceInRow(main, eye, 0f, -0.08f, BehindDesk(eye));
        PlaceSide(eye, main, +1, WorkspaceSlot.Right, WorkspaceSlot.FarRight);
        PlaceSide(eye, main, -1, WorkspaceSlot.Left, WorkspaceSlot.FarLeft);

        foreach (var o in openItems)
        {
            if (o.item.slot == WorkspaceSlot.Above)
            {
                // Clear of the main panel's top edge.
                float up = 0.36f;
                if (main != null) up = Mathf.Max(up, mainY - eye.y + Size(main).y * 0.5f + panelGap + Size(o).y * 0.5f);
                PlaceInRow(o, eye, 0f, up, layout == WorkspaceLayout.Curved ? 0.05f : 0f);
            }
        }

        // Out of the room's walls (RoomFit): what fits keeps its place, and the rest come closer or toward the middle,
        // clear of those if they can.
        if (RoomFit.Begin(presence.Frame, eye))
        {
            foreach (var o in openItems)
                if (Floats(o) && !RoomFit.InWall(o.position, o.rotation, FitSize(o), o.panel == null))
                    RoomFit.Take(o.position, FitSize(o), o.panel == null);
            foreach (var o in openItems)
                if (Floats(o) && RoomFit.InWall(o.position, o.rotation, FitSize(o), o.panel == null))
                    RoomFit.Fit(ref o.position, ref o.rotation, FitSize(o), o.panel == null, layout == WorkspaceLayout.Curved);
        }
    }

    // How much further than panelRadius the main panel goes to stand behind the volume in the middle of the desk (its
    // back plus a gap), along the row's depth: the distance from the eyes (curved) or straight ahead (flat).
    float BehindDesk(Vector3 eye)
    {
        OpenItem desk = Find(WorkspaceSlot.DeskCenter);
        if (desk == null || desk.volume == null) return 0f;
        Vector3 d = desk.position - eye;
        float along = layout == WorkspaceLayout.Curved ? new Vector2(d.x, d.z).magnitude : d.z;
        float back = along + desk.volume.size.z * desk.go.transform.localScale.z * 0.5f + panelGap;
        return Mathf.Max(0f, back - panelRadius);
    }

    // Panels and volumes in the air (not the ones standing on the desk).
    static bool Floats(OpenItem o) => o.go != null && !IsDeskSlot(o.item.slot);

    // What RoomFit tests: a panel's width and height, a volume's box at its scale.
    static Vector3 FitSize(OpenItem o)
    {
        if (o.panel != null) return o.panel.SizeMeters;
        if (o.volume != null) return Vector3.Scale(o.volume.size, o.go.transform.localScale);
        return new Vector3(0.3f, 0.3f, 0.3f);
    }

    // What a placeholder panel or volume will show once its module is plugged in (its empty state's caption).
    static string PlaceholderCaption(string itemId)
    {
        switch (itemId)
        {
            case "ml-training": return "Loss and accuracy curves draw here while a model trains.";
            case "ml-results": return "Predictions, mistakes and the confusion matrix land here after a run.";
            case "ml-dataset":
            case "ml-datastack": return "The dataset, as a stack of photos";
            case "ml-model3d": return "The model's layers, as blocks";
            case "cad-timeline": return "Every version of the design, newest first.";
            case "cad-drawing": return "The design's engineering drawing: views, dimensions, parts list.";
            case "messages": return "Slack messages land here, with replies Albie drafts for you.";
            case "screen-left":
            case "screen-right": return "Share a window from Screen Bridge to see it here.";
            default: return "Shows up here once its module is plugged in.";
        }
    }

    void PlaceSide(Vector3 eye, OpenItem main, int side, WorkspaceSlot near, WorkspaceSlot far)
    {
        float r = panelRadius;
        bool curved = layout == WorkspaceLayout.Curved;
        float mainHalf = main != null ? Size(main).x * 0.5f : 0f;
        // Curved: angle (deg) of the outer edge placed so far; flat: its x offset (m).
        float edge = curved ? Mathf.Atan2(mainHalf, r) * Mathf.Rad2Deg : mainHalf;
        float gap = curved ? panelGap / r * Mathf.Rad2Deg : panelGap;
        for (int i = 0; i < 2; i++)
        {
            OpenItem o = Find(i == 0 ? near : far);
            if (o == null) continue;
            float half = curved ? Mathf.Atan2(Size(o).x * 0.5f, r) * Mathf.Rad2Deg : Size(o).x * 0.5f;
            float along = edge + gap + half;
            if (curved) along = Mathf.Max(i == 0 ? sideAngle : farAngle, along);
            PlaceInRow(o, eye, along * side, -0.08f);
            edge = along + half;
        }
        sideEdge[side > 0 ? 1 : 0] = edge;
    }

    // Place an item in the row around the eyes: `along` is an angle (deg, curved) or an x offset (m, flat), `up`
    // is the height above the eyes. Panels stay clear of the desk top. Returns the item's height in the desk frame.
    float PlaceInRow(OpenItem o, Vector3 eye, float along, float up, float extraRadius = 0f)
    {
        RowPose(eye, along, up, Size(o), o.panel != null, extraRadius, out o.position, out o.rotation);
        o.facing = layout == WorkspaceLayout.Curved ? PanelFacing.FaceMe : PanelFacing.Straight;
        return o.position.y;
    }

    // The pose of a slot in the row (see PlaceInRow) for a panel or a volume of `size`.
    void RowPose(Vector3 eye, float along, float up, Vector2 size, bool isPanel, float extraRadius, out Vector3 pos, out Quaternion rot)
    {
        float r = panelRadius + extraRadius;
        bool curved = layout == WorkspaceLayout.Curved;
        if (curved)
        {
            float a = along * Mathf.Deg2Rad;
            pos = eye + new Vector3(Mathf.Sin(a) * r, up, Mathf.Cos(a) * r);
        }
        else
        {
            pos = eye + new Vector3(along, up, r);
        }

        if (!isPanel)
        {
            // Floating volume: base below the slot center, turned to face the user (curved) or straight (flat).
            pos.y = Mathf.Max(pos.y - 0.15f, 0.02f);
            rot = curved ? Quaternion.LookRotation(Flat(pos - eye), Vector3.up) : Quaternion.identity;
        }
        else
        {
            pos.y = Mathf.Max(pos.y, size.y * 0.5f + 0.03f);
            // Panel faces the eyes (a canvas reads correctly when its +Z points away from the viewer).
            rot = curved ? Quaternion.LookRotation(pos - eye, Vector3.up) : Quaternion.identity;
        }
    }

    // ---- For things laid out with the open mode that aren't in it (WorkspaceExtras) ----

    /// <summary>The eyes in the desk frame as the layout uses them (clamped to a sane seat).</summary>
    public Vector3 LayoutEye => ClampEye(presence.EyeInFrame());

    /// <summary>
    /// The middle of the top edge of the open mode's highest middle panel (Above, else Main) and its rotation, in the
    /// desk frame, where it's laid out. No mode open: where Main's top edge would be.
    /// </summary>
    public void TopEdge(out Vector3 position, out Quaternion rotation)
    {
        OpenItem top = Find(WorkspaceSlot.Above);
        if (top == null || top.panel == null) top = Find(WorkspaceSlot.Main);
        Vector2 size;
        if (top != null && top.panel != null)
        {
            size = top.motion != null ? top.restSize : top.panel.SizeMeters;
            position = top.position;
            rotation = top.rotation;
        }
        else
        {
            size = PanelSize(WorkspaceSlot.Main);
            RowPose(LayoutEye, 0f, -0.08f, size, true, 0f, out position, out rotation);
        }
        position += rotation * Vector3.up * (size.y * 0.5f);
    }

    /// <summary>
    /// Where a panel of <paramref name="size"/> goes just outside what the open mode has on one side (+1 right,
    /// -1 left), in the row's next place out, a little below the eyes. Desk frame.
    /// </summary>
    public void BesidePose(int side, Vector2 size, out Vector3 position, out Quaternion rotation)
    {
        bool curved = layout == WorkspaceLayout.Curved;
        float r = panelRadius;
        float edge = ActiveMode != null ? sideEdge[side > 0 ? 1 : 0] : 0f;
        float gap = 2f * (curved ? panelGap / r * Mathf.Rad2Deg : panelGap);  // a little apart: it isn't one of the mode's
        float half = curved ? Mathf.Atan2(size.x * 0.5f, r) * Mathf.Rad2Deg : size.x * 0.5f;
        float along = edge + gap + half;
        if (curved) along = Mathf.Max(sideAngle, along);
        RowPose(LayoutEye, along * (side > 0 ? 1f : -1f), -0.05f, size, true, 0f, out position, out rotation);
    }

    OpenItem Find(WorkspaceSlot slot)
    {
        foreach (var o in openItems) if (o.item.slot == slot) return o;
        return null;
    }

    static Vector2 Size(OpenItem o)
    {
        if (o.panel != null) return o.panel.SizeMeters;
        if (o.volume != null) return new Vector2(o.volume.size.x, o.volume.size.y);
        return new Vector2(0.3f, 0.3f);
    }

    static string Key(string modeId, string itemId) => modeId + "/" + itemId;

    static Vector2 PanelSize(WorkspaceSlot slot)
    {
        switch (slot)
        {
            case WorkspaceSlot.Main: return new Vector2(0.9f, 0.55f);
            case WorkspaceSlot.Above: return new Vector2(0.8f, 0.28f);
            case WorkspaceSlot.FarLeft:
            case WorkspaceSlot.FarRight: return new Vector2(0.45f, 0.4f);
            default: return new Vector2(0.55f, 0.42f);
        }
    }

    static bool IsDeskSlot(WorkspaceSlot slot) =>
        slot == WorkspaceSlot.DeskCenter || slot == WorkspaceSlot.DeskLeft || slot == WorkspaceSlot.DeskRight;

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v : Vector3.forward;
    }

    // Keep the layout sane if the head is far from a normal seat (e.g. forced seated from across the room).
    static Vector3 ClampEye(Vector3 eye) =>
        new Vector3(Mathf.Clamp(eye.x, -0.3f, 0.3f), Mathf.Clamp(eye.y, 0.3f, 1.0f), Mathf.Clamp(eye.z, -0.8f, -0.15f));

    public static List<WorkspaceMode> DefaultModes() => new List<WorkspaceMode>
    {
        new WorkspaceMode
        {
            id = "ml", label = "ML", color = ForgeTheme.ML,
            items =
            {
                new WorkspaceItem("screen-main", "VS Code", WorkspaceSlot.Main),
                new WorkspaceItem("ml-training", "Training", WorkspaceSlot.Above),
                // Panels, like the ML prefabs (ML/Resources/MLPanels, all ForgePanel variants); ids per section 7.
                new WorkspaceItem("ml-datastack", "Dataset", WorkspaceSlot.FarLeft),
                new WorkspaceItem("ml-model3d", "Model", WorkspaceSlot.Left),
                new WorkspaceItem("ml-results", "Results", WorkspaceSlot.Right),
            },
        },
        new WorkspaceMode
        {
            id = "cad", label = "CAD", color = ForgeTheme.CAD,
            items =
            {
                new WorkspaceItem("cad-drawing", "Drawing", WorkspaceSlot.Main),
                new WorkspaceItem("cad-viewer", "Rocket", WorkspaceSlot.DeskCenter, WorkspaceItemKind.Volume),
                new WorkspaceItem("cad-params", "Parameters", WorkspaceSlot.Right),
                new WorkspaceItem("cad-timeline", "Versions", WorkspaceSlot.Left),
            },
        },
        // The landing mode: just the editor. The pet opens it on START, after the arrival greeting.
        new WorkspaceMode
        {
            id = "code", label = "CODE", color = ForgeTheme.Code,
            items = { new WorkspaceItem("screen-main", "VS Code", WorkspaceSlot.Main) },
        },
        new WorkspaceMode
        {
            id = "messages", label = "MSG", color = ForgeTheme.Messages,
            items =
            {
                // The Slack panel (conversations, messages, composer) and the "New for you" list beside it
                // (Messages/Resources/SlackPanel.prefab, MentionsPanel.prefab; ids per section 7).
                new WorkspaceItem("messages", "Slack", WorkspaceSlot.Main),
                new WorkspaceItem("messages-mentions", "Mentions", WorkspaceSlot.Left),
            },
        },
    };
}
