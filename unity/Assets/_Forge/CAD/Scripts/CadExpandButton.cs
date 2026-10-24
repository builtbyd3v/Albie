using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

/// <summary>
/// The Expand ring on the desk beside the CAD volume's base (CAD UX spec, section 3): a 32 mm ring of light in the dock's
/// language (Core's NeonRing: one white line, a CAD-hue halo, its comet on hover) with two small arrows inside, pointing
/// out (Expand) or in (Back to desk). Pressing it raises <see cref="Pressed"/>; CadViewer toggles the stage. The first
/// time it shows, an "Expand" tip comes up beside it for a few seconds.
/// Input: a mouse (DesktopRig, the sandbox's input module) through <see cref="CadPartRaycaster"/>; controller and hand
/// rays through a RayInteractable on its collider; a fingertip (any PokeInteractor) pressing down into the ring, like the
/// dock's rings. Its hit box rises just above the volume's base grab box, so pointing at the ring presses the ring rather
/// than moving the volume. CadViewer makes it at runtime.
/// </summary>
public class CadExpandButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    const int IgnoreRaycastLayer = 2;
    const float RingMm = 16f, QuadMm = 92f, PoolMm = 92f;
    static readonly List<CadExpandButton> active = new List<CadExpandButton>();
    static readonly int SizeId = Shader.PropertyToID("_Size"), RadiusId = Shader.PropertyToID("_Radius");
    static readonly int HueId = Shader.PropertyToID("_Hue"), HueTId = Shader.PropertyToID("_HueT");
    static readonly int NearId = Shader.PropertyToID("_Near"), HoverId = Shader.PropertyToID("_Hover");
    static readonly int SelectedId = Shader.PropertyToID("_Selected"), EnabledId = Shader.PropertyToID("_Enabled");
    static readonly int FadeId = Shader.PropertyToID("_Fade"), OpacityId = Shader.PropertyToID("_Opacity");
    static readonly int ImprintId = Shader.PropertyToID("_Imprint");
    static bool tipShown;

    // Pokes: a fingertip within the ring's 24 mm and under 10 mm above the desk presses it once; lifting it 20 mm (or
    // leaving the ring) arms it again; within 60 mm above it lights the ring like a hover. The hands' poke interactors
    // are looked up every few seconds, not every frame.
    const float PokeRadius = 0.024f, PokeDown = 0.01f, PokeUp = 0.02f, PokeHover = 0.06f, PokeRefresh = 3f;
    static PokeInteractor[] pokes = new PokeInteractor[0];
    static float pokesFound = -100f;

    // This project enters Play mode without a domain reload: statics would carry over from the last session.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        tipShown = false;
        active.Clear();
        pokes = new PokeInteractor[0];
        pokesFound = -100f;
    }

    public event Action Pressed;

    BoxCollider box;
    RayInteractable ray;
    MeshRenderer ring, pool;
    MaterialPropertyBlock block;
    CadLines arrows;
    CadChips chips;
    CadLabel tip;
    int mouseHovers;
    float hover, selected, enable = 1f, tipStart = -1f;
    bool expanded, interactable = true, tipSaysBack, pokeNear, pokeArmed = true;
    Vector4 shown = new Vector4(-1f, -1f, -1f, -1f);
    // What the arrows were last drawn for: they're rebuilt only when these change (the eyes after 1 cm).
    Matrix4x4 drawnPose;
    Vector3 drawnEye;
    float drawnEnable = -1f;
    bool drawnExpanded;

    public static int Count => active.Count;
    public bool Hovered => mouseHovers > 0 || pokeNear || (ray != null && (ray.State == InteractableState.Hover || ray.State == InteractableState.Select));

    /// <summary>Nearest Expand ring the ray hits within <paramref name="maxDistance"/>.</summary>
    public static bool Raycast(Ray r, float maxDistance, out RaycastHit hit, out CadExpandButton button)
    {
        hit = default;
        button = null;
        foreach (var b in active)
        {
            if (b.box == null || !b.interactable || !b.box.Raycast(r, out RaycastHit candidate, maxDistance)) continue;
            maxDistance = candidate.distance;
            hit = candidate;
            button = b;
        }
        return button != null;
    }

    /// <summary>A ring at <paramref name="localPosition"/> under <paramref name="volume"/> (on its base's plane).</summary>
    public static CadExpandButton Create(Transform volume, Vector3 localPosition)
    {
        var go = new GameObject("CadExpandButton");
        go.layer = IgnoreRaycastLayer;
        go.transform.SetParent(volume, false);
        go.transform.localPosition = localPosition;
        var button = go.AddComponent<CadExpandButton>();
        button.Build();
        return button;
    }

    void Build()
    {
        box = gameObject.AddComponent<BoxCollider>();
        box.size = new Vector3(0.048f, 0.024f, 0.048f);
        box.center = new Vector3(0f, 0.016f, 0f);  // top at 28 mm: above the volume's base grab box (25 mm)
        box.excludeLayers = ~0;                     // pointing only: it bumps into nothing
        var surface = gameObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(box);
        ray = gameObject.AddComponent<RayInteractable>();
        ray.InjectAllRayInteractable(surface);
        ray.WhenSelectingInteractorAdded.Action += OnRaySelect;

        pool = Layer("Pool", ForgeTheme.PoolLight, PoolMm, 0.0015f);
        ring = Layer("Ring", ForgeTheme.RingLight, QuadMm, 0.002f);
        block = new MaterialPropertyBlock();
        arrows = new CadLines("CadExpandArrows", transform, CadLook.Light(overlay: false));
        chips = new CadChips("CadExpandTipChip", transform);
        tip = new CadLabel(transform, "CadExpandTip", CadLabel.Style.Tag);
        tip.SetText("Expand");
    }

    // A quad lying flat on the desk (a quad faces -Z; turned 90 deg about X it faces up), sized in mm like the dock's.
    MeshRenderer Layer(string name, Material material, float sizeMm, float height)
    {
        var quad = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        quad.layer = gameObject.layer;
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = new Vector3(0f, height, 0f);
        quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        quad.transform.localScale = new Vector3(sizeMm * 0.001f, sizeMm * 0.001f, 1f);
        quad.GetComponent<MeshFilter>().sharedMesh = ForgeTheme.Quad;
        var r = quad.GetComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return r;
    }

    /// <summary>Arrows pointing in (Back to desk) while the model is up on the stage.</summary>
    public void SetExpanded(bool on) => expanded = on;

    /// <summary>Dimmed and deaf while it can't be used (Fly is running).</summary>
    public void SetInteractable(bool on) => interactable = on;

    void OnEnable()
    {
        active.Add(this);
        tipStart = -1f;
        drawnEnable = -1f;  // arrows were hidden while disabled: draw them again
        pokeArmed = true;
    }

    void OnDisable()
    {
        active.Remove(this);
        mouseHovers = 0;
        if (arrows != null) arrows.Hide();
        if (chips != null) chips.Hide();
        if (tip != null) tip.Hide();
    }

    void OnDestroy()
    {
        if (ray != null) ray.WhenSelectingInteractorAdded.Action -= OnRaySelect;
        arrows?.Destroy();
        chips?.Destroy();
        tip?.Destroy();
    }

    void OnRaySelect(RayInteractor interactor) => Press();

    public void OnPointerEnter(PointerEventData e) => mouseHovers++;
    public void OnPointerExit(PointerEventData e) => mouseHovers = Mathf.Max(0, mouseHovers - 1);
    public void OnPointerClick(PointerEventData e) => Press();

    void Press()
    {
        if (interactable) Pressed?.Invoke();
    }

    // A fingertip pressing down into the ring presses it (once per push).
    void Pokes()
    {
        if (Time.unscaledTime - pokesFound > PokeRefresh || Time.unscaledTime < pokesFound)
        {
            pokesFound = Time.unscaledTime;
            pokes = FindObjectsByType<PokeInteractor>();
        }
        bool near = false, down = false, clear = true;
        for (int i = 0; i < pokes.Length; i++)
        {
            var poke = pokes[i];
            if (poke == null || !poke.isActiveAndEnabled || poke.State == InteractorState.Disabled) continue;
            Vector3 local = transform.InverseTransformPoint(poke.Origin);
            float radial = Mathf.Sqrt(local.x * local.x + local.z * local.z);
            if (radial > PokeRadius * 1.25f || local.y < -0.03f) continue;  // beside it, or under the desk
            if (local.y < PokeHover) near = true;
            if (radial <= PokeRadius && local.y < PokeDown) down = true;
            if (local.y < PokeUp) clear = false;
        }
        pokeNear = near && interactable;
        if (down && pokeArmed)
        {
            pokeArmed = false;
            Press();
        }
        else if (clear) pokeArmed = true;
    }

    void LateUpdate()
    {
        if (ring == null) return;
        Pokes();
        float dt = Time.deltaTime;
        hover = Mathf.MoveTowards(hover, interactable && Hovered ? 1f : 0f, dt / ForgeTheme.HoverFade);
        selected = Mathf.MoveTowards(selected, expanded ? 1f : 0f, dt / ForgeTheme.SelectFade);
        enable = Mathf.MoveTowards(enable, interactable ? 1f : 0f, dt / ForgeTheme.HoverFade);
        var state = new Vector4(hover, selected, enable, 0f);
        if ((state - shown).sqrMagnitude > 1e-7f)
        {
            shown = state;
            block.Clear();
            block.SetFloat(SizeId, QuadMm);
            block.SetFloat(RadiusId, RingMm);
            block.SetColor(HueId, ForgeTheme.CAD);
            block.SetFloat(HueTId, ForgeTheme.HueStrength);
            block.SetFloat(NearId, 0.35f + 0.65f * hover);
            block.SetFloat(HoverId, hover);
            block.SetFloat(SelectedId, selected);
            block.SetFloat(EnabledId, enable);
            block.SetFloat(FadeId, 1f);
            ring.SetPropertyBlock(block);
            if (pool != null)
            {
                block.Clear();
                block.SetFloat(SizeId, PoolMm);
                block.SetFloat(RadiusId, RingMm);
                block.SetColor(HueId, ForgeTheme.CAD);
                block.SetFloat(HueTId, ForgeTheme.HueStrength);
                block.SetFloat(OpacityId, (0.3f + 0.45f * hover + 0.25f * selected) * Mathf.Lerp(0.4f, 1f, enable));
                block.SetFloat(ImprintId, selected);
                block.SetFloat(FadeId, 1f);
                pool.SetPropertyBlock(block);
            }
        }

        var cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : transform.position + Vector3.up;
        // Two small arrows lying on the desk inside the ring: out to expand, in to come back. Rebuilt only when the
        // ring moved, its state changed or the eyes moved over 1 cm (the lines face them), not every frame.
        var pose = transform.localToWorldMatrix;
        if (expanded != drawnExpanded || Mathf.Abs(enable - drawnEnable) > 1e-4f || (eye - drawnEye).sqrMagnitude > 1e-4f || !SameMatrix(pose, drawnPose))
        {
            drawnExpanded = expanded;
            drawnEnable = enable;
            drawnEye = eye;
            drawnPose = pose;
            arrows.Begin(eye);
            Color line = CadLook.Alpha(Color.white, 0.95f * Mathf.Lerp(0.4f, 1f, enable));
            Arrow(1f, 1f, line);
            Arrow(-1f, -1f, line);
            arrows.End();
        }

        // The first time it shows: "Expand" beside it for a few seconds.
        if (!tipShown && tipStart < 0f) tipStart = Time.time;
        float tipAge = tipStart >= 0f ? Time.time - tipStart : 99f;
        float tipAlpha = tipAge < 4.5f ? Mathf.Clamp01(tipAge / 0.3f) * Mathf.Clamp01((4.5f - tipAge) / 0.5f) : 0f;
        if (tipAge >= 4.5f) tipShown = true;
        if (tipAlpha > 0f)
        {
            Vector3 right = Vector3.Cross(Vector3.up, transform.position - eye);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            if (tipSaysBack != expanded)
            {
                tipSaysBack = expanded;
                tip.SetText(expanded ? "Back to desk" : "Expand");
            }
            tip.SetAnchor(1);
            tip.MoveTo(transform.position + right * 0.03f + Vector3.up * 0.012f, eye, dt);
            tip.SetAlpha(tipAlpha);
            chips.Begin();
            tip.Draw(chips);
            chips.End();
        }
        else if (tip.Alpha > 0f)
        {
            tip.Hide();
            chips.Hide();
        }
    }

    static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++)
            if (Mathf.Abs(a[i] - b[i]) > 1e-6f) return false;
        return true;
    }

    void Arrow(float dx, float dz, Color color)
    {
        const float reach = 0.0068f, head = 0.0032f;
        float sign = expanded ? -1f : 1f;
        float tipAt = expanded ? 0.25f : 1f, tailAt = expanded ? 1f : 0.25f;
        Vector3 c = new Vector3(0f, 0.004f, 0f);
        Vector3 tipPoint = c + new Vector3(dx, 0f, dz) * (reach * tipAt);
        Vector3 tailPoint = c + new Vector3(dx, 0f, dz) * (reach * tailAt);
        Vector3 t = transform.TransformPoint(tipPoint);
        arrows.Segment(transform.TransformPoint(tailPoint), t, CadLook.ThinLine, color);
        arrows.Segment(t, transform.TransformPoint(tipPoint + new Vector3(-dx * head * sign, 0f, 0f)), CadLook.ThinLine, color);
        arrows.Segment(t, transform.TransformPoint(tipPoint + new Vector3(0f, 0f, -dz * head * sign)), CadLook.ThinLine, color);
    }
}
