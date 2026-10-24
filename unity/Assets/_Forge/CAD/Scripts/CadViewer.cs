using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The part on the desk. CadViewer.prefab is a ForgeVolume variant with this component.
/// Shows the CadSession's current version. On a change: the changed parts glow (cad-vision's outline),
/// a ghost of what they were fades out, and the CG (blue) and CP (red) collars slide to their new
/// heights. cad.view explodes the assembly or focuses one part.
/// The first model of each design is fitted to the volume and later versions keep that scale, so "20% longer"
/// looks longer. Opening another design fits it afresh and puts its title on the volume.
/// Inspect (the panel's button, key I): the model flies off the desk to a stage in front of the eyes, big and
/// still, where <see cref="CadTakeApart"/> lets you take it apart; Back to desk flies it home. The stage has its
/// own fit (each visit fits afresh, later versions keep that scale).
/// </summary>
[RequireComponent(typeof(ForgeVolume))]
public class CadViewer : MonoBehaviour
{
    public CadMockSet mocks;

    [Header("Materials")]
    public Material ghostMaterial;  // Forge/CadGhost
    public Material outlineMask;    // CADVision/SelectionMask
    public Material outline;        // CADVision/SelectionOutline
    public Material cgMaterial;
    public Material cpMaterial;

    [Header("Feel")]
    public float ghostSeconds = 1.6f;
    public float glowSeconds = 2.5f;
    public float glowWidth = 0.008f;
    public float markerSeconds = 0.6f;
    [Tooltip("The first model fills this fraction of the volume, leaving room for later versions to grow visibly.")]
    [Range(0.3f, 1f)] public float fitHeadroom = 0.7f;
    [Tooltip("Exploded view: each part moves this many times its vertical distance from the model's centre...")]
    public float explodeAxial = 0.6f;
    [Tooltip("...and this many times its sideways distance (fins fan out).")]
    public float explodeRadial = 1.5f;
    [Range(0f, 1f)] public float focusDim = 0.12f;
    public bool showMarkers = true;
    [Tooltip("Outline of the part pointed at or held in the Inspect view.")]
    public Color hoverColor = new Color(0.45f, 0.85f, 1f, 1f);
    public float hoverWidth = 0.004f;

    [Header("Inspect: the big view in front of you")]
    [Tooltip("The stage's centre is this far in front of the eyes (m), toward the desk...")]
    public float stageDistance = 0.6f;
    [Tooltip("...and this far below them (m).")]
    public float stageDrop = 0.1f;
    [Tooltip("The model is scaled to fit this box (m: width, height, depth). CAD mode's VS Code screen is 0.8 m in " +
             "front of the eyes, so the box stays in front of it.")]
    public Vector3 stageSize = new Vector3(0.6f, 0.6f, 0.36f);
    [Tooltip("On the stage the model is turned this far (deg) so you see a corner...")]
    public float stageYaw = 30f;
    [Tooltip("...and, unless it's tall (a rocket stays upright), tipped back this far so you see its top.")]
    public float stageTilt = 25f;
    public float inspectSeconds = 0.6f;

    public CadModel Current => current;
    public bool Exploded => explodeTarget > 0.5f;
    /// <summary>Up on the stage (or flying there).</summary>
    public bool OnStage => onStage;
    /// <summary>Holds the model; the volume's content on the desk, the stage's child when inspecting.</summary>
    public Transform Holder => holder;
    /// <summary>The model's scale on the stage (real size = 1).</summary>
    public float StageScale => stageScale;

    struct ExplodePart
    {
        public Transform transform;
        public Vector3 home;
        public Vector3 offset;
        public bool detached;  // taken off in the Inspect view: explode leaves it where the user put it
    }

    struct Swapped
    {
        public Renderer renderer;
        public Material[] original;
    }

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int WidthId = Shader.PropertyToID("_OutlineWidth");
    static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
    const string LayerPrefix = "Glow ";

