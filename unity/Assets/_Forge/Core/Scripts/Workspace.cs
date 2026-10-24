using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public enum WorkspaceSlot { Main, Left, Right, FarLeft, FarRight, Above, DeskCenter, DeskLeft, DeskRight }
public enum WorkspaceItemKind { Panel, Volume }

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
/// the desk; opening a mode unfolds its panels in an arc around the user's eyes and its volumes on the desk.
/// Slots are laid out in the desk frame (front edge, +Z into the desk), so they follow whichever table is used.
/// Pet / voice: call <see cref="Open"/>, <see cref="Toggle"/> or <see cref="Close"/> with a mode id.
/// </summary>
public class Workspace : MonoBehaviour
{
    public DeskPresence presence;
    public DeskDock dock;
    public ForgePanel panelPrefab;
    public ForgeVolume volumePrefab;
    public List<WorkspaceMode> modes = new List<WorkspaceMode>();

    public UnityEvent<string> modeChanged = new UnityEvent<string>();

    [Header("Layout (m)")]
    public float panelRadius = 0.8f;
    public float sideAngle = 48f;
    public float farAngle = 90f;

    readonly List<GameObject> openItems = new List<GameObject>();

    public string ActiveMode { get; private set; }

    void Reset() => modes = DefaultModes();

    void OnEnable()
    {
        presence.stateChanged.AddListener(OnDeskState);
        presence.frameChanged.AddListener(OnFrameChanged);
    }

    void OnDisable()
    {
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
        Vector3 eye = ClampEye(presence.EyeInFrame());
        for (int i = 0; i < mode.items.Count; i++)
            openItems.Add(Unfold(mode.items[i], eye, i * 0.08f));

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

    GameObject Unfold(WorkspaceItem item, Vector3 eye, float delay)
    {
        GameObject go = Spawn(item);
        Transform t = go.transform;
        t.SetParent(presence.Frame, false);

        SlotPose(item.slot, item.kind, eye, out Vector3 pos, out Quaternion rot);
        // Rise out of the desk, near the front edge, lying flat.
        Vector3 startPos = new Vector3(Mathf.Clamp(pos.x * 0.3f, -0.25f, 0.25f), 0.02f, 0.12f);
        Quaternion startRot = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        Vector3 endScale = t.localScale;

        t.localPosition = startPos;
        t.localRotation = startRot;
        t.localScale = endScale * 0.02f;
        StartCoroutine(ForgeTween.Run(0.45f, delay, k =>
        {
            if (t == null) return;
            float e = ForgeTween.EaseOutCubic(k);
            t.localPosition = Vector3.LerpUnclamped(startPos, pos, e);
            t.localRotation = Quaternion.SlerpUnclamped(startRot, rot, e);
            t.localScale = endScale * Mathf.Max(0.02f, ForgeTween.EaseOutBack(k));
        }));
        return go;
    }

    void FoldAll()
    {
        foreach (var go in openItems)
        {
            if (go == null) continue;
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

    GameObject Spawn(WorkspaceItem item)
    {
        string title = string.IsNullOrEmpty(item.title) ? item.id : item.title;
        if (item.prefab != null)
        {
            var custom = Instantiate(item.prefab);
            var p = custom.GetComponentInChildren<ForgePanel>();
            if (p != null && !string.IsNullOrEmpty(item.title)) p.Title = item.title;
            var v = custom.GetComponentInChildren<ForgeVolume>();
            if (v != null && !string.IsNullOrEmpty(item.title)) v.Title = item.title;
            return custom;
        }

        if (item.kind == WorkspaceItemKind.Panel)
        {
            var panel = Instantiate(panelPrefab);
            panel.panelId = item.id;
            panel.Title = title;
            panel.SizeMeters = PanelSize(item.slot);
            panel.name = $"Panel {item.id}";
            return panel.gameObject;
        }

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
        return volume.gameObject;
    }

    void SlotPose(WorkspaceSlot slot, WorkspaceItemKind kind, Vector3 eye, out Vector3 pos, out Quaternion rot)
    {
        if (IsDeskSlot(slot))
        {
            float x = Mathf.Min(0.3f, Mathf.Max(0f, presence.FrameWidth * 0.5f - 0.12f));
            float z = Mathf.Clamp(presence.FrameDepth * 0.5f, 0.12f, 0.3f);
            pos = new Vector3(slot == WorkspaceSlot.DeskLeft ? -x : slot == WorkspaceSlot.DeskRight ? x : 0f, 0f, z);
            rot = Quaternion.LookRotation(Flat(pos - eye), Vector3.up);
            return;
        }

        float angle = 0f, up = -0.08f, radius = panelRadius;
        switch (slot)
        {
            case WorkspaceSlot.Left: angle = -sideAngle; break;
            case WorkspaceSlot.Right: angle = sideAngle; break;
            case WorkspaceSlot.FarLeft: angle = -farAngle; break;
            case WorkspaceSlot.FarRight: angle = farAngle; break;
            case WorkspaceSlot.Above: up = 0.36f; radius += 0.05f; break;
        }
        float a = angle * Mathf.Deg2Rad;
        pos = eye + new Vector3(Mathf.Sin(a) * radius, up, Mathf.Cos(a) * radius);

        if (kind == WorkspaceItemKind.Volume)
        {
            // Floating volume: base below the slot center, turned to face the user.
            pos.y -= 0.15f;
            rot = Quaternion.LookRotation(Flat(pos - eye), Vector3.up);
        }
        else
        {
            // Panel faces the eyes (a canvas reads correctly when its +Z points away from the viewer).
            rot = Quaternion.LookRotation(pos - eye, Vector3.up);
        }
    }

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
            id = "ml", label = "ML", color = new Color(0.25f, 0.55f, 1f),
            items =
            {
                new WorkspaceItem("screen-main", "VS Code", WorkspaceSlot.Main),
                new WorkspaceItem("ml-training", "Training", WorkspaceSlot.Above),
                new WorkspaceItem("ml-dataset", "Dataset", WorkspaceSlot.FarLeft, WorkspaceItemKind.Volume),
                new WorkspaceItem("ml-model3d", "Model", WorkspaceSlot.Left, WorkspaceItemKind.Volume),
                new WorkspaceItem("ml-results", "Results", WorkspaceSlot.Right),
            },
        },
        new WorkspaceMode
        {
            id = "cad", label = "CAD", color = new Color(1f, 0.55f, 0.2f),
            items =
            {
                new WorkspaceItem("screen-main", "rocket.py", WorkspaceSlot.Main),
                new WorkspaceItem("cad-viewer", "Rocket", WorkspaceSlot.DeskCenter, WorkspaceItemKind.Volume),
                new WorkspaceItem("cad-params", "Parameters", WorkspaceSlot.Right),
                new WorkspaceItem("cad-timeline", "Versions", WorkspaceSlot.Left),
            },
        },
        new WorkspaceMode
        {
            id = "screens", label = "SCR", color = new Color(0.4f, 0.85f, 0.5f),
            items =
            {
                new WorkspaceItem("screen-main", "Screen 1", WorkspaceSlot.Main),
                new WorkspaceItem("screen-left", "Screen 2", WorkspaceSlot.Left),
                new WorkspaceItem("screen-right", "Screen 3", WorkspaceSlot.Right),
            },
        },
        new WorkspaceMode
        {
            id = "messages", label = "MSG", color = new Color(0.85f, 0.4f, 0.9f),
            items = { new WorkspaceItem("messages", "Messages", WorkspaceSlot.Right) },
        },
    };
}
