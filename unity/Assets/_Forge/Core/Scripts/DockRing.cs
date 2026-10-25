using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// One of the dock's neon rings on its own: the ring of light with its glowing Inter label and a pool of its hue under
/// it, plus the input the dock uses, an invisible raycast Image + Button (+ <see cref="DockButton"/>) on a small
/// world-space canvas, so the mouse (<see cref="DesktopRig"/>), Interaction SDK rays and pokes (PointableCanvasModule)
/// all press it the same way. Faces -Z like a canvas: parent it where it goes and turn its +Z away from the viewer.
/// <see cref="DeskDock"/> builds its rings from the same pieces (<see cref="Layer"/>, <see cref="Label"/>,
/// <see cref="SetLook"/>, <see cref="Pointers"/>) and animates them itself; a ring made with <see cref="Create"/>
/// animates itself in LateUpdate (brightens as a pointer comes near, comet while hovered, dips while pressed).
/// The pet's START button on the first arrival is one of these.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class DockRing : MonoBehaviour
{
    /// <summary>Room around a ring's quad for its glow (mm).</summary>
    public const float GlowPad = 30f;

    static readonly int SizeId = Shader.PropertyToID("_Size"), RadiusId = Shader.PropertyToID("_Radius");
    static readonly int HueId = Shader.PropertyToID("_Hue"), HueTId = Shader.PropertyToID("_HueT");
    static readonly int NearId = Shader.PropertyToID("_Near"), HoverId = Shader.PropertyToID("_Hover");
    static readonly int SelectedId = Shader.PropertyToID("_Selected"), EnabledId = Shader.PropertyToID("_Enabled");
    static readonly int FadeId = Shader.PropertyToID("_Fade"), OpacityId = Shader.PropertyToID("_Opacity");
    static readonly int ImprintId = Shader.PropertyToID("_Imprint");
    static readonly int HalfId = Shader.PropertyToID("_Half"), WashId = Shader.PropertyToID("_Wash");
    static readonly int FaceColorId = Shader.PropertyToID("_FaceColor"), GlowColorId = Shader.PropertyToID("_GlowColor");
    static readonly int UnderlayColorId = Shader.PropertyToID("_UnderlayColor");

    [SerializeField] Button button;
    [SerializeField] DockButton input;
    [SerializeField] CanvasGroup group;
    [SerializeField] Transform lift;
    [SerializeField] MeshRenderer ring, pool, label;
    [SerializeField] TextMeshPro text;
    [SerializeField] Color hue = ForgeTheme.Tool;
    [SerializeField] float radius = ForgeTheme.DockModeRing * 0.5f;
    [SerializeField] float half;          // pill: half length of the straight part (mm); 0 = a ring
    [SerializeField] float wash = 1f;     // the pool's dark contrast wash (a ring on its own has no dock behind it)

    public Button Button => button;
    public DockButton Input => input;
    public TextMeshPro Text => text;
    public Color Hue => hue;
    public float Radius => radius;
    /// <summary>Half the button's width (mm): the radius for a ring, radius + the straight part for a pill.</summary>
    public float HalfWidth => radius + half;
    /// <summary>Off: dimmed and not pressable (the Button's interactable follows).</summary>
    public bool Interactable
    {
        get => interactable;
        set { interactable = value; if (button != null) button.interactable = value; }
    }

    bool interactable = true;
    float near, hover, press, enable = 1f;
    Vector4 shown = new Vector4(-1f, -1f, -1f, -1f);
    MaterialPropertyBlock block;
    RayInteractor[] rays = new RayInteractor[0];
    PokeInteractor[] pokes = new PokeInteractor[0];
    readonly Vector3[] pointers = new Vector3[8];
    float nextInteractorSearch;

    /// <summary>
    /// A ring of light with <paramref name="label"/> in <paramref name="hue"/>, <paramref name="diameterMm"/> across,
    /// under <paramref name="parent"/> (local pose identity). <paramref name="onClick"/> may be null (add to
    /// <see cref="Button"/>.onClick later). The label is sized like a dock mode label of that diameter.
    /// </summary>
    public static DockRing Create(Transform parent, string label, Color hue, float diameterMm, UnityAction onClick)
        => Create(parent, label, hue, diameterMm, diameterMm, onClick);

    /// <summary>
    /// A pill of the same light (<paramref name="widthMm"/> by <paramref name="heightMm"/>, round ends): the dock's look for
    /// a button that stands on its own, so it also gets a stronger dark wash under it (there is no dock behind it).
    /// </summary>
    public static DockRing Create(Transform parent, string label, Color hue, float heightMm, float widthMm, UnityAction onClick)
    {
        float diameterMm = heightMm;
        float halfMm = Mathf.Max(0f, (widthMm - heightMm) * 0.5f);
        var go = new GameObject("Ring " + label, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(CanvasGroup));
        if (parent != null)
        {
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
        }
        go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)go.transform;
        float quad = diameterMm + 2f * GlowPad;
        rt.sizeDelta = new Vector2(quad + 2f * halfMm, quad);
        rt.localScale = Vector3.one * ForgePanel.MetersPerUnit;

        // Input: an invisible plate that never moves, a little bigger than the ring to aim at.
        var hit = new GameObject("Button", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        hit.gameObject.layer = go.layer;
        hit.rectTransform.SetParent(rt, false);
        hit.rectTransform.sizeDelta = new Vector2(diameterMm + 2f * halfMm + 8f, diameterMm + 8f);
        hit.color = Color.clear;
        hit.canvasRenderer.cullTransparentMesh = true;
        var button = hit.gameObject.AddComponent<Button>();
        button.targetGraphic = hit;
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        if (onClick != null) button.onClick.AddListener(onClick);
        var input = hit.gameObject.AddComponent<DockButton>();

        // Light: the pool on the plane, the ring and its label a hair in front (canvas -Z).
        var light = new GameObject("Light").transform;
        light.gameObject.layer = go.layer;
        light.SetParent(rt, false);
        var r = go.AddComponent<DockRing>();
        r.button = button;
        r.input = input;
        r.group = go.GetComponent<CanvasGroup>();
        r.hue = hue;
        r.radius = diameterMm * 0.5f;
        r.half = halfMm;
        r.wash = halfMm > 0f ? 2.2f : 1f;
        r.pool = Layer("Pool", light, ForgeTheme.PoolLight, ForgeTheme.Quad, new Vector2(diameterMm + 60f + 2f * halfMm, diameterMm + 60f), -0.1f, go.layer);
        r.lift = new GameObject("Lift").transform;
        r.lift.gameObject.layer = go.layer;
        r.lift.SetParent(light, false);
        r.ring = Layer("Ring", r.lift, ForgeTheme.RingLight, ForgeTheme.Quad, new Vector2(quad + 2f * halfMm, quad), -0.5f, go.layer);
        r.text = Label(label, r.lift, diameterMm + 2f * halfMm, ForgeTheme.DockModeLabel * diameterMm / ForgeTheme.DockModeRing, true, go.layer);
        r.label = r.text.GetComponent<MeshRenderer>();
        return r;
    }

    // ---- Pieces shared with DeskDock ----

    /// <summary>A quad (or any mesh) of light: no shadows, no probes, sized in mm, at <paramref name="z"/> in canvas space.</summary>
    public static MeshRenderer Layer(string name, Transform parent, Material material, Mesh mesh, Vector2 sizeMm, float z, int layer)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 0f, z);
        go.transform.localScale = new Vector3(sizeMm.x, sizeMm.y, 1f);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return r;
    }

    /// <summary>A ring's label: Inter drawn as light (ForgeTheme.GlowText), <paramref name="labelMm"/> tall, centred, 0.8 mm in front.</summary>
    public static TextMeshPro Label(string label, Transform parent, float diameter, float labelMm, bool bold, int layer)
    {
        var text = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshPro>();
        text.gameObject.layer = layer;
        text.transform.SetParent(parent, false);
        text.transform.localPosition = new Vector3(0f, 0f, -0.8f);
        text.font = ForgeTheme.Font;
        text.fontSharedMaterial = ForgeTheme.GlowText;
        text.fontSize = labelMm * 10f;  // TextMeshPro (3D) is 0.1 unit per point, and 1 unit = 1 mm here
        text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        text.characterSpacing = bold ? 8f : 5f;
        text.alignment = TextAlignmentOptions.Midline;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.rectTransform.sizeDelta = new Vector2(diameter, labelMm * 2f);
        text.text = label;
        var r = text.GetComponent<MeshRenderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return text;
    }

    /// <summary>
    /// Drive one ring's shaders: the ring quad (and its light column when given and enabled), the pool and the label,
    /// from the smoothed near / hover / press / select / enable values (0..1) and the dock's fade. <paramref name="reveal"/>
    /// (0..1, normally 1) hides the pool, label and icon while a ring is still appearing. With an <paramref name="icon"/>
    /// (a mode's white svg filling the ring) the label is a hint that fades in only while hovered.
    /// </summary>
    public static void SetLook(MaterialPropertyBlock block, MeshRenderer ring, MeshRenderer column, MeshRenderer pool, MeshRenderer label,
        Color hue, float radius, float near, float hover, float press, float select, float enable, float fade, float reveal = 1f,
        SpriteRenderer icon = null, float half = 0f, float wash = -1f)
    {
        float quad = 2f * (radius + GlowPad);
        block.Clear();
        block.SetFloat(SizeId, quad);
        block.SetFloat(RadiusId, radius);
        block.SetFloat(HalfId, half);
        block.SetColor(HueId, hue);
        block.SetFloat(HueTId, ForgeTheme.HueStrength);
        block.SetFloat(NearId, near);
        block.SetFloat(HoverId, hover);
        block.SetFloat(SelectedId, select);
        block.SetFloat(EnabledId, enable);
        block.SetFloat(FadeId, fade);
        ring.SetPropertyBlock(block);
        if (column != null && column.enabled) column.SetPropertyBlock(block);

        block.Clear();
        block.SetFloat(SizeId, 2f * radius + 60f);
        block.SetFloat(RadiusId, radius);
        block.SetFloat(HalfId, half);
        if (wash >= 0f) block.SetFloat(WashId, wash);
        block.SetColor(HueId, hue);
        block.SetFloat(HueTId, ForgeTheme.HueStrength);
        block.SetFloat(OpacityId, Mathf.Lerp(0.3f + 0.55f * near, 0.9f, select) * Mathf.Lerp(0.4f, 1f, enable) * reveal);
        block.SetFloat(ImprintId, select);
        block.SetFloat(FadeId, fade);
        pool.SetPropertyBlock(block);

        float alpha = Mathf.Lerp(0.7f + 0.3f * near, 1f, select) * Mathf.Lerp(0.25f, 1f, enable) * fade * reveal;
        // Under an icon the label is a hint: it fades in only while a pointer is on the button.
        float textAlpha = icon != null ? alpha * hover : alpha;
        Color halo = Color.Lerp(Color.white, hue, 0.6f);
        block.Clear();
        block.SetColor(FaceColorId, new Color(1f, 1f, 1f, textAlpha));
        block.SetColor(GlowColorId, new Color(1f, 1f, 1f, 0.55f * textAlpha));
        block.SetColor(UnderlayColorId, new Color(halo.r, halo.g, halo.b, 0.32f * textAlpha));
        label.SetPropertyBlock(block);
        if (icon != null) icon.color = new Color(1f, 1f, 1f, alpha);
    }

    /// <summary>The Interaction SDK pointers in the scene (found when something shows; the rig is picked by then).</summary>
    public static void FindInteractors(out RayInteractor[] rays, out PokeInteractor[] pokes)
    {
        rays = FindObjectsByType<RayInteractor>(FindObjectsSortMode.None);
        pokes = FindObjectsByType<PokeInteractor>(FindObjectsSortMode.None);
    }

    /// <summary>
    /// Where the pointers are this frame in <paramref name="space"/> (canvas mm): the mouse ray and the SDK rays where
    /// they meet its z = 0 plane, fingertips as they are. Fills <paramref name="into"/>, returns how many.
    /// </summary>
    public static int Pointers(Transform space, RayInteractor[] rays, PokeInteractor[] pokes, Vector3[] into)
    {
        int count = 0;
        var rig = DesktopRig.Current;
        if (rig != null && rig.isActiveAndEnabled) AddRay(space, rig.PointerRay, into, ref count);
        foreach (var ray in rays)
            if (ray != null && ray.isActiveAndEnabled && ray.State != InteractorState.Disabled) AddRay(space, ray.Ray, into, ref count);
        foreach (var poke in pokes)
            if (poke != null && poke.isActiveAndEnabled && poke.State != InteractorState.Disabled && count < into.Length)
                into[count++] = space.InverseTransformPoint(poke.Origin);
        return count;
    }

    static void AddRay(Transform space, Ray ray, Vector3[] into, ref int count)
    {
        Vector3 o = space.InverseTransformPoint(ray.origin), d = space.InverseTransformVector(ray.direction);
        if (Mathf.Abs(d.z) < 1e-6f || count >= into.Length) return;
        float t = -o.z / d.z;
        if (t > 0f) into[count++] = o + d * t;
    }

    // ---- Standalone animation ----

    void OnEnable()
    {
        shown = new Vector4(-1f, -1f, -1f, -1f);
        nextInteractorSearch = 0f;
    }

    void LateUpdate()
    {
        if (ring == null || pool == null || label == null || lift == null || input == null) return;
        if (block == null) block = new MaterialPropertyBlock();
        float dt = Time.deltaTime, now = Time.time;
        if (now >= nextInteractorSearch)  // the XR rig can appear after us; the arrays are tiny
        {
            nextInteractorSearch = now + 2f;
            FindInteractors(out rays, out pokes);
        }
        int pointerCount = Pointers(transform, rays, pokes, pointers);
        float closest = float.MaxValue;
        for (int i = 0; i < pointerCount; i++) closest = Mathf.Min(closest, pointers[i].sqrMagnitude);
        float nearTarget = pointerCount > 0 ? Mathf.Clamp01(1f - Mathf.Sqrt(closest) / ForgeTheme.DockNearRange) : 0f;
        near = Mathf.Lerp(near, nearTarget, 1f - Mathf.Exp(-dt * 12f));
        hover = Mathf.MoveTowards(hover, interactable && input.Hovered ? 1f : 0f, dt / ForgeTheme.HoverFade);
        press = Mathf.MoveTowards(press, interactable && input.Pressed ? 1f : 0f, dt / ForgeTheme.PressTime);
        enable = Mathf.MoveTowards(enable, interactable ? 1f : 0f, dt / ForgeTheme.HoverFade);
        float fade = group != null ? group.alpha : 1f;

        var state = new Vector4(near, hover + 2f * press, enable, fade);
        if ((state - shown).sqrMagnitude < 1e-7f) return;
        shown = state;
        lift.localScale = Vector3.one * ((1f + 0.05f * near) * (1f - 0.05f * press));
        SetLook(block, ring, null, pool, label, hue, radius, near, hover, press, 0f, enable, fade, 1f, null, half, wash);
    }
}