    ForgeVolume volume;
    CadSession session;
    CadTakeApart apart;
    Workspace workspace;
    Transform holder;
    CadModel current, ghost;
    float ghostT = 1f;
    Color ghostColor;
    readonly List<Renderer> ghostRenderers = new List<Renderer>();
    float glowT = 1f;
    readonly List<GameObject> glowLayers = new List<GameObject>();
    readonly List<GameObject> hoverLayers = new List<GameObject>();
    readonly List<Swapped> dimmed = new List<Swapped>();
    Material glowInstance, hoverInstance;
    MaterialPropertyBlock block;
    Transform cgRing, cpRing;
    float cgFrom, cgTo, cpFrom, cpTo, markerT = 1f;
    bool markersVisible;
    readonly List<ExplodePart> explodeParts = new List<ExplodePart>();
    float explodeT, explodeTarget;
    string focusId, highlighted;
    int loadToken;
    bool fitted;
    string part, title;  // the design shown; a different one is fitted afresh
    Bounds assembled;    // the current model in holder space, assembled (before explode or take apart)
    Transform stage;     // the Inspect view's anchor in front of the eyes (scene root, made on first use)
    bool onStage, stageFitted;
    float stageScale = 1f;
    Quaternion stageTurn = Quaternion.identity;
    Coroutine flight;

