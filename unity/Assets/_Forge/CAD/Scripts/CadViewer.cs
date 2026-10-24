using System;
using System.Collections;
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
/// Expand (Inspect: the panel's button, the ring of light by the base, key I): the model springs off the desk to a stage
/// in front of the eyes, 2 to 3 times bigger, where <see cref="CadTakeApart"/> lets you take it apart, dimensions come
/// on and a lid turns to glass; a ring of light under it and a chip with its scale. Back to desk springs it home.
/// From the version's analysis (CAD_PLAN section 5, round 4; HANDOFF_UI2/cad/CAD_UX_SPEC.md), desk and stage alike:
/// - Explode (<see cref="CadExploder"/>): parts spring out along the design's own vectors, staggered, and one chip per
///   kind of part comes up beside the model on a leader (<see cref="CadAnnotations"/>).
/// - Markers (CG, CP, thrust, input, output...; on by default, key N) and dimension callouts (Measure, key M).
/// - The demo (<see cref="CadDemoPlayer"/>): Fly, Hover or Run.
/// - A parameter's parts and dimension on the model while a panel points at it (<see cref="HighlightParam"/>).
/// Panels drive these through <see cref="CadSession"/> (SetView, SetInspecting, RequestDemo, SetMeasuring,
/// SetMarkersShown) or this class's API (<see cref="Active"/> finds the viewer); both end up in the same place.
/// Everything new is made at runtime under a "CadOverlay" child, so the prefab needs nothing.
/// </summary>
[RequireComponent(typeof(ForgeVolume))]
[DefaultExecutionOrder(100)]  // after the turntable and the volume's grips: the demo poses the model last each frame
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
    [Range(0f, 1f)] public float focusDim = 0.12f;
    [Tooltip("Markers and the rocket's CG / CP collars (panels toggle them with CadSession.SetMarkersShown, key N).")]
    public bool showMarkers = true;
    [Tooltip("Outline of the part pointed at or held in the Inspect view.")]
    public Color hoverColor = new Color(0.45f, 0.85f, 1f, 1f);
    public float hoverWidth = 0.004f;
    [Tooltip("Outline of the parts a parameter shapes, while a panel points at its row.")]
    public Color paramColor = new Color(1f, 0.6f, 0.28f, 1f);

    [Header("Explode")]
    [Tooltip("The design's own explode distances (analysis.explode) times this (radial designs get 1.5x more).")]
    public float explodeScale = 1f;
    [Tooltip("Versions without explode data: each part moves this many times its vertical distance from the model's centre...")]
    public float explodeAxial = 0.6f;
    [Tooltip("...and this many times its sideways distance (fins fan out).")]
    public float explodeRadial = 1.5f;

    [Header("Demo: Fly / Hover / Run (CAD UX spec, section 4)")]
    [Tooltip("Fly: apogee comes this far above the pad (m), kept at least 0.3 m under the room's ceiling.")]
    public float launchHeight = 1.3f;
    [Tooltip("Fly: the climb plays in real time; the way down under the parachute plays this many times faster.")]
    public float descentSpeedup = 8f;
    [Tooltip("Fly: it lands at most this far from the pad (m; the C6 rocket lands about 0.24 m out), then glides home.")]
    public float maxDrift = 0.35f;
    [Tooltip("Hover: how high it lifts off its base (m).")]
    public float hoverHeight = 0.22f;
    [Tooltip("Hover: seconds hovering before it lands by itself (pressing Hover again lands it sooner).")]
    public float hoverSeconds = 6f;
    [Tooltip("Hover: props turn at most this many times a second on screen, so they read as fast rather than strobing.")]
    public float propTurnsPerSecond = 4.5f;
    [Tooltip("Run: every part turns at its real rpm divided by this (3,000 rpm shows as half a turn a second).")]
    public float spinSlowdown = 100f;
    [Tooltip("Run: seconds running at speed before it stops by itself (pressing Run again stops it sooner).")]
    public float spinSeconds = 7.6f;

    [Header("Inspect / Expand: the big view in front of you")]
    [Tooltip("The stage's centre is this far in front of the eyes (m), toward the desk...")]
    public float stageDistance = 0.6f;
    [Tooltip("...and this far below them (m).")]
    public float stageDrop = 0.1f;
    [Tooltip("The model is scaled to fit this box (m: width, height, depth). CAD mode's VS Code screen is 0.8 m in " +
             "front of the eyes, so the box stays in front of it.")]
    public Vector3 stageSize = new Vector3(0.6f, 0.6f, 0.36f);
    [Tooltip("On the stage the model is turned this far (deg) so you see a corner (a Run design faces you square on)...")]
    public float stageYaw = 30f;
    [Tooltip("...and, unless it's tall (a rocket stays upright), tipped back this far so you see its top...")]
    public float stageTilt = 25f;
    [Tooltip("...or this far if it's flat (height under 35% of its footprint), so a drone's top reads.")]
    public float stageFlatTilt = 40f;
    [Tooltip("Expand / Back to desk: any value above 0 springs the model there (about 0.6 s); 0 jumps.")]
    public float inspectSeconds = 0.6f;

    // ---- API (for panels; the session's SetView / SetInspecting / RequestDemo / SetMeasuring / SetMarkersShown agree) ----

    /// <summary>The viewer in the scene (panels are spawned separately: look it up here). Null while CAD mode is closed.</summary>
    public static CadViewer Active { get; private set; }

    public CadModel Current => current;
    /// <summary>The version on show; its analysis has the markers, dimensions, explode, mass breakdown and demo.</summary>
    public CadModelUpdated Shown => shown;
    public bool Exploded => exploder != null && exploder.Exploded;
    /// <summary>Up on the stage (or flying there).</summary>
    public bool OnStage => onStage;
    /// <summary>Holds the model; the volume's content on the desk, the stage's child when inspecting.</summary>
    public Transform Holder => holder;
    /// <summary>The model's fitted scale on the stage (real size = 1).</summary>
    public float StageScale => stageScale;

    /// <summary>Explode (true) or reassemble. Goes through the session, so the hub and the pet see it too.</summary>
    public void Explode(bool on)
    {
        if (session != null) session.SetView(on, focusId);
    }

    /// <summary>Same as <see cref="Explode"/>.</summary>
    public void SetExploded(bool on) => Explode(on);

    /// <summary>Expand (the model up big on the stage in front of you) or back to the desk. Same as Inspect.</summary>
    public void Expand(bool on)
    {
        if (session != null) session.SetInspecting(on);
    }

    public bool Expanded => onStage;

    /// <summary>On the stage: the model at its real size (true) or fitted to the stage (false, the default).</summary>
    public void SetTrueSize(bool on)
    {
        if (on == trueSize) return;
        trueSize = on;
        if (onStage && current != null) PlaceOnStage(animate: true);
    }

    public bool TrueSize => trueSize;

    /// <summary>This version's demo (kind, label for the button: Fly / Hover / Run, note, readouts, blocked), or null.</summary>
    public CadDemo Demo => CadDemoPlayer.Resolve(shown?.analysis, session != null ? session.Params : null);
    /// <summary>A demo is running (or getting ready: reassembling, fetching the flight, coming back from the stage).</summary>
    public bool DemoRunning => player != null && player.Running;
    /// <summary>A demo started (true) or ended (false). Also CadSession.DemoChanged.</summary>
    public event Action<bool> DemoChanged;
    /// <summary>Every frame of a running demo: its phase and live readouts. Also CadSession.DemoFrame.</summary>
    public event Action<CadDemoFrame> DemoFrame;

    /// <summary>Start this version's demo (Fly, Hover or Run); false if it has none or the hub says it can't run.</summary>
    public bool StartDemo()
    {
        if (session == null || player == null) return false;
        var demo = Demo;
        if (demo == null || !string.IsNullOrEmpty(demo.blocked)) return false;
        // Fly needs the desk: from the stage the model goes home first, and the demo waits for it.
        if (demo.kind == CadDemoPlayer.Launch && session.Inspecting) session.SetInspecting(false);
        player.launchHeight = launchHeight;
        player.descentSpeedup = descentSpeedup;
        player.maxDrift = maxDrift;
        player.hoverHeight = hoverHeight;
        player.hoverSeconds = hoverSeconds;
        player.propTurnsPerSecond = propTurnsPerSecond;
        player.spinSlowdown = spinSlowdown;
        player.spinSeconds = spinSeconds;
        return player.Start(session);
    }

    /// <summary>Stop the demo: it lands, glides home or spins down first (<paramref name="instant"/>: back as it was now).</summary>
    public void StopDemo(bool instant = false)
    {
        if (player != null) player.Stop(instant);
    }

    /// <summary>Dimension callouts on or off (through the session, so every panel's Measure agrees). Off by default;
    /// on by themselves while the model is up on the stage.</summary>
    public void ShowMeasure(bool on)
    {
        if (session != null) session.SetMeasuring(on);
    }

    public bool Measuring => session != null && session.Measuring;

    /// <summary>Markers (and the rocket's CG / CP collars) on or off. On by default.</summary>
    public void ShowMarkers(bool on)
    {
        if (session != null) session.SetMarkersShown(on);
    }

    public bool MarkersShown => session == null || session.MarkersShown;

    /// <summary>
    /// A panel points at a parameter row: its parts (params[].parts) get an orange outline and its dimension
    /// (params[].dimension) is drawn on the model. Null clears it. Kept across new versions until cleared.
    /// </summary>
    public void HighlightParam(string name)
    {
        name = string.IsNullOrEmpty(name) ? null : name;
        if (name == highlightedParam) return;
        highlightedParam = name;
        ShowParam();
    }

    public void ClearParamHighlight() => HighlightParam(null);

    public string HighlightedParam => highlightedParam;

    struct Swapped
    {
        public Renderer renderer;
        public Material[] original;
    }

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int WidthId = Shader.PropertyToID("_OutlineWidth");
    static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
    const string LayerPrefix = "Glow ";
    const float TripSeconds = 1f;  // the springs settle in about 0.6 s; the last 0.4 s is too small to see

    ForgeVolume volume;
    CadSession session;
    CadTakeApart apart;
    Workspace workspace;
    Transform holder, overlay;
    CadModel current, ghost;
    CadModelUpdated shown;
    CadExploder exploder;
    CadAnnotations annotations;
    CadDemoPlayer player;
    CadReveal reveal;  // made in Awake: it holds a MaterialPropertyBlock, which a MonoBehaviour's field initializer can't make
    CadExpandButton expandButton;
    CadLines stageLines;
    CadChips stageChips;
    CadLabel scaleChip;
    float ghostT = 1f;
    Color ghostColor;
    readonly List<Renderer> ghostRenderers = new List<Renderer>();
    float glowT = 1f;
    readonly List<GameObject> glowLayers = new List<GameObject>();
    readonly List<GameObject> hoverLayers = new List<GameObject>();
    readonly List<GameObject> paramLayers = new List<GameObject>();
    readonly List<Swapped> dimmed = new List<Swapped>();
    Material glowInstance, hoverInstance, paramInstance;
    MaterialPropertyBlock block;
    Transform cgRing, cpRing;
    float cgFrom, cgTo, cpFrom, cpTo, markerT = 1f;
    bool collarData;     // the version has the rocket's CG / CP (markers_gltf + a body diameter)
    string focusId, highlighted, highlightedParam;
    int loadToken;
    bool fitted;
    string part, title;  // the design shown; a different one is fitted afresh
    Bounds assembled;    // the current model in holder space, assembled (before explode or take apart)
    Transform stage;     // the Inspect view's anchor in front of the eyes (scene root, made on first use)
    bool onStage, stageFitted, stageTall, stageSettled, measureBeforeStage, trueSize;
    float stageScale = 1f, tripProgress = 1f, stageRing, scaleShown = -1f;
    Quaternion stageTurn = Quaternion.identity;
    Coroutine trip;      // the model springing to or from the stage

    void Awake()
    {
        volume = GetComponent<ForgeVolume>();
        apart = GetComponent<CadTakeApart>();
        block = new MaterialPropertyBlock();
        reveal = new CadReveal();
        if (outline != null)
        {
            glowInstance = new Material(outline);
            hoverInstance = new Material(outline);
            hoverInstance.SetColor(OutlineColorId, hoverColor);
            hoverInstance.SetFloat(WidthId, hoverWidth);
            paramInstance = new Material(outline);
            paramInstance.SetColor(OutlineColorId, paramColor);
            paramInstance.SetFloat(WidthId, 0.005f);
        }
        ghostColor = ghostMaterial != null ? ghostMaterial.GetColor(ColorId) : new Color(0.6f, 0.85f, 1f, 0.35f);
        holder = new GameObject("CadModel").transform;
        volume.SetContent(holder.gameObject, fit: false);
        cgRing = MakeCollar("CG", cgMaterial);
        cpRing = MakeCollar("CP", cpMaterial);
        // Labels, lines and the demo's light live under the viewer, so they fold and scale with the volume.
        overlay = new GameObject("CadOverlay").transform;
        overlay.SetParent(transform, false);
        exploder = new CadExploder();
        annotations = new CadAnnotations(overlay);
        player = new CadDemoPlayer(overlay, exploder);
        player.Changed += OnDemoChanged;
        player.Frame += OnDemoFrame;
        stageLines = new CadLines("CadStageRing", overlay, CadLook.Light(overlay: false));
        stageChips = new CadChips("CadStageChip", overlay);
        scaleChip = new CadLabel(overlay, "CadScaleChip", CadLabel.Style.Tag);
        // Expand's ring of light on the desk, 155 mm from the base's centre toward the front right.
        expandButton = CadExpandButton.Create(transform, new Vector3(0.1096f, 0f, -0.1096f));
        expandButton.Pressed += ToggleExpand;
        if (apart == null && GetComponent<CadPartRaycaster>() == null) gameObject.AddComponent<CadPartRaycaster>();  // the ring's mouse
    }

    void OnEnable()
    {
        Active = this;
        session = CadSession.Get(mocks);
        session.ModelChanged += OnModelChanged;
        session.ViewChanged += OnViewChanged;
        session.InspectChanged += OnInspectChanged;
        session.DemoRequested += OnDemoRequested;
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
        player?.Stop(instant: true);
        GoHome(animate: false);
        reveal?.Reset();
        annotations?.Hide();
        stageRing = 0f;
        stageLines?.Hide();
        stageChips?.Hide();
        scaleChip?.Hide();
        if (Active == this) Active = null;
        if (session == null) return;
        session.ModelChanged -= OnModelChanged;
        session.ViewChanged -= OnViewChanged;
        session.InspectChanged -= OnInspectChanged;
        session.DemoRequested -= OnDemoRequested;
    }

    void OnDestroy()
    {
        if (workspace != null) workspace.modeChanged.RemoveListener(OnModeChanged);
        if (expandButton != null) expandButton.Pressed -= ToggleExpand;
        player?.Dispose();
        annotations?.Dispose();
        stageLines?.Destroy();
        stageChips?.Destroy();
        scaleChip?.Destroy();
        ghost?.Dispose();
        current?.Dispose();
        if (glowInstance != null) Destroy(glowInstance);
        if (hoverInstance != null) Destroy(hoverInstance);
        if (paramInstance != null) Destroy(paramInstance);
        if (stage != null) Destroy(stage.gameObject);
    }

    void ToggleExpand()
    {
        if (session != null) session.SetInspecting(!session.Inspecting);
    }

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
        player.Stop(instant: true);  // first: a demo has moved (or is turning parts of) the old model
        reveal.Reset();              // and the old lid gets its own materials back before it ghosts
        ClearParamLayers();
        var previous = current;
        current = model;
        shown = info;
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
        exploder.scale = explodeScale;
        exploder.axial = explodeAxial;
        exploder.radial = explodeRadial;
        exploder.Build(current, info.analysis, holder);
        reveal.Bind(current, CadDemoPlayer.RevealIds(info.analysis), exploder, ghostMaterial, ghostColor);
        UpdateMarkers(info.analysis, animate);
        ApplyFocus();  // before the glow, so dimming never touches the temporary glow layers
        if (animate) StartGlow(info.changedIds);
        if (onStage && apart != null) apart.Attach(current);
        annotations.Build(current, info, exploder, holder, assembled);
        player.Bind(holder, current, info, assembled);
        ShowParam();  // a row still pointed at lights the new version's parts
    }

    void Update()
    {
        var keys = Keyboard.current;
        if (keys != null && session != null)
        {
            if (keys.iKey.wasPressedThisFrame) session.SetInspecting(!session.Inspecting);
            if (keys.mKey.wasPressedThisFrame) session.SetMeasuring(!session.Measuring);
            if (keys.nKey.wasPressedThisFrame) session.SetMarkersShown(!session.MarkersShown);
        }

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
        exploder.Tick();
        SyncCollars();
    }

    // After every Update (the turntable, the grips): the demo poses the model, then the overlays follow it.
    void LateUpdate()
    {
        float dt = Time.deltaTime;
        player.hold = trip != null;
        player.Tick(dt);
        // The lid: lifted and glass during Run, glass on the stage; left alone while a focus dims the model.
        bool focused = !string.IsNullOrEmpty(focusId);
        reveal.Apply(focused ? 0f : player.RevealLift,
            focused ? 0f : Mathf.Max(player.RevealGlass, onStage && stageSettled ? 0.38f : 0f));
        var cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : transform.position - transform.forward * 0.6f;
        bool together = exploder.Progress < 0.02f;
        annotations.Tick(dt, eye, new CadAnnotations.State
        {
            markers = showMarkers && MarkersShown && together,
            measure = Measuring && together && !player.Running,
            names = exploder.Exploded,
            stage = onStage && stageSettled,
            demo = player.Kind,
            cgCollar = cgRing.gameObject.activeSelf ? cgRing : null,
            cpCollar = cpRing.gameObject.activeSelf ? cpRing : null,
            floorY = onStage ? -1000f : transform.position.y,
        });
        DrawStage(eye, dt);
        if (expandButton != null)
        {
            expandButton.SetExpanded(onStage);
            expandButton.SetInteractable(player.Kind != CadDemoPlayer.Launch);  // Fly leaves the desk
        }
    }

    // Demo: the session's requests in, the player's news out.

    void OnDemoRequested(bool run)
    {
        if (run) StartDemo();
        else StopDemo();
    }

    void OnDemoChanged(bool running)
    {
        if (session != null) session.SetDemoRunning(running);
        DemoChanged?.Invoke(running);
    }

    void OnDemoFrame(CadDemoFrame frame)
    {
        DemoFrame?.Invoke(frame);
        if (session != null) session.PublishDemoFrame(frame);
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

    // Inspect / Expand: spring the model to the stage and back

    void OnInspectChanged()
    {
        if (session.Inspecting == onStage) return;  // also raised when the number of parts off changes
        player.Stop(instant: true);  // a demo moves the holder; so does the trip
        if (session.Inspecting) GoToStage();
        else GoHome(animate: true);
    }

    void OnModeChanged(string mode)
    {
        player.Stop(instant: true);
        GoHome(animate: false);
    }

    void GoToStage()
    {
        onStage = true;
        stageSettled = false;
        StepAside(true);
        PoseStage();
        exploder.SetLiftWeight(0f, inspectSeconds);  // floating in front of you: an exploded view stays centred
        stageFitted = false;  // each visit fits afresh; versions during the visit keep the scale
        if (current == null) return;  // the first version goes straight up when it arrives (Swap)
        PlaceOnStage(animate: true);
        if (apart != null) apart.Attach(current);
    }

    void GoHome(bool animate)
    {
        if (!onStage) return;
        onStage = false;
        if (stageSettled)
        {
            stageSettled = false;
            if (session != null) session.SetMeasuring(measureBeforeStage);  // dimensions as they were before the stage
        }
        if (holder == null || volume.ContentRoot == null) return;  // leaving Play: the scene is being torn down
        StepAside(false);
        exploder.SetLiftWeight(1f, animate ? inspectSeconds : 0f);  // on the desk: lift off the base
        if (apart != null) apart.Release(instant: !animate);
        FlyTo(volume.ContentRoot, Vector3.zero, Quaternion.identity, 1f, animate ? inspectSeconds : 0f);
        if (session != null) session.SetInspecting(false);
    }

    // Settled on the stage: dimensions come on (until it goes home), the lid turns to glass (LateUpdate).
    void ArriveOnStage()
    {
        if (!onStage || stageSettled) return;
        stageSettled = true;
        if (session == null) return;
        measureBeforeStage = session.Measuring;
        session.SetMeasuring(true);
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
    const float StageMaxDistance = 0.62f;  // m from the eyes, flat screens only

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
            // But no further than keeps the box in front of CAD mode's VS Code screen (0.8 m), which would hide it.
            distance = Mathf.Min(Mathf.Max(distance, fit + stageSize.z * 0.5f), StageMaxDistance);
        }
        Vector3 centre = head.position + toward * distance + Vector3.down * stageDrop;
        centre.y = Mathf.Max(centre.y, transform.position.y + stageSize.y * 0.5f + 0.02f);
        stage.SetPositionAndRotation(centre, Quaternion.LookRotation(toward, Vector3.up));
    }

    // The assembled model, turned to show a corner (and its top), fitted to the stage box and centred on the stage.
    // Tall designs stay upright, flat ones tip back further, and a Run design faces you square on.
    void PlaceOnStage(bool animate)
    {
        var b = assembled;
        float footprint = Mathf.Max(b.size.x, b.size.z);
        stageTall = b.size.y > 1.2f * footprint;
        bool flat = b.size.y < 0.35f * footprint;
        var demo = Demo;
        float yaw = demo != null && demo.kind == CadDemoPlayer.Spin ? 0f : stageYaw;
        stageTurn = Quaternion.Euler(stageTall ? 0f : flat ? stageFlatTilt : stageTilt, 0f, 0f) * Quaternion.Euler(0f, yaw, 0f);
        var size = TurnedSize(b.size, stageTurn);
        stageScale = Mathf.Min(stageSize.x / Mathf.Max(size.x, 1e-4f), stageSize.y / Mathf.Max(size.y, 1e-4f),
            stageSize.z / Mathf.Max(size.z, 1e-4f));
        stageFitted = true;
        float s = trueSize ? 1f : stageScale;
        FlyTo(stage, -(stageTurn * b.center) * s, stageTurn, s, animate ? inspectSeconds : 0f);
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

    // Re-parent the holder (keeping where it is in the world) and spring it to a pose in its new parent.
    void FlyTo(Transform parent, Vector3 position, Quaternion rotation, float scale, float seconds)
    {
        if (trip != null) StopCoroutine(trip);
        trip = null;
        holder.SetParent(parent, true);
        bool toStage = parent == stage;
        if (seconds <= 0f || !isActiveAndEnabled)
        {
            holder.localPosition = position;
            holder.localRotation = rotation;
            holder.localScale = Vector3.one * scale;
            tripProgress = 1f;
            if (toStage) ArriveOnStage();
            return;
        }
        tripProgress = 0f;
        trip = StartCoroutine(Trip(position, rotation, scale, toStage));
    }

    // Position and turn on a critically damped spring (omega 9) along an arc rising 0.14 m above the straight line;
    // scale on a livelier one (omega 10; zeta 0.62 up to the stage, a gentle pop; 0.75 coming back).
    IEnumerator Trip(Vector3 position, Quaternion rotation, float scale, bool toStage)
    {
        var parent = holder.parent;
        Vector3 p0 = holder.localPosition, s0 = holder.localScale, s1 = Vector3.one * scale;
        Quaternion r0 = holder.localRotation;
        float t = 0f;
        while (t < TripSeconds)
        {
            t += Time.deltaTime;
            float e = CadLook.SpringAt(t, 9f, 1f), g = CadLook.SpringAt(t, 10f, toStage ? 0.62f : 0.75f);
            tripProgress = e;
            Vector3 arc = parent != null ? parent.InverseTransformVector(Vector3.up * (0.14f * 4f * e * (1f - e))) : Vector3.zero;
            holder.localPosition = Vector3.LerpUnclamped(p0, position, e) + arc;
            holder.localRotation = Quaternion.SlerpUnclamped(r0, rotation, e);
            holder.localScale = Vector3.LerpUnclamped(s0, s1, g);
            yield return null;
        }
        holder.localPosition = position;
        holder.localRotation = rotation;
        holder.localScale = s1;
        tripProgress = 1f;
        trip = null;
        if (toStage) ArriveOnStage();
    }

    // The stage's ring of light under the model, fading in over the last 45% of the trip, and its scale chip.
    void DrawStage(Vector3 eye, float dt)
    {
        float target = onStage && stage != null ? CadLook.Smooth((tripProgress - 0.55f) / 0.45f) : 0f;
        stageRing = target >= stageRing ? target : Mathf.MoveTowards(stageRing, target, dt / 0.25f);
        if (stageRing <= 0.003f || stage == null)
        {
            if (scaleChip.Alpha > 0f)
            {
                stageLines.Hide();
                stageChips.Hide();
                scaleChip.Hide();
            }
            else stageLines.Hide();
            return;
        }
        float s = trueSize ? 1f : stageScale;
        var size = TurnedSize(assembled.size, stageTurn) * s;
        Vector3 c = stage.position + Vector3.down * (size.y * (stageTall ? 0.5f : 0.34f) + 0.02f);
        float r = Mathf.Clamp(Mathf.Max(size.x, size.z) * 0.46f, 0.13f, 0.26f);
        stageLines.Begin(eye);
        stageLines.Disc(c, Vector3.up, r * 1.12f, CadLook.Alpha(CadLook.Hue, 0.3f * stageRing));
        stageLines.Circle(c, Vector3.up, r, CadLook.Line, CadLook.Alpha(Color.Lerp(Color.white, CadLook.Hue, 0.4f), 0.9f * stageRing), 72);
        stageLines.End();
        float shownScale = Mathf.Round(holder.lossyScale.x * 10f) / 10f;
        if (shownScale != scaleShown)
        {
            scaleShown = shownScale;
            scaleChip.SetText(trueSize ? "Real size, 1 : 1" : shownScale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "× real size");
        }
        scaleChip.SetAnchor(1);
        scaleChip.MoveTo(c + stage.right * (r * 0.94f) - stage.forward * (r * 0.34f), eye, dt);
        scaleChip.SetAlpha(stageRing > 0.3f ? stageRing : 0f);
        stageChips.Begin();
        scaleChip.Draw(stageChips);
        stageChips.End();
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

    // The highlighted parameter's parts, outlined, and its dimension drawn (the hub's params[].parts / .dimension).
    void ShowParam()
    {
        ClearParamLayers();
        var p = highlightedParam != null && session != null && session.Params != null ? session.Params.Find(highlightedParam) : null;
        annotations?.FocusDimension(p?.dimension);
        if (p?.parts == null || current == null || paramInstance == null || outlineMask == null) return;
        foreach (var id in p.parts)
            foreach (var r in current.Renderers(id))
            {
                if (IsLayer(r)) continue;
                AddGlowLayer(r, outlineMask, paramLayers);
                AddGlowLayer(r, paramInstance, paramLayers);
            }
    }

    void ClearParamLayers()
    {
        foreach (var layer in paramLayers)
        {
            if (layer == null) continue;
            layer.SetActive(false);
            Destroy(layer);
        }
        paramLayers.Clear();
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

    // CG / CP collars (the rocket's): rings round the body tube at the CG and the CP, sliding between versions.

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
        bool data = markers?.cg != null && markers.cp != null && markers.cg.Length >= 2 && markers.cp.Length >= 2 &&
                    analysis.diameterMm.HasValue;
        bool had = collarData;
        collarData = data;
        if (data)
        {
            // Collars stick out a little past the body tube; the primitive is 2 units tall, so y = half the thickness.
            float d = analysis.diameterMm.Value / 1000f * 1.45f;
            cgRing.localScale = new Vector3(d, 0.0012f, d);
            cpRing.localScale = new Vector3(d * 1.12f, 0.0012f, d * 1.12f);
            cgTo = markers.cg[1];
            cpTo = markers.cp[1];
            cgFrom = animate && had ? cgRing.localPosition.y : cgTo;
            cpFrom = animate && had ? cpRing.localPosition.y : cpTo;
            markerT = 0f;
        }
        SyncCollars();
    }

    // CG / CP belong to the assembled model at rest: hidden while exploded or flying, or when markers are off.
    void SyncCollars()
    {
        bool on = collarData && showMarkers && MarkersShown && exploder.Progress < 0.02f && !player.MovesModel;
        if (cgRing.gameObject.activeSelf == on) return;
        cgRing.gameObject.SetActive(on);
        cpRing.gameObject.SetActive(on);
    }

    static void SetHeight(Transform t, float y) => t.localPosition = new Vector3(0f, y, 0f);

    // Explode / focus

    void OnViewChanged(CadView view)
    {
        bool explode = view.explode == true;
        // A demo needs the model together and every part as it is: an explode or a focus ends it first.
        if (player.Running && (explode || view.focusId != focusId)) player.Stop(instant: true);
        exploder.SetTarget(explode);
        if (view.focusId == focusId) return;
        reveal.Reset();  // the focus dims every other part with the same material swap
        EndGlow();
        ClearDim();
        focusId = view.focusId;
        ApplyFocus();
    }

    /// <summary>
    /// Where <paramref name="part"/> belongs right now (assembled, or its exploded spot), in its parent's space.
    /// False if it isn't one of the current model's parts.
    /// </summary>
    public bool TryGetSlot(Transform part, out Vector3 localPosition) => exploder.TryGetSlot(part, out localPosition);

    /// <summary>A part taken off (or put back) in the Inspect view; explode leaves detached parts alone.</summary>
    public void SetDetached(Transform part, bool detached) => exploder.SetDetached(part, detached);

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
}
