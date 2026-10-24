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

    /// <summary>Id of the open mode, or null when everything is folded.</summary>
    public string ActiveMode { get; private set; }

    /// <summary>The scene's workspace (one per scene), for modules that would otherwise FindAnyObjectByType.</summary>
    public static Workspace Current { get; private set; }

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
            Close();
            dock.Hide();
        }
        else if (!dock.IsShown)
        {
            dock.Show(presence.Frame, presence.FrameWidth, presence.FrameDepth);
        }
    }

    // The user went round to another side of the desk: move the dock to the edge they're at.
    void OnFrameChanged()
    {
        if (dock.IsShown) dock.Show(presence.Frame, presence.FrameWidth, presence.FrameDepth);
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

        FoldAll();
        foreach (var item in mode.items) openItems.Add(Spawn(item, mode));
        LayOut(ClampEye(presence.EyeInFrame()));
        for (int i = 0; i < openItems.Count; i++)
        {
            var o = openItems[i];
            bool restored = saved.TryGetValue(Key(modeId, o.item.id), out SavedPose pose);
            if (restored) Restore(o, pose);
            if (o.handles != null) o.handles.SetPlacement(o.facing, byUser: restored);
            if (o.grips != null) o.grips.SetPlacement(byUser: restored);
            Unfold(o, i * 0.08f);
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

    // Forget the open mode's saved poses and animate every item to its slot in the current layout.
    void Rearrange(bool resetSizes)
    {
        if (ActiveMode == null) return;
        foreach (var o in openItems) saved.Remove(Key(ActiveMode, o.item.id));
        if (resetSizes)
            foreach (var o in openItems)
            {
                if (o.panel != null) o.panel.SizeMeters = o.defaultSize;
                if (o.volume != null) o.go.transform.localScale = Vector3.one * o.defaultScale;
            }
        LayOut(ClampEye(presence.EyeInFrame()));
        foreach (var o in openItems)
        {
            if (o.go == null) continue;
            Transform t = o.go.transform;
            Vector3 p0 = t.localPosition, p1 = o.position;
            Quaternion r0 = t.localRotation, r1 = o.rotation;
            if (o.handles != null) o.handles.SetPlacement(o.facing, byUser: false);
            if (o.grips != null) o.grips.SetPlacement(byUser: false);
            SetBusy(o, true);
            StartCoroutine(ForgeTween.Run(0.35f, 0f, k =>
            {
                if (t == null) return;
                float e = ForgeTween.EaseOutCubic(k);
                t.localPosition = Vector3.LerpUnclamped(p0, p1, e);
                t.localRotation = Quaternion.SlerpUnclamped(r0, r1, e);
            }, () => SetBusy(o, false)));
        }
    }

    void Unfold(OpenItem o, float delay)
    {
        Transform t = o.go.transform;
        Vector3 pos = o.position;
        Quaternion rot = o.rotation;
        // Rise out of the desk, near the front edge, lying flat.
        Vector3 startPos = new Vector3(Mathf.Clamp(pos.x * 0.3f, -0.25f, 0.25f), 0.02f, 0.12f);
        Quaternion startRot = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        Vector3 endScale = t.localScale;

        t.localPosition = startPos;
        t.localRotation = startRot;
        t.localScale = endScale * 0.02f;
        SetBusy(o, true);
        StartCoroutine(ForgeTween.Run(0.45f, delay, k =>
        {
            if (t == null) return;
            float e = ForgeTween.EaseOutCubic(k);
            t.localPosition = Vector3.LerpUnclamped(startPos, pos, e);
            t.localRotation = Quaternion.SlerpUnclamped(startRot, rot, e);
            t.localScale = endScale * Mathf.Max(0.02f, ForgeTween.EaseOutBack(k));
        }, () => SetBusy(o, false)));
    }

    void FoldAll()
    {
        foreach (var o in openItems)
        {
            if (o.go == null) continue;
            // Remember what the user moved, resized or turned, to put it back next time this mode opens.
            bool placed = (o.handles != null && o.handles.UserPlaced) || (o.grips != null && o.grips.UserPlaced);
            if (ActiveMode != null && placed) saved[Key(ActiveMode, o.item.id)] = Capture(o);
            if (o.handles != null) o.handles.Cancel();
            if (o.grips != null) o.grips.Cancel();
            SetBusy(o, true);

            GameObject go = o.go;
            Transform t = go.transform;
            Vector3 p0 = t.localPosition, s0 = t.localScale;
            Vector3 p1 = new Vector3(Mathf.Clamp(p0.x * 0.3f, -0.25f, 0.25f), 0.02f, 0.12f);
            StartCoroutine(ForgeTween.Run(0.25f, 0f, k =>
            {
                if (t == null) return;
                float e = ForgeTween.EaseInCubic(k);
                t.localPosition = Vector3.Lerp(p0, p1, e);
                t.localScale = s0 * (1f - e * 0.98f);
            }, () => { if (go != null) Destroy(go); }));
        }
        openItems.Clear();
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
            // Placeholder content until the module's prefab is assigned.
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(cube.GetComponent<Collider>());
            cube.transform.localRotation = Quaternion.Euler(20f, 30f, 10f);
            volume.SetContent(cube);
            volume.turntable = true;
            o.volume = volume;
            o.go = volume.gameObject;
        }

        o.go.transform.SetParent(presence.Frame, false);
        if (o.panel != null)
        {
            o.panel.SetBadge(mode.label, mode.color);
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
        OpenItem main = Find(WorkspaceSlot.Main);
        float mainY = 0f;
        if (main != null) mainY = PlaceInRow(main, eye, 0f, -0.08f);
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
            else if (IsDeskSlot(o.item.slot))
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
    }

    // Place an item in the row around the eyes: `along` is an angle (deg, curved) or an x offset (m, flat), `up`
    // is the height above the eyes. Panels stay clear of the desk top. Returns the item's height in the desk frame.
    float PlaceInRow(OpenItem o, Vector3 eye, float along, float up, float extraRadius = 0f)
    {
        float r = panelRadius + extraRadius;
        bool curved = layout == WorkspaceLayout.Curved;
        Vector3 pos;
        if (curved)
        {
            float a = along * Mathf.Deg2Rad;
            pos = eye + new Vector3(Mathf.Sin(a) * r, up, Mathf.Cos(a) * r);
        }
        else
        {
            pos = eye + new Vector3(along, up, r);
        }

        if (o.panel == null)
        {
            // Floating volume: base below the slot center, turned to face the user (curved) or straight (flat).
            pos.y = Mathf.Max(pos.y - 0.15f, 0.02f);
            o.rotation = curved ? Quaternion.LookRotation(Flat(pos - eye), Vector3.up) : Quaternion.identity;
        }
        else
        {
            pos.y = Mathf.Max(pos.y, Size(o).y * 0.5f + 0.03f);
            // Panel faces the eyes (a canvas reads correctly when its +Z points away from the viewer).
            o.rotation = curved ? Quaternion.LookRotation(pos - eye, Vector3.up) : Quaternion.identity;
        }
        o.position = pos;
        o.facing = curved ? PanelFacing.FaceMe : PanelFacing.Straight;
        return pos.y;
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
                new WorkspaceItem("screen-main", "rocket.py", WorkspaceSlot.Main),
                new WorkspaceItem("cad-viewer", "Rocket", WorkspaceSlot.DeskCenter, WorkspaceItemKind.Volume),
                new WorkspaceItem("cad-params", "Parameters", WorkspaceSlot.Right),
                new WorkspaceItem("cad-timeline", "Versions", WorkspaceSlot.Left),
            },
        },
        // The landing mode: just the editor. The pet opens it after the sit-down digest.
        new WorkspaceMode
        {
            id = "code", label = "CODE", color = ForgeTheme.Code,
            items = { new WorkspaceItem("screen-main", "VS Code", WorkspaceSlot.Main) },
        },
        new WorkspaceMode
        {
            id = "messages", label = "MSG", color = ForgeTheme.Messages,
            items = { new WorkspaceItem("messages", "Messages", WorkspaceSlot.Right) },
        },
    };
}