    void Awake()
    {
        volume = GetComponent<ForgeVolume>();
        apart = GetComponent<CadTakeApart>();
        block = new MaterialPropertyBlock();
        if (outline != null)
        {
            glowInstance = new Material(outline);
            hoverInstance = new Material(outline);
            hoverInstance.SetColor(OutlineColorId, hoverColor);
            hoverInstance.SetFloat(WidthId, hoverWidth);
        }
        ghostColor = ghostMaterial != null ? ghostMaterial.GetColor(ColorId) : new Color(0.6f, 0.85f, 1f, 0.35f);
        holder = new GameObject("CadModel").transform;
        volume.SetContent(holder.gameObject, fit: false);
        cgRing = MakeCollar("CG", cgMaterial);
        cpRing = MakeCollar("CP", cpMaterial);
    }

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        session.ModelChanged += OnModelChanged;
        session.ViewChanged += OnViewChanged;
        session.InspectChanged += OnInspectChanged;
        if (session.Latest != null) OnModelChanged(session.Latest);  // spawned after the first version arrived
        OnViewChanged(session.View);
        OnInspectChanged();
    }

    void Start()
    {
        // Workspace names the volume after spawning it; the open design's title wins.
        if (!string.IsNullOrEmpty(title)) volume.Title = title;
        // Closing CAD mode (or opening another) folds the volume from where it is: bring the model home first.
        workspace = FindAnyObjectByType<Workspace>();
        if (workspace != null) workspace.modeChanged.AddListener(OnModeChanged);
    }

    void OnDisable()
    {
        GoHome(animate: false);
        if (session == null) return;
        session.ModelChanged -= OnModelChanged;
        session.ViewChanged -= OnViewChanged;
        session.InspectChanged -= OnInspectChanged;
    }

    void OnDestroy()
    {
        if (workspace != null) workspace.modeChanged.RemoveListener(OnModeChanged);
        ghost?.Dispose();
        current?.Dispose();
        if (glowInstance != null) Destroy(glowInstance);
        if (hoverInstance != null) Destroy(hoverInstance);
        if (stage != null) Destroy(stage.gameObject);
    }

    /// <summary>Explode or reassemble (goes through the session, so the hub and the pet see it too).</summary>
    public void SetExploded(bool on) => session.SetView(on, focusId);

    async void OnModelChanged(CadUpdate update)
    {
        if (current != null && current.Version == update.info.version) return;
        int token = ++loadToken;
        CadModel model;
        try
        {
            model = await CadModel.LoadAsync(update.info.version, update.glb, update.metadata, holder,
                destroyCancellationToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            Debug.LogError($"[CadViewer] Could not load v{update.info.version}: {e.Message}");
            return;
        }
        if (token != loadToken) { model.Dispose(); return; }  // a newer version arrived while this one loaded
        if (apart != null) apart.Release(instant: true);      // parts back on before the swap, glow and ghost
        Swap(model, update.info);
    }

    void Swap(CadModel model, CadModelUpdated info)
    {
        var previous = current;
        current = model;
        bool samePart = previous != null && info.part == part;
        bool animate = samePart && info.previousVersion.HasValue;  // glow and ghost compare versions of one design
        if (!samePart) fitted = stageFitted = false;
        part = info.part;
        if (!string.IsNullOrEmpty(info.title)) volume.Title = title = info.title;
        EndGlow();
        Highlight(null);
        ClearDim();
        DropGhost();
        if (previous != null)
        {
            if (animate) StartGhost(previous, info);
            else previous.Dispose();
        }
        assembled = ModelBounds();
        if (!fitted || Outgrown())
        {
            FitToVolume();
            fitted = true;
        }
        if (onStage && (!stageFitted || StageOutgrown())) PlaceOnStage(animate: false);
        BuildExplode();
        UpdateMarkers(info.analysis, animate);
        ApplyFocus();  // before the glow, so dimming never touches the temporary glow layers
        if (animate) StartGlow(info.changedIds);
        if (onStage && apart != null) apart.Attach(current);
    }

    void Update()
    {
        var keys = Keyboard.current;
        if (keys != null && keys.iKey.wasPressedThisFrame) session.SetInspecting(!session.Inspecting);

        float dt = Time.deltaTime;
        if (ghost != null)
        {
            ghostT += dt / ghostSeconds;
            var c = ghostColor;
            c.a *= 1f - Smooth(ghostT);
            block.SetColor(ColorId, c);
            for (int i = 0; i < ghostRenderers.Count; i++) ghostRenderers[i].SetPropertyBlock(block);
            if (ghostT >= 1f) DropGhost();
        }
        if (glowLayers.Count > 0)
        {
            glowT += dt / glowSeconds;
            float pulse = 0.65f + 0.35f * Mathf.Sin(glowT * 18f);
            glowInstance.SetFloat(WidthId, glowWidth * pulse * (1f - Smooth(glowT)));
            if (glowT >= 1f) EndGlow();
        }
        if (markerT < 1f)
        {
            markerT = Mathf.Min(1f, markerT + dt / markerSeconds);
            float e = Smooth(markerT);
            SetHeight(cgRing, Mathf.Lerp(cgFrom, cgTo, e));
            SetHeight(cpRing, Mathf.Lerp(cpFrom, cpTo, e));
        }
        if (explodeT != explodeTarget)
        {
            explodeT = Mathf.MoveTowards(explodeT, explodeTarget, dt * 2.5f);
            ApplyExplode();
        }
    }

    // Fit (desk). From the model's own bounds, so it works while the model is up on the stage too.

    void FitToVolume()
    {
        var s = volume.size;
        var b = assembled;
        float k = Mathf.Min(s.x / Mathf.Max(b.size.x, 1e-4f), s.y / Mathf.Max(b.size.y, 1e-4f),
            s.z / Mathf.Max(b.size.z, 1e-4f)) * fitHeadroom;
        // Stand it on the base, centred; the headroom shrinks it about the base.
        var root = volume.ContentRoot;
        root.localScale = Vector3.one * k;
        root.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z) * k;
    }

    bool Outgrown()
    {
        var b = assembled.size * volume.ContentRoot.localScale.x;
        return b.y > volume.size.y * 1.1f || b.x > volume.size.x * 1.4f || b.z > volume.size.z * 1.4f;
    }

    /// <summary>The current model's bounds in holder space, from its meshes (exact whatever the holder's pose).</summary>
    Bounds ModelBounds()
    {
        var b = new Bounds();
        bool first = true;
        var toHolder = holder.worldToLocalMatrix;
        foreach (var filter in current.Root.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null || filter.name.StartsWith(LayerPrefix, StringComparison.Ordinal)) continue;
            var m = toHolder * filter.transform.localToWorldMatrix;
            var mb = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    // Inspect: fly the model to the stage and back

    void OnInspectChanged()
    {
        if (session.Inspecting == onStage) return;  // also raised when the number of parts off changes
        if (session.Inspecting) GoToStage();
        else GoHome(animate: true);
    }

    void OnModeChanged(string mode) => GoHome(animate: false);

    void GoToStage()
    {
        onStage = true;
        StepAside(true);
        PoseStage();
        stageFitted = false;  // each visit fits afresh; versions during the visit keep the scale
        if (current == null) return;  // the first version goes straight up when it arrives (Swap)
        PlaceOnStage(animate: true);
        if (apart != null) apart.Attach(current);
    }

    void GoHome(bool animate)
    {
        if (!onStage) return;
        onStage = false;
        if (holder == null || volume.ContentRoot == null) return;  // leaving Play: the scene is being torn down
        StepAside(false);
        if (apart != null) apart.Release(instant: !animate);
        FlyTo(volume.ContentRoot, Vector3.zero, Quaternion.identity, 1f, animate ? inspectSeconds : 0f);
        if (session != null) session.SetInspecting(false);
    }

    // The desk volume is empty while the model is away: its model grab box steps aside (Core's VolumeManipulator).
    void StepAside(bool away)
    {
        var grips = GetComponent<VolumeManipulator>();
        if (grips != null) grips.ModelAway = away;
    }

    // In front of the eyes, toward the desk volume (not where you're looking: that's the panel you just pressed),
    // and never into the desk. A flat screen (desktop mode, sandbox) sees far less than a headset: there the stage
    // steps back until it fits in view.
    void PoseStage()
    {
        if (stage == null) stage = new GameObject("CadStage").transform;
        var cam = Camera.main;
        Transform head = cam != null ? cam.transform : transform;
        Vector3 toward = Vector3.ProjectOnPlane(transform.position - head.position, Vector3.up);
        if (toward.sqrMagnitude < 1e-4f) toward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (toward.sqrMagnitude < 1e-4f) toward = Vector3.forward;
        toward.Normalize();
        float distance = stageDistance;
        if (cam != null && !cam.stereoEnabled)
        {
            float tan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float fit = Mathf.Max(stageSize.y, stageSize.x / Mathf.Max(cam.aspect, 0.1f)) * 0.55f / tan;  // 10% margin
            distance = Mathf.Max(distance, fit + stageSize.z * 0.5f);
        }
        Vector3 centre = head.position + toward * distance + Vector3.down * stageDrop;
        centre.y = Mathf.Max(centre.y, transform.position.y + stageSize.y * 0.5f + 0.02f);
        stage.SetPositionAndRotation(centre, Quaternion.LookRotation(toward, Vector3.up));
    }

    // The assembled model, turned to show a corner (and its top), fitted to the stage box and centred on the stage.
    void PlaceOnStage(bool animate)
    {
        var b = assembled;
        bool tall = b.size.y > 1.2f * Mathf.Max(b.size.x, b.size.z);
        stageTurn = Quaternion.Euler(tall ? 0f : stageTilt, 0f, 0f) * Quaternion.Euler(0f, stageYaw, 0f);
        var size = TurnedSize(b.size, stageTurn);
        stageScale = Mathf.Min(stageSize.x / Mathf.Max(size.x, 1e-4f), stageSize.y / Mathf.Max(size.y, 1e-4f),
            stageSize.z / Mathf.Max(size.z, 1e-4f));
        stageFitted = true;
        FlyTo(stage, -(stageTurn * b.center) * stageScale, stageTurn, stageScale, animate ? inspectSeconds : 0f);
    }

    bool StageOutgrown()
    {
        var b = TurnedSize(assembled.size, stageTurn) * stageScale;
        return b.x > stageSize.x * 1.25f || b.y > stageSize.y * 1.25f || b.z > stageSize.z * 1.25f;
    }

    // Size of the box that holds a box of `size` turned by `turn`.
    static Vector3 TurnedSize(Vector3 size, Quaternion turn)
    {
        Vector3 x = turn * new Vector3(size.x, 0f, 0f), y = turn * new Vector3(0f, size.y, 0f), z = turn * new Vector3(0f, 0f, size.z);
        return new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
            Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
    }

    // Re-parent the holder (keeping where it is in the world) and tween it to a pose in its new parent.
    void FlyTo(Transform parent, Vector3 position, Quaternion rotation, float scale, float seconds)
    {
        if (flight != null) StopCoroutine(flight);
        flight = null;
        holder.SetParent(parent, true);
        if (seconds <= 0f || !isActiveAndEnabled)
        {
            holder.localPosition = position;
            holder.localRotation = rotation;
            holder.localScale = Vector3.one * scale;
            return;
        }
        Vector3 p0 = holder.localPosition, s0 = holder.localScale, s1 = Vector3.one * scale;
        Quaternion r0 = holder.localRotation;
        flight = StartCoroutine(ForgeTween.Run(seconds, 0f, k =>
        {
            float e = Smooth(k);
            holder.localPosition = Vector3.LerpUnclamped(p0, position, e);
            holder.localRotation = Quaternion.SlerpUnclamped(r0, rotation, e);
            holder.localScale = Vector3.LerpUnclamped(s0, s1, e);
        }, () => flight = null));
    }

    // Ghost: what the changed parts were

    void StartGhost(CadModel old, CadModelUpdated info)
    {
        ghost = old;
        ghostT = 0f;
        var keep = new HashSet<string>(info.changedIds);
        keep.UnionWith(info.removedIds);
        foreach (var r in old.Root.GetComponentsInChildren<Renderer>(true))
        {
            var part = r.GetComponentInParent<CadPart>();
            if (ghostMaterial == null || part == null || !keep.Contains(part.id)) { r.enabled = false; continue; }
            var materials = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = ghostMaterial;
            r.sharedMaterials = materials;
            ghostRenderers.Add(r);
        }
        old.Root.name += " (ghost)";
    }

    void DropGhost()
    {
        ghost?.Dispose();
        ghost = null;
        ghostRenderers.Clear();
        ghostT = 1f;
    }

    // Glow: cad-vision's silhouette rim on the parts that changed. OCCT's GLBs split a part into several
    // primitives (submeshes), so the mask and rim go on child renderers that cover every submesh; the
    // render queue draws all masks before any rim.

    void StartGlow(List<string> ids)
    {
        if (glowInstance == null || outlineMask == null || ids == null || ids.Count == 0) return;
        var set = new HashSet<string>(ids);
        foreach (var r in current.Root.GetComponentsInChildren<Renderer>(true))
        {
            var part = r.GetComponentInParent<CadPart>();
            if (part == null || !set.Contains(part.id) || IsLayer(r)) continue;
            AddGlowLayer(r, outlineMask, glowLayers);
            AddGlowLayer(r, glowInstance, glowLayers);
        }
        glowT = 0f;
    }

    /// <summary>Outline one part in the hover colour (pointed at or held in the Inspect view); null clears it.</summary>
    public void Highlight(string id)
    {
        if (id == highlighted) return;
        foreach (var layer in hoverLayers)
        {
            if (layer == null) continue;
            layer.SetActive(false);
            Destroy(layer);
        }
        hoverLayers.Clear();
        highlighted = id;
        if (current == null || id == null || hoverInstance == null || outlineMask == null) return;
        foreach (var r in current.Renderers(id))
        {
            if (IsLayer(r)) continue;
            AddGlowLayer(r, outlineMask, hoverLayers);
            AddGlowLayer(r, hoverInstance, hoverLayers);
        }
    }

    static void AddGlowLayer(Renderer r, Material material, List<GameObject> layers)
    {
        var filter = r.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return;
        var layer = new GameObject(LayerPrefix + material.shader.name);
        layer.transform.SetParent(r.transform, false);
        layer.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
        var layerRenderer = layer.AddComponent<MeshRenderer>();
        var materials = new Material[filter.sharedMesh.subMeshCount];
        for (int i = 0; i < materials.Length; i++) materials[i] = material;
        layerRenderer.sharedMaterials = materials;
        layerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        layerRenderer.receiveShadows = false;
        layers.Add(layer);
    }

    static bool IsLayer(Renderer r) => r.name.StartsWith(LayerPrefix, StringComparison.Ordinal);

    void EndGlow()
    {
        foreach (var layer in glowLayers)
            if (layer != null) Destroy(layer);
        glowLayers.Clear();
        glowT = 1f;
    }

    // CG / CP collars

    Transform MakeCollar(string label, Material material)
    {
        var collar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        collar.name = label + " marker";
        Destroy(collar.GetComponent<Collider>());
        if (material != null) collar.GetComponent<MeshRenderer>().sharedMaterial = material;
        collar.transform.SetParent(holder, false);
        collar.SetActive(false);
        return collar.transform;
    }

    void UpdateMarkers(CadAnalysis analysis, bool animate)
    {
        var markers = analysis?.markers;
        bool show = showMarkers && markers?.cg != null && markers.cp != null && analysis.diameterMm.HasValue;
        bool wasShown = markersVisible;
        markersVisible = show;
        cgRing.gameObject.SetActive(show && explodeT < 0.02f);
        cpRing.gameObject.SetActive(show && explodeT < 0.02f);
        if (!show) return;
        // Collars stick out a little past the body tube; the primitive is 2 units tall, so y = half the thickness.
        float d = analysis.diameterMm.Value / 1000f * 1.45f;
        cgRing.localScale = new Vector3(d, 0.0012f, d);
        cpRing.localScale = new Vector3(d * 1.12f, 0.0012f, d * 1.12f);
        cgTo = markers.cg[1];
        cpTo = markers.cp[1];
        cgFrom = animate && wasShown ? cgRing.localPosition.y : cgTo;
        cpFrom = animate && wasShown ? cpRing.localPosition.y : cpTo;
        markerT = 0f;
    }

    static void SetHeight(Transform t, float y) => t.localPosition = new Vector3(0f, y, 0f);

    // Explode / focus

    void OnViewChanged(CadView view)
    {
        explodeTarget = view.explode == true ? 1f : 0f;
        if (view.focusId == focusId) return;
        EndGlow();
        ClearDim();
        focusId = view.focusId;
        ApplyFocus();
    }

    void BuildExplode()
    {
        explodeParts.Clear();
        var all = LocalBounds(current.Root.GetComponentsInChildren<Renderer>(), holder);
        // Spread parts away from the model's centre, along the axis and sideways; then lift everything so
        // nothing sinks below the base.
        var offsets = new Vector3[current.partIds.Count];
        float lowest = 0f;
        for (int i = 0; i < offsets.Length; i++)
        {
            var d = LocalBounds(current.objects[current.partIds[i]].GetComponentsInChildren<Renderer>(), holder).center - all.center;
            offsets[i] = new Vector3(d.x * explodeRadial, d.y * explodeAxial, d.z * explodeRadial);
            lowest = Mathf.Min(lowest, offsets[i].y);
        }
        for (int i = 0; i < offsets.Length; i++)
        {
            var t = current.objects[current.partIds[i]].transform;
            var lifted = offsets[i] + Vector3.up * -lowest;
            var offset = t.parent.InverseTransformVector(holder.TransformVector(lifted));
            explodeParts.Add(new ExplodePart { transform = t, home = t.localPosition, offset = offset });
        }
        ApplyExplode();
    }

    void ApplyExplode()
    {
        float e = Smooth(explodeT);
        for (int i = 0; i < explodeParts.Count; i++)
        {
            var p = explodeParts[i];
            if (!p.detached) p.transform.localPosition = p.home + p.offset * e;
        }
        // CG/CP belong to the assembled rocket.
        bool collars = markersVisible && explodeT < 0.02f;
        if (cgRing.gameObject.activeSelf != collars)
        {
            cgRing.gameObject.SetActive(collars);
            cpRing.gameObject.SetActive(collars);
        }
    }

    /// <summary>
    /// Where <paramref name="part"/> belongs right now (assembled, or its exploded spot), in its parent's space.
    /// False if it isn't one of the current model's parts.
    /// </summary>
    public bool TryGetSlot(Transform part, out Vector3 localPosition)
    {
        float e = Smooth(explodeTarget);  // where explode is heading, so a part sent home lands where the rest end up
        for (int i = 0; i < explodeParts.Count; i++)
        {
            if (explodeParts[i].transform != part) continue;
            localPosition = explodeParts[i].home + explodeParts[i].offset * e;
            return true;
        }
        localPosition = default;
        return false;
    }

    /// <summary>A part taken off (or put back) in the Inspect view; explode leaves detached parts alone.</summary>
    public void SetDetached(Transform part, bool detached)
    {
        for (int i = 0; i < explodeParts.Count; i++)
        {
            if (explodeParts[i].transform != part) continue;
            var p = explodeParts[i];
            p.detached = detached;
            explodeParts[i] = p;
            return;
        }
    }

    void ApplyFocus()
    {
        if (current == null || string.IsNullOrEmpty(focusId) || ghostMaterial == null) return;
        if (!current.objects.TryGetValue(focusId, out var focus)) return;
        var c = ghostColor;
        c.a = focusDim;
        block.SetColor(ColorId, c);
        foreach (var r in current.Root.GetComponentsInChildren<Renderer>())
        {
            if (r.transform.IsChildOf(focus.transform) || IsLayer(r)) continue;
            dimmed.Add(new Swapped { renderer = r, original = r.sharedMaterials });
            var materials = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = ghostMaterial;
            r.sharedMaterials = materials;
            r.SetPropertyBlock(block);
        }
    }

    void ClearDim()
    {
        foreach (var s in dimmed)
        {
            if (s.renderer == null) continue;
            s.renderer.sharedMaterials = s.original;
            s.renderer.SetPropertyBlock(null);
        }
        dimmed.Clear();
    }

    // Helpers

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Bounds of renderers in <paramref name="space"/>'s local coordinates.</summary>
    static Bounds LocalBounds(Renderer[] renderers, Transform space)
    {
        var b = new Bounds();
        bool first = true;
        foreach (var r in renderers)
        {
            var wb = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = wb.center + Vector3.Scale(wb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var local = space.InverseTransformPoint(corner);
                if (first) { b = new Bounds(local, Vector3.zero); first = false; }
                else b.Encapsulate(local);
            }
        }
        return b;
    }
}
