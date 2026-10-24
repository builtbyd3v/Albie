using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;

/// <summary>
/// A floating flat window: world-space canvas with a graphite frame, a header and a content area.
/// Put your module's UI under <see cref="Content"/>. 1 canvas unit = 1 mm, so a 600 x 400 rect is 60 x 40 cm.
/// The header shows <see cref="Title"/>, an optional <see cref="Subtitle"/> and the mode badge (<see cref="SetBadge"/>);
/// the look comes from <see cref="ForgeTheme"/>.
/// The prefab (Core/Prefabs/ForgePanel.prefab) already has Interaction SDK ray + poke wired to the canvas,
/// so Buttons, Sliders, ScrollRects etc. work with hands and controllers.
/// To resize it, set <see cref="SizeMeters"/> (not the transform's scale): that keeps the ray/poke hit area in step.
/// In the desk workspace, <see cref="PanelManipulator"/> lets the user move, resize and turn it.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class ForgePanel : MonoBehaviour
{
    public const float MetersPerUnit = 0.001f;
    const float HeaderLeft = 20f, BadgeSize = 34f, BadgeGap = 12f, HeaderRight = 180f;

    [Tooltip("Matches the panel IDs in PROJECT_CONTEXT section 7 (e.g. ml-results, cad-params).")]
    public string panelId;

    [SerializeField] TMP_Text titleText;
    [SerializeField] TMP_Text subtitleText;
    [SerializeField] ForgeSurface badge;
    [SerializeField] TMP_Text badgeText;
    [SerializeField] RectTransform content;

    BoundsClipper[] hitAreas;
    float hitMarginMm;

    public RectTransform Content => content;

    public string Title
    {
        get => titleText != null ? titleText.text : "";
        set { if (titleText != null) titleText.text = value; }
    }

    /// <summary>Small second line under the title (e.g. "rocket v1"). Empty hides it.</summary>
    public string Subtitle
    {
        get => subtitleText != null ? subtitleText.text : "";
        set
        {
            if (subtitleText != null) subtitleText.text = value;
            LayoutHeader();
        }
    }

    /// <summary>The mode color this panel belongs to (set with <see cref="SetBadge"/>), for modules to accent with.</summary>
    public Color Accent { get; private set; } = Color.white;

    /// <summary>
    /// The mode badge at the left of the header: a small tile tinted with the mode's color and its short label
    /// (CAD, ML, ...), matching the dock ring. Null or empty hides it.
    /// </summary>
    public void SetBadge(string label, Color accent)
    {
        Accent = accent;
        if (badge == null) return;
        bool show = !string.IsNullOrEmpty(label);
        badge.gameObject.SetActive(show);
        if (show)
        {
            badge.color = ForgeTheme.Tint(accent, 0.16f);
            badge.Outline = 0.35f;
            if (badgeText != null)
            {
                badgeText.text = label;
                badgeText.color = Color.Lerp(accent, Color.white, 0.45f);
            }
        }
        LayoutHeader();
    }

    // Title (and subtitle) after the badge, clear of the Straight / Face me pills on the right.
    void LayoutHeader()
    {
        if (titleText == null) return;
        bool hasBadge = badge != null && badge.gameObject.activeSelf;
        bool hasSubtitle = subtitleText != null && !string.IsNullOrEmpty(subtitleText.text);
        float left = HeaderLeft + (hasBadge ? BadgeSize + BadgeGap : 0f);
        PlaceInHeader(titleText.rectTransform, left, hasSubtitle ? 25f : ForgeTheme.PanelHeader * 0.5f, 22f);
        if (subtitleText != null)
        {
            subtitleText.gameObject.SetActive(hasSubtitle);
            PlaceInHeader(subtitleText.rectTransform, left, 44f, 16f);
        }
    }

    static void PlaceInHeader(RectTransform rt, float left, float centerFromTop, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(-(left + HeaderRight), height);
        rt.anchoredPosition = new Vector2((left - HeaderRight) * 0.5f, -centerFromTop);
    }

    public Vector2 SizeMeters
    {
        get => ((RectTransform)transform).sizeDelta * MetersPerUnit;
        set
        {
            ((RectTransform)transform).sizeDelta = value / MetersPerUnit;
            SyncHitArea();
        }
    }

    /// <summary>Resizing keeps the width:height ratio (a video stream, say).</summary>
    public bool KeepAspect { get; set; }

    /// <summary>Extra ray/poke hit area around the edges (mm), for handles drawn just outside the panel.</summary>
    public float HitMarginMm
    {
        get => hitMarginMm;
        set
        {
            hitMarginMm = value;
            SyncHitArea();
        }
    }

    void Awake()
    {
        SyncHitArea();
        LayoutHeader();
    }

    // The Interaction SDK only lets rays and pokes hit inside its clippers' boxes, which the prefab saved at the
    // original size. Fit them to the panel (plus the margin) whenever the size changes.
    void SyncHitArea()
    {
        if (hitAreas == null) hitAreas = GetComponentsInChildren<BoundsClipper>(true);
        Vector2 size = ((RectTransform)transform).sizeDelta + Vector2.one * (2f * hitMarginMm);
        foreach (var clipper in hitAreas) clipper.Size = new Vector3(size.x, size.y, clipper.Size.z);
    }
}
