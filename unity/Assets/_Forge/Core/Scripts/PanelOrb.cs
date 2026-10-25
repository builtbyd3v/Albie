using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A panel shrunk into a dot of light (<see cref="WorkspaceExtras"/> minimizes into one): a neon ring like the dock's
/// buttons, with a glyph or a number inside and an optional badge (how many notes). For a timer the ring drains
/// instead (<see cref="SetProgress"/>). Pointed at, a comet runs round it. Tap it to open the panel again. Hold it
/// (0.3 s, or start moving it) to carry it on the end of the pointer like a panel's title bar; scroll, the thumbstick
/// or reaching out pushes it back or pulls it in. Mouse (DesktopRig), Interaction SDK rays and pokes.
/// Lives on a small world-space canvas (a ForgePanel variant with its frame hidden, Desk/Prefabs/DeskOrb.prefab) and
/// builds its look in Awake, all uGUI (<see cref="ForgeRim"/>, <see cref="ForgeSurface"/>, Inter).
/// </summary>
public class PanelOrb : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public const float Diameter = 64f;  // mm
    const float HoldTime = 0.3f;        // s: a press this long starts a carry
    const float MoveSlop = 0.01f;       // m: so does moving this far while pressed
    const float CometTurn = 1.05f;      // s per turn, as on the dock
    const float MinDistance = 0.25f, MaxDistance = 4f, ReachGain = 4f, PushPerNotch = 0.05f;

    /// <summary>Pressed and let go without carrying it.</summary>
    public event Action Tapped;
    /// <summary>Carried and let go: it stays where it is now.</summary>
    public event Action Moved;

    /// <summary>Set while the workspace animates it; presses wait.</summary>
    public bool Busy { get; set; }
    public bool Carrying { get; private set; }
    public CanvasGroup Group { get; private set; }

    Transform head;
    RectTransform visuals, glyph, badge;
    ForgeRim ring, comet, hold;
    ForgeSurface halo, disc;
    TMP_Text big, small, badgeText;
    Color hue = ForgeTheme.Tool;
    bool ringing, built;
    int hovers;
    float hover, lift, holdShown = -1f, shownHover = -1f, shownLift = -1f;

    bool pressing;
    float pressedAt, grabDistance, pushed, reachAtGrab;
    Vector3 pressPoint;
    Plane pressPlane;
    ForgePointer pointer;

    public void Init(Transform eyes) => head = eyes;

    /// <summary>A number (or a few characters) with a small caption under it, e.g. "42" / "MIN".</summary>
    public void SetText(string number, string caption)
    {
        Build();
        if (big.text != number) big.text = number ?? "";
        if (small.text != caption) small.text = caption ?? "";
        big.gameObject.SetActive(!string.IsNullOrEmpty(number));
        small.gameObject.SetActive(!string.IsNullOrEmpty(caption));
        glyph.gameObject.SetActive(false);
    }

    /// <summary>A page of lines (notes) instead of a number.</summary>
    public void SetGlyph()
    {
        Build();
        big.gameObject.SetActive(false);
        small.gameObject.SetActive(false);
        glyph.gameObject.SetActive(true);
    }

    /// <summary>A white count at the top right; null or empty hides it.</summary>
    public void SetBadge(string text)
    {
        Build();
        bool show = !string.IsNullOrEmpty(text);
        badge.gameObject.SetActive(show);
        if (!show || badgeText.text == text) return;
        badgeText.text = text;
        badge.sizeDelta = new Vector2(Mathf.Max(22f, badgeText.GetPreferredValues(text).x + 12f), 22f);
    }

    /// <summary>0..1: the ring lit that much of the way round (a timer); 1 (the default) is the whole ring.</summary>
    public void SetProgress(float value)
    {
        Build();
        ring.Progress = value;
        ring.TrackAlpha = value < 1f ? 0.18f : 0f;
    }

    /// <summary>Pulses (a timer that's ringing).</summary>
    public void SetRinging(bool value) => ringing = value;

    /// <summary>The faint tint in the halo (a mode color); white-blue by default, like the dock's tools.</summary>
    public void SetHue(Color value)
    {
        hue = value;
        if (halo != null) halo.color = ForgeTheme.Tint(hue, 0.26f);
    }

    void Awake() => Build();

    void Build()
    {
        if (built) return;
        built = true;
        Group = GetComponent<CanvasGroup>();
        if (Group == null) Group = gameObject.AddComponent<CanvasGroup>();
        var root = (RectTransform)transform;
        visuals = NewRect("Visuals", root, Vector2.one * Diameter);

        // A soft dark wash so the light reads on a bright room, a faint tinted halo, and the disc that lights up
        // while it's carried.
        var wash = Surface("Wash", visuals, Diameter + 30f, new Color(0.05f, 0.06f, 0.08f, 0.26f), 1f);
        wash.raycastTarget = false;
        halo = Surface("Halo", visuals, Diameter + 18f, ForgeTheme.Tint(hue, 0.26f), 1f);
        disc = Surface("Disc", visuals, Diameter - 4f, new Color(1f, 1f, 1f, 0.12f), 0f);
        disc.canvasRenderer.SetAlpha(0f);

        ring = Rim("Ring", Diameter, 2.4f, 8f, 0.6f);
        ring.InnerGlow = 0.6f;
        comet = Rim("Comet", Diameter + 2f, 2.8f, 6f, 0.7f);
        comet.InnerGlow = 0.25f;  // a streak of light round the ring, not a wedge into it
        comet.Tail = true;
        comet.Progress = 0.24f;
        comet.TrackAlpha = 0f;
        comet.canvasRenderer.SetAlpha(0f);
        hold = Rim("Hold", Diameter + 14f, 2.4f, 4f, 0.6f);
        hold.Inset = 0f;
        hold.TrackAlpha = 0f;
        hold.Progress = 0f;
        hold.canvasRenderer.SetAlpha(0f);

        big = NewText("Number", visuals, 21f, new Vector2(0f, 4f));
        small = NewText("Caption", visuals, 8.5f, new Vector2(0f, -13f));
        small.characterSpacing = 8f;
        small.color = new Color(1f, 1f, 1f, 0.8f);
        big.gameObject.SetActive(false);
        small.gameObject.SetActive(false);

        // A page of lines: three bars, the last one short.
        glyph = NewRect("Glyph", visuals, new Vector2(28f, 22f));
        for (int i = 0; i < 3; i++)
        {
            var bar = NewRect("Line", glyph, new Vector2(i == 2 ? 16f : 28f, 3.4f));
            bar.anchorMin = bar.anchorMax = new Vector2(0f, 1f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.anchoredPosition = new Vector2(0f, -2f - 9f * i);
            var line = bar.gameObject.AddComponent<ForgeSurface>();
            line.Pill = true;
            line.Glow = 0.6f;
            line.color = Color.white;
            line.raycastTarget = false;
        }
        glyph.gameObject.SetActive(false);

        badge = NewRect("Badge", visuals, new Vector2(22f, 22f));
        badge.anchoredPosition = new Vector2(Diameter * 0.36f, Diameter * 0.36f);
        var badgeSurface = badge.gameObject.AddComponent<ForgeSurface>();
        badgeSurface.Pill = true;
        badgeSurface.Glow = 0.5f;
        badgeSurface.color = Color.white;
        badgeSurface.raycastTarget = false;
        badgeText = NewText("Count", badge, 12f, Vector2.zero);
        badgeText.color = ForgeTheme.PillOnText;
        badgeText.rectTransform.anchorMin = Vector2.zero;
        badgeText.rectTransform.anchorMax = Vector2.one;
        badgeText.rectTransform.sizeDelta = Vector2.zero;
        badge.gameObject.SetActive(false);

        // What rays and the mouse hit: a still, invisible disc a little bigger than the ring.
        var hit = NewRect("Hit", root, Vector2.one * (Diameter + 12f)).gameObject.AddComponent<Image>();
        hit.color = Color.clear;
    }

    ForgeRim Rim(string name, float size, float stroke, float glow, float glowAlpha)
    {
        var rim = NewRect(name, visuals, Vector2.one * size).gameObject.AddComponent<ForgeRim>();
        rim.raycastTarget = false;
        rim.color = Color.white;
        rim.Stroke = stroke;
        rim.Glow = glow;
        rim.GlowAlpha = glowAlpha;
        rim.TrackAlpha = 0f;
        return rim;
    }

    static ForgeSurface Surface(string name, RectTransform parent, float size, Color color, float glow)
    {
        var surface = NewRect(name, parent, Vector2.one * size).gameObject.AddComponent<ForgeSurface>();
        surface.Pill = true;
        surface.Glow = glow;
        surface.color = color;
        surface.raycastTarget = false;
        return surface;
    }

    // ---------------------------------------------------------------- input

    public void OnPointerEnter(PointerEventData e) => hovers++;
    public void OnPointerExit(PointerEventData e) => hovers = Mathf.Max(0, hovers - 1);

    public void OnPointerDown(PointerEventData e)
    {
        if (pressing || Busy || head == null || !ForgePointer.TryFind(e, head, out pointer)) return;
        pressing = true;
        pressedAt = Time.time;
        Ray r = pointer.Ray;
        pressPlane = new Plane(transform.forward, transform.position);
        pressPoint = pressPlane.Raycast(r, out float enter) ? r.GetPoint(enter) : transform.position;
        grabDistance = Vector3.Distance(r.origin, transform.position);
        reachAtGrab = pointer.Reach;
        pushed = 0f;
    }

    public void OnPointerUp(PointerEventData e) => Release();

    void Release()
    {
        if (!pressing) return;
        pressing = false;
        pointer = null;
        if (Carrying)
        {
            Carrying = false;
            Moved?.Invoke();
        }
        else Tapped?.Invoke();
    }

    void OnDisable()
    {
        hovers = 0;
        pressing = Carrying = false;
        pointer = null;
    }

    // ---------------------------------------------------------------- per frame

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (pressing) Press(dt);

        hover = Mathf.MoveTowards(hover, hovers > 0 && !Carrying ? 1f : 0f, dt / ForgeTheme.HoverFade);
        lift = Mathf.MoveTowards(lift, Carrying ? 1f : 0f, dt / 0.18f);
        float holding = pressing && !Carrying ? Mathf.Clamp01((Time.time - pressedAt) / HoldTime) : 0f;
        if (holding != holdShown)
        {
            holdShown = holding;
            hold.Progress = holding;
            hold.canvasRenderer.SetAlpha(holding > 0f ? 1f : 0f);
        }

        // Pointed at: the comet runs round (turning the rect is free, no mesh rebuild).
        if (hover > 0f) comet.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -360f * (Time.time / CometTurn % 1f));
        if (hover != shownHover)
        {
            shownHover = hover;
            comet.canvasRenderer.SetAlpha(hover);
            ring.canvasRenderer.SetAlpha(0.8f + 0.2f * hover);
        }
        if (lift != shownLift)
        {
            shownLift = lift;
            disc.canvasRenderer.SetAlpha(lift);
            halo.canvasRenderer.SetAlpha(0.6f + 0.4f * lift);
        }

        float pulse = ringing ? 1f + 0.06f * (0.5f + 0.5f * Mathf.Sin(Time.time * Mathf.PI * 2f / 1.2f)) : 1f;
        visuals.localScale = Vector3.one * ((1f + 0.05f * hover) * (1f + 0.14f * ForgeTween.EaseOutBack(lift)) * pulse);
    }

    // Held: a short press is a tap; past HoldTime, or moved a centimetre, it's carried on the end of the pointer.
    void Press(float dt)
    {
        // The release can get lost (controller put down, hand tracking lost): poll too.
        if (pointer == null || !pointer.Held)
        {
            Release();
            return;
        }
        Ray r = pointer.Ray;
        if (!Carrying)
        {
            bool moved = pressPlane.Raycast(r, out float enter) && Vector3.Distance(r.GetPoint(enter), pressPoint) > MoveSlop;
            if (Time.time - pressedAt < HoldTime && !moved) return;
            Carrying = true;
        }
        if (pointer.CanPush)
        {
            pushed += pointer.Notches(dt) * PushPerNotch;
            float reach = (pointer.Reach - reachAtGrab) * ReachGain;
            float d = Mathf.Clamp(grabDistance + pushed + reach, MinDistance, MaxDistance);
            pushed = d - grabDistance - reach;
            transform.position = r.origin + r.direction.normalized * d;
        }
        else if (pressPlane.Raycast(r, out float enter))
        {
            transform.position = r.GetPoint(enter);
        }
        transform.rotation = Quaternion.LookRotation(transform.position - head.position, Vector3.up);
    }

    // ---------------------------------------------------------------- helpers

    static RectTransform NewRect(string name, RectTransform parent, Vector2 size)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        rt.sizeDelta = size;
        return rt;
    }

    static TMP_Text NewText(string name, RectTransform parent, float size, Vector2 position)
    {
        var text = NewRect(name, parent, new Vector2(Diameter, size * 1.4f)).gameObject.AddComponent<TextMeshProUGUI>();
        text.rectTransform.anchoredPosition = position;
        if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
        text.fontSize = size;
        text.fontStyle = FontStyles.Bold;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return text;
    }
}
