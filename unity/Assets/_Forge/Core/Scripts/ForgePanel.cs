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
    const float HeaderLeft = 20f, BadgeSize = 40f, BadgePadding = 12f, BadgeGap = 12f, HeaderRight = 180f;

    [Tooltip("Matches the panel IDs in PROJECT_CONTEXT section 7 (e.g. ml-results, cad-params).")]
    public string panelId;

    [SerializeField] TMP_Text titleText;
    [SerializeField] TMP_Text subtitleText;
    [SerializeField] ForgeSurface badge;
    [SerializeField] TMP_Text badgeText;
    [SerializeField] RectTransform content;

    BoundsClipper[] hitAreas;
    float hitMarginMm;
    RectTransform emptyState;
    ForgeSurface emptyRing;
    TMP_Text emptyMark, emptyHeading, emptyCaption;

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
            badge.color = ForgeTheme.Tint(accent, 0.2f);
            badge.Outline = 0.4f;
            var rt = (RectTransform)badge.transform;
            float width = BadgeSize;
            if (badgeText != null)
            {
                badgeText.text = label;
                badgeText.color = Color.Lerp(accent, Color.white, 0.55f);
                // Wider for longer labels (CODE), so it never truncates.
                width = Mathf.Max(BadgeSize, badgeText.GetPreferredValues(label).x + 2f * BadgePadding);
            }
            rt.sizeDelta = new Vector2(width, BadgeSize);
            rt.anchoredPosition = new Vector2(HeaderLeft + width * 0.5f, rt.anchoredPosition.y);
        }
        LayoutHeader();
    }

    // Title (and subtitle) after the badge, clear of the Straight / Face me pills on the right.
    void LayoutHeader()
    {
        if (titleText == null) return;
        bool hasBadge = badge != null && badge.gameObject.activeSelf;
        bool hasSubtitle = subtitleText != null && !string.IsNullOrEmpty(subtitleText.text);
        float left = HeaderLeft + (hasBadge ? ((RectTransform)badge.transform).sizeDelta.x + BadgeGap : 0f);
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

    /// <summary>
    /// A calm placeholder for a panel whose module isn't plugged in yet: a ring in the mode's color around its short
    /// label, the panel's name and a line about what will show up here. Replaces the prefab's placeholder text.
    /// Call after <see cref="SetBadge"/> so it picks up the mode color.
    /// </summary>
    public void ShowEmptyState(string heading, string caption)
    {
        if (content == null) return;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);
        if (emptyState == null) BuildEmptyState();
        emptyRing.color = ForgeTheme.Tint(Accent, 0.08f);
        emptyMark.text = badgeText != null ? badgeText.text : "";
        emptyMark.color = Color.Lerp(Accent, Color.white, 0.55f);
        emptyHeading.text = heading;
        emptyCaption.text = caption;
        emptyState.gameObject.SetActive(true);
    }

    void BuildEmptyState()
    {
        emptyState = NewRect("EmptyState", content);
        emptyState.anchorMin = emptyState.anchorMax = new Vector2(0.5f, 0.5f);
        emptyState.sizeDelta = new Vector2(360f, 150f);
        emptyState.anchoredPosition = new Vector2(0f, 10f);

        var ring = NewRect("Ring", emptyState);
        ring.anchorMin = ring.anchorMax = new Vector2(0.5f, 1f);
        ring.sizeDelta = new Vector2(52f, 52f);
        ring.anchoredPosition = new Vector2(0f, -26f);
        emptyRing = ring.gameObject.AddComponent<ForgeSurface>();
        emptyRing.Pill = true;
        emptyRing.Outline = 0.8f;
        emptyRing.raycastTarget = false;

        emptyMark = EmptyText("Mark", ring, 12f, FontStyles.Bold, new Color(1f, 1f, 1f, 0.9f));
        emptyMark.characterSpacing = 3f;
        emptyMark.rectTransform.anchorMin = Vector2.zero;
        emptyMark.rectTransform.anchorMax = Vector2.one;
        emptyMark.rectTransform.sizeDelta = Vector2.zero;

        emptyHeading = EmptyText("Heading", emptyState, 18f, FontStyles.Bold, ForgeTheme.PanelText);
        emptyHeading.rectTransform.anchoredPosition = new Vector2(0f, -76f);
        emptyHeading.rectTransform.sizeDelta = new Vector2(360f, 24f);

        emptyCaption = EmptyText("Caption", emptyState, 12.5f, FontStyles.Normal, ForgeTheme.PanelSubtext);
        emptyCaption.rectTransform.anchoredPosition = new Vector2(0f, -100f);
        emptyCaption.rectTransform.sizeDelta = new Vector2(330f, 40f);
        emptyCaption.textWrappingMode = TextWrappingModes.Normal;
        emptyCaption.alignment = TextAlignmentOptions.Top;
    }

    static TMP_Text EmptyText(string name, RectTransform parent, float size, FontStyles style, Color color)
    {
        var text = NewRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        text.rectTransform.pivot = new Vector2(0.5f, 1f);
        if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return text;
    }

    static RectTransform NewRect(string name, RectTransform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
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
