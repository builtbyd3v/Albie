using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// The desk dock: light drawn on the desk near its front edge, no object. One rounded frame line of white light,
/// the mode buttons as neon rings with glowing letters (each casting a faint pool of its mode color), then Flat / Curve
/// and Reset for the open mode, and the hub status dot. Press a ring (poke, ray + pinch/trigger, or mouse) to unfold
/// that mode: the ring rises off the desk on a column of light and turns toward the head. Press it again to fold it.
/// Rings brighten as a pointer comes near and run a comet of light while hovered. The dock comes in in two beats: the
/// frame slides along the front edge in from the user's right, then the rings plop on left to right (ForgeTheme.DockSlide*).
/// Input stays flat: each button is an invisible raycast Image + Button on the dock canvas that never moves. The light
/// is quads under "Light" (Forge/NeonFrame, NeonRing, LightPool) and 3D Inter labels, styled by <see cref="ForgeTheme"/>;
/// the ring pieces come from <see cref="DockRing"/>, which also makes one on its own. Built from <see cref="Workspace.modes"/>.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class DeskDock : MonoBehaviour
{
    [SerializeField] RectTransform row;
    [SerializeField] Button buttonTemplate;
    [SerializeField] CanvasGroup group;

    [Tooltip("Distance from the desk's front edge to the row of buttons (m).")]
    public float inset = 0.09f;

    const float GlowPad = DockRing.GlowPad;  // room around each ring's quad for its glow (mm)

    // One button: its input (fixed in place) and its light (animated).
    class Ring
    {
        public string modeId;  // null for a tool
        public bool isMode;
        public Button button;
        public DockButton input;
        public Color hue;
        public float radius;
        public Vector3 center;  // dock canvas space (mm)
        public Transform lift, aim;
        public MeshRenderer ring, pool, column, label;
        public TextMeshPro text;
        public bool selected, enabled = true;
        public float near, hover, press, select, enable = 1f;
        public float liftFrom, liftTo, liftStart = -10f;
        public float plopStart = -10f;  // entrance: when this ring plops onto the dock (-10 = long landed)
        public Quaternion aimRotation = Quaternion.identity;
        public Vector4 shown = new Vector4(-1f, -1f, -1f, -1f);  // last values sent to the shaders
    }

    static readonly int SizeId = Shader.PropertyToID("_Size"), FadeId = Shader.PropertyToID("_Fade");
    static readonly int BoxId = Shader.PropertyToID("_Box");
    static readonly int CornerId = Shader.PropertyToID("_Corner"), DividerId = Shader.PropertyToID("_Divider");
    static readonly int HubId = Shader.PropertyToID("_Hub"), HubColorId = Shader.PropertyToID("_HubColor");
    static readonly int HubGlowId = Shader.PropertyToID("_HubGlow");

    readonly List<Ring> rings = new List<Ring>();
    readonly List<Button> tools = new List<Button>();
    Workspace workspace;
    Transform lightRoot;
    MeshRenderer frame;
    TMP_Text layoutLabel;
    Coroutine anim;
    MaterialPropertyBlock block;
    Color hubColor = ForgeTheme.HubConnecting, hubGlow = ForgeTheme.HubConnectingGlow;
    float dividerX, shownFade = -1f;
    bool frameDirty = true;
    // Hub status in words beside the corner light: shown for a few seconds after it changes, and while a pointer is
    // near the light, so mock mode is clear without shouting.
    TextMeshPro hubLabel;
    string hubText = "Connecting…";
    float hubShown, hubAttentionUntil;
    const float HubLabelMm = 10f, HubAttention = 4f, HubNearRange = 80f;

    // Interaction SDK pointers, found when the dock shows (the rig is picked by then).
    RayInteractor[] rays = new RayInteractor[0];
    PokeInteractor[] pokes = new PokeInteractor[0];
    readonly Vector3[] pointers = new Vector3[8];
    int pointerCount;

    public bool IsShown { get; private set; }

    void OnEnable()
    {
        ForgeNet.StatusChanged += ShowStatus;
        ShowStatus(ForgeNet.Status);
    }

    void OnDisable() => ForgeNet.StatusChanged -= ShowStatus;

    void ShowStatus(ForgeNetStatus status)
    {
        hubColor = status == ForgeNetStatus.Live ? ForgeTheme.HubLive : status == ForgeNetStatus.Mock ? ForgeTheme.HubMock : ForgeTheme.HubConnecting;
        hubGlow = status == ForgeNetStatus.Live ? ForgeTheme.HubLiveGlow : status == ForgeNetStatus.Mock ? ForgeTheme.HubMockGlow : ForgeTheme.HubConnectingGlow;
        hubText = status == ForgeNetStatus.Live ? "Hub live" : status == ForgeNetStatus.Mock ? "Mock data" : "Connecting…";
        if (hubLabel != null) hubLabel.text = hubText;
        hubAttentionUntil = Time.time + HubAttention;
        frameDirty = true;
    }

    public void Build(Workspace workspace)
    {
        this.workspace = workspace;
        if (block == null) block = new MaterialPropertyBlock();
        foreach (var r in rings) Destroy(r.button.gameObject);
        rings.Clear();
        tools.Clear();
        if (lightRoot != null) Destroy(lightRoot.gameObject);
        buttonTemplate.gameObject.SetActive(false);

        lightRoot = new GameObject("Light").transform;
        lightRoot.SetParent(transform, false);
        lightRoot.gameObject.layer = gameObject.layer;

        // Row: modes, divider, tools, spaced evenly and centered.
        int modeCount = workspace.modes.Count;
        float width = modeCount * ForgeTheme.DockModeRing + 2f * ForgeTheme.DockToolRing + 1.2f + (modeCount + 2) * ForgeTheme.DockGap;
        float x = -width * 0.5f;
        foreach (var mode in workspace.modes)
        {
            string id = mode.id;
            AddRing($"Mode {id}", mode.label, true, mode.color, ForgeTheme.DockModeRing, ForgeTheme.DockModeLabel, ref x, () => workspace.Toggle(id)).modeId = id;
        }
        dividerX = x + 0.6f;
        x += 1.2f + ForgeTheme.DockGap;
        layoutLabel = AddRing("Dock layout", "", false, ForgeTheme.Tool, ForgeTheme.DockToolRing, ForgeTheme.DockToolLabel, ref x, workspace.ToggleLayout).text;
        AddRing("Dock reset", "Reset", false, ForgeTheme.Tool, ForgeTheme.DockToolRing, ForgeTheme.DockToolLabel, ref x, workspace.ResetLayout);

        var size = new Vector2(width + 2f * ForgeTheme.DockPadding, ForgeTheme.DockHeight);
        ((RectTransform)transform).sizeDelta = size;
        SyncHitArea();
        frame = DockRing.Layer("Frame", lightRoot, ForgeTheme.FrameLight, ForgeTheme.Quad, new Vector2(size.x + 2f * GlowPad + 20f, size.y + 2f * GlowPad + 20f), 0f, gameObject.layer);
        frameDirty = true;
        BuildHubLabel(size);

        workspace.layoutChanged.RemoveListener(ShowLayout);
        workspace.layoutChanged.AddListener(ShowLayout);
        ShowLayout(workspace.layout);
        SetActiveMode(workspace.ActiveMode);
    }

    // The Interaction SDK only lets rays and pokes hit inside its clippers' boxes, which the prefab saved at its
    // original size. Fit them to the dock, or the outer buttons can't be pressed in XR (the mouse doesn't care).
    void SyncHitArea()
    {
        Vector2 size = ((RectTransform)transform).sizeDelta;
        foreach (var clipper in GetComponentsInChildren<BoundsClipper>(true))
            clipper.Size = new Vector3(size.x, size.y, clipper.Size.z);
    }

    Ring AddRing(string name, string label, bool isMode, Color hue, float diameter, float labelMm, ref float x, UnityAction onClick)
    {
        var button = Instantiate(buttonTemplate, row);
        button.gameObject.SetActive(true);
        button.name = name;
        button.onClick.AddListener(onClick);
        var rt = (RectTransform)button.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = Vector2.one * (diameter + 8f);  // a little extra to aim at
        rt.anchoredPosition = new Vector2(x + diameter * 0.5f, 0f);
        var input = button.GetComponent<DockButton>();
        if (input == null) input = button.gameObject.AddComponent<DockButton>();

        var r = new Ring { isMode = isMode, button = button, input = input, hue = hue, radius = diameter * 0.5f, center = new Vector3(x + diameter * 0.5f, 0f, 0f) };
        x += diameter + ForgeTheme.DockGap;
        if (!isMode) tools.Add(button);

        var root = new GameObject(name).transform;
        root.SetParent(lightRoot, false);
        root.localPosition = r.center;
        float quad = diameter + 2f * GlowPad;
        int layer = gameObject.layer;
        r.pool = DockRing.Layer("Pool", root, ForgeTheme.PoolLight, ForgeTheme.Quad, Vector2.one * (diameter + 60f), -0.1f, layer);
        r.column = DockRing.Layer("Column", root, ForgeTheme.ColumnLight, ColumnMesh, Vector2.one * quad, 0f, layer);
        r.column.enabled = false;
        r.lift = Child("Lift", root);
        r.aim = Child("Aim", r.lift);
        r.ring = DockRing.Layer("Ring", r.aim, ForgeTheme.RingLight, ForgeTheme.Quad, Vector2.one * quad, -0.5f, layer);
        r.text = DockRing.Label(label, r.aim, diameter, labelMm, isMode, layer);
        r.label = r.text.GetComponent<MeshRenderer>();
        rings.Add(r);
        return r;
    }

    Transform Child(string name, Transform parent)
    {
        var t = new GameObject(name).transform;
        t.gameObject.layer = gameObject.layer;
        t.SetParent(parent, false);
        return t;
    }

    public void SetActiveMode(string modeId)
    {
        float now = Time.time;
        foreach (var r in rings)
        {
            if (r.isMode)
            {
                bool selected = modeId != null && r.modeId == modeId;
                if (selected != r.selected) StartLift(r, selected, now);
                r.selected = selected;
            }
            else
            {
                // Layout and Reset only mean something while a mode is open.
                r.enabled = modeId != null;
                r.button.interactable = r.enabled;
            }
        }
    }

    // The button says what pressing it does.
    void ShowLayout(WorkspaceLayout layout)
    {
        if (layoutLabel != null) layoutLabel.text = layout == WorkspaceLayout.Curved ? "Flat" : "Curve";
    }

    /// <summary>Lay the dock flat on the desk, centered along the front edge, readable from the seat, and bring it in: the
    /// frame slides in from the user's right, then the rings plop on left to right. <paramref name="reposition"/>: already
    /// showing and the front edge moved (the user went round the desk): just move it, no entrance.</summary>
    public void Show(Transform deskFrame, float deskWidth, float deskDepth, bool reposition = false)
    {
        transform.SetParent(deskFrame, false);
        transform.localPosition = new Vector3(0f, 0.004f, Mathf.Min(inset, deskDepth * 0.5f));
        transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        float width = ((RectTransform)transform).sizeDelta.x * ForgePanel.MetersPerUnit;
        float fit = Mathf.Min(1f, (deskWidth - 0.04f) / Mathf.Max(width, 0.01f));
        Vector3 scale = Vector3.one * ForgePanel.MetersPerUnit * Mathf.Max(0.5f, fit);
        DockRing.FindInteractors(out rays, out pokes);

        bool wasShown = IsShown && gameObject.activeInHierarchy;
        IsShown = true;
        gameObject.SetActive(true);
        if (reposition && wasShown && anim == null)
        {
            transform.localScale = scale;
            return;
        }
        if (anim != null) StopCoroutine(anim);
        transform.localScale = scale;
        group.alpha = 1f;
        group.interactable = group.blocksRaycasts = false;
        // Entrance: full size and light from the start but no rings; the frame comes in along the front edge from +X (the
        // user's right) over DockSlideTime, then each ring plops on in row order (Ring.plopStart, see Animate). Nothing is
        // pressable until the last ring has landed.
        float now = Time.time;
        float slide = deskWidth * 0.5f + ((RectTransform)transform).sizeDelta.x * scale.x * 0.5f;  // starts just off the desk's right end
        Vector3 end = transform.localPosition;
        float total = ForgeTheme.DockSlideTime + Mathf.Max(0, rings.Count - 1) * ForgeTheme.DockPlopStagger + ForgeTheme.DockPlopTime;
        for (int i = 0; i < rings.Count; i++) rings[i].plopStart = now + ForgeTheme.DockSlideTime + i * ForgeTheme.DockPlopStagger;
        anim = StartCoroutine(ForgeTween.Run(total, 0f, k =>
        {
            float s = Mathf.Clamp01(k * total / ForgeTheme.DockSlideTime);
            transform.localPosition = end + Vector3.right * (slide * (1f - ForgeTween.EaseOutCubic(s)));
        }, () =>
        {
            anim = null;
            transform.localPosition = end;
            group.interactable = group.blocksRaycasts = true;
        }));
    }

    public void Hide(bool immediate = false)
    {
        IsShown = false;
        if (!gameObject.activeInHierarchy) return;
        if (immediate)
        {
            gameObject.SetActive(false);
            return;
        }
        Vector3 scale = transform.localScale;
        Animate(scale, scale * 0.6f, group.alpha, 0f, 0.2f, () => gameObject.SetActive(false));
    }

    void Animate(Vector3 fromScale, Vector3 toScale, float fromAlpha, float toAlpha, float duration, System.Action done)
    {
        if (anim != null) StopCoroutine(anim);
        group.interactable = toAlpha > 0f;
        group.blocksRaycasts = toAlpha > 0f;
        anim = StartCoroutine(ForgeTween.Run(duration, 0f, k =>
        {
            float e = ForgeTween.EaseOutCubic(k);
            transform.localScale = Vector3.LerpUnclamped(fromScale, toScale, e);
            group.alpha = Mathf.Lerp(fromAlpha, toAlpha, e);
        }, done));
    }

    // ---- Light ----

    void LateUpdate()
    {
        if (block == null || frame == null) return;
        float dt = Time.deltaTime, now = Time.time, fade = group.alpha;
        bool fadeChanged = !Mathf.Approximately(fade, shownFade);
        shownFade = fade;
        FindPointers();
        Transform head = workspace != null && workspace.presence != null ? workspace.presence.head : null;
        Vector3 eye = head != null ? transform.InverseTransformPoint(head.position) : new Vector3(0f, -400f, -500f);
        foreach (var r in rings) Animate(r, dt, now, fade, fadeChanged, eye);
        if (frameDirty || fadeChanged) ShowFrame(fade);
        ShowHubLabel(dt, now, fade);
    }

    // Hub light position (dock canvas mm), the same spot ShowFrame draws the dot at.
    static Vector2 HubDot(Vector2 size) => new Vector2(size.x * 0.5f - 21.3f, size.y * 0.5f - 16.4f);

    // Just outside the dock's right edge, level with the light, so it never sits over a ring.
    void BuildHubLabel(Vector2 size)
    {
        hubLabel = new GameObject("Hub status", typeof(RectTransform)).AddComponent<TextMeshPro>();
        hubLabel.gameObject.layer = gameObject.layer;
        hubLabel.transform.SetParent(lightRoot, false);
        var rt = hubLabel.rectTransform;
        rt.pivot = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(120f, HubLabelMm * 2f);
        rt.localPosition = new Vector3(size.x * 0.5f + 10f, HubDot(size).y, -0.8f);
        if (ForgeTheme.Font != null)
        {
            hubLabel.font = ForgeTheme.Font;
            hubLabel.fontSharedMaterial = ForgeTheme.Font.material;
        }
        hubLabel.fontSize = HubLabelMm * 10f;  // TextMeshPro (3D) is 0.1 unit per point
        hubLabel.characterSpacing = 4f;
        hubLabel.alignment = TextAlignmentOptions.MidlineLeft;
        hubLabel.textWrappingMode = TextWrappingModes.NoWrap;
        hubLabel.color = new Color(1f, 1f, 1f, 0.7f);
        hubLabel.alpha = 0f;
        hubLabel.text = hubText;
        var r = hubLabel.GetComponent<MeshRenderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        hubShown = 0f;
    }

    void ShowHubLabel(float dt, float now, float fade)
    {
        if (hubLabel == null) return;
        Vector2 dot = HubDot(((RectTransform)transform).sizeDelta);
        bool near = false;
        for (int i = 0; i < pointerCount && !near; i++)
            near = ((Vector2)pointers[i] - dot).sqrMagnitude < HubNearRange * HubNearRange;
        float target = now < hubAttentionUntil || near ? 1f : 0f;
        float shown = Mathf.MoveTowards(hubShown, target, dt / ForgeTheme.HoverFade);
        float alpha = 0.7f * shown * fade;
        if (Mathf.Approximately(shown, hubShown) && Mathf.Approximately(hubLabel.alpha, alpha)) return;
        hubShown = shown;
        hubLabel.alpha = alpha;
    }

    void Animate(Ring r, float dt, float now, float fade, bool force, Vector3 eye)
    {
        // Nearness: the closest pointer (a ray's spot on the desk, or a fingertip) to this ring's center.
        float closest = float.MaxValue;
        for (int i = 0; i < pointerCount; i++) closest = Mathf.Min(closest, (pointers[i] - r.center).sqrMagnitude);
        float nearTarget = pointerCount > 0 ? Mathf.Clamp01(1f - Mathf.Sqrt(closest) / ForgeTheme.DockNearRange) : 0f;
        r.near = Mathf.Lerp(r.near, nearTarget, 1f - Mathf.Exp(-dt * 12f));
        r.hover = Mathf.MoveTowards(r.hover, r.enabled && r.input.Hovered ? 1f : 0f, dt / ForgeTheme.HoverFade);
        r.press = Mathf.MoveTowards(r.press, r.enabled && r.input.Pressed ? 1f : 0f, dt / ForgeTheme.PressTime);
        r.select = Mathf.MoveTowards(r.select, r.selected ? 1f : 0f, dt / ForgeTheme.SelectFade);
        r.enable = Mathf.MoveTowards(r.enable, r.enabled ? 1f : 0f, dt / ForgeTheme.HoverFade);
        float lift = Lift(r, now);
        float plop = Mathf.Clamp01((now - r.plopStart) / ForgeTheme.DockPlopTime);  // entrance: 0 before its turn, 1 once landed

        // A raised ring turns a little toward the head (smoothed, so leaning moves it); lowered, it lies flat.
        Quaternion aimTarget = Quaternion.identity;
        if (r.selected)
        {
            Vector3 toEye = eye - (r.center + new Vector3(0f, 0f, -ForgeTheme.DockRise));
            Vector3 up = Vector3.back;  // canvas -Z is up off the desk
            if (toEye.sqrMagnitude > 1f)
                aimTarget = Quaternion.FromToRotation(up, Vector3.RotateTowards(up, toEye.normalized, ForgeTheme.DockTilt * Mathf.Deg2Rad, 0f));
        }
        r.aimRotation = Quaternion.Slerp(r.aimRotation, aimTarget, 1f - Mathf.Exp(-dt * 3f / ForgeTheme.HeadSmoothing));

        var state = new Vector4(r.near, r.hover + 2f * r.press, r.select + 2f * r.enable, lift + 4f * plop);
        bool moving = Quaternion.Angle(r.aim.localRotation, r.aimRotation) > 0.05f;
        if (!force && !moving && (state - r.shown).sqrMagnitude < 1e-7f) return;
        r.shown = state;

        float scale = (1f + 0.05f * r.near * (1f - r.select)) * (1f + 0.06f * r.select) * (1f - 0.05f * r.press);
        if (plop < 1f) scale *= Mathf.Max(0f, ForgeTween.EaseOutBack(plop));  // plops on from nothing with a small overshoot
        r.lift.localPosition = new Vector3(0f, 0f, -ForgeTheme.DockRise * lift);
        r.lift.localScale = Vector3.one * scale;
        r.aim.localRotation = r.aimRotation;
        r.column.enabled = r.select > 0.001f || lift > 0.001f;
        if (r.column.enabled)
        {
            Vector3 s = r.column.transform.localScale;
            r.column.transform.localScale = new Vector3(s.x, s.y, Mathf.Max(lift, 0.001f));
        }

        DockRing.SetLook(block, r.ring, r.column, r.pool, r.label, r.hue, r.radius, r.near, r.hover, r.press, r.select, r.enable, fade, plop);
    }

    void StartLift(Ring r, bool up, float now)
    {
        r.liftFrom = Lift(r, now);
        r.liftTo = up ? 1f : 0f;
        r.liftStart = now;
    }

    // Springs up past the top and settles; going down it stops at the desk instead of dipping into it.
    static float Lift(Ring r, float now)
    {
        float k = Mathf.Clamp01((now - r.liftStart) / ForgeTheme.LiftTime);
        float v = Mathf.LerpUnclamped(r.liftFrom, r.liftTo, ForgeTween.Spring(k));
        return r.liftTo > 0f ? v : Mathf.Max(0f, v);
    }

    void ShowFrame(float fade)
    {
        frameDirty = false;
        Vector2 size = ((RectTransform)transform).sizeDelta;
        Vector3 quad = frame.transform.localScale;
        block.Clear();
        block.SetVector(SizeId, new Vector4(quad.x, quad.y, 0f, 0f));
        block.SetVector(BoxId, new Vector4(size.x * 0.5f, size.y * 0.5f, 0f, 0f));
        block.SetFloat(CornerId, ForgeTheme.DockCorner);
        block.SetVector(DividerId, new Vector4(dividerX, ForgeTheme.DockDivider * 0.5f, 1f, 0f));
        block.SetVector(HubId, new Vector4(size.x * 0.5f - 21.3f, size.y * 0.5f - 16.4f, 3.25f, 1f));
        block.SetColor(HubColorId, hubColor);
        block.SetColor(HubGlowId, hubGlow);
        block.SetFloat(FadeId, fade);
        frame.SetPropertyBlock(block);
    }

    // Where the pointers are this frame, in dock space (mm): rays where they meet the desk, fingertips as they are.
    void FindPointers() => pointerCount = DockRing.Pointers(transform, rays, pokes, pointers);

    // Nine faint rings from the desk up to a raised ring, the top one brightest: a hologram column. Ring heights are in
    // mm at full rise; the column's z scale follows the lift.
    static Mesh columnMesh;
    static Mesh ColumnMesh
    {
        get
        {
            if (columnMesh != null) return columnMesh;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            for (int k = 1; k <= 9; k++)
            {
                float z = -(ForgeTheme.DockRise - 2.6f * k);
                var c = new Color(1f, 1f, 1f, 0.6f - 0.065f * k);
                int v = vertices.Count;
                vertices.Add(new Vector3(-0.5f, -0.5f, z));
                vertices.Add(new Vector3(0.5f, -0.5f, z));
                vertices.Add(new Vector3(-0.5f, 0.5f, z));
                vertices.Add(new Vector3(0.5f, 0.5f, z));
                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(0f, 1f));
                uvs.Add(new Vector2(1f, 1f));
                colors.Add(c);
                colors.Add(c);
                colors.Add(c);
                colors.Add(c);
                triangles.AddRange(new[] { v, v + 2, v + 1, v + 2, v + 3, v + 1 });
            }
            columnMesh = new Mesh { name = "ForgeLightColumn" };
            columnMesh.SetVertices(vertices);
            columnMesh.SetUVs(0, uvs);
            columnMesh.SetColors(colors);
            columnMesh.SetTriangles(triangles, 0);
            columnMesh.RecalculateBounds();
            return columnMesh;
        }
    }

    /// <summary>
    /// Where a mode's ring is in the world (its light, raised or not), for the workspace to unfold out of and fold back
    /// into. False while the dock is hidden, or if no ring is that mode's.
    /// </summary>
    public bool TryGetRingPoint(string modeId, out Vector3 world)
    {
        world = default;
        if (!gameObject.activeInHierarchy) return false;
        foreach (var r in rings)
        {
            if (!r.isMode || r.modeId != modeId || r.lift == null) continue;
            world = r.lift.position;
            return true;
        }
        return false;
    }
}
