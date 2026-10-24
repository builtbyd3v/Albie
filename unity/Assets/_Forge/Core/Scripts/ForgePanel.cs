using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;

/// <summary>
/// A floating flat window: world-space canvas with a title bar and a content area.
/// Put your module's UI under <see cref="Content"/>. 1 canvas unit = 1 mm, so a 600 x 400 rect is 60 x 40 cm.
/// The prefab (Core/Prefabs/ForgePanel.prefab) already has Interaction SDK ray + poke wired to the canvas,
/// so Buttons, Sliders, ScrollRects etc. work with hands and controllers.
/// To resize it, set <see cref="SizeMeters"/> (not the transform's scale): that keeps the ray/poke hit area in step.
/// In the desk workspace, <see cref="PanelManipulator"/> lets the user move, resize and turn it.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class ForgePanel : MonoBehaviour
{
    public const float MetersPerUnit = 0.001f;

    [Tooltip("Matches the panel IDs in PROJECT_CONTEXT section 7 (e.g. ml-results, cad-params).")]
    public string panelId;

    [SerializeField] TMP_Text titleText;
    [SerializeField] RectTransform content;

    BoundsClipper[] hitAreas;
    float hitMarginMm;

    public RectTransform Content => content;

    public string Title
    {
        get => titleText != null ? titleText.text : "";
        set { if (titleText != null) titleText.text = value; }
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

    void Awake() => SyncHitArea();

    // The Interaction SDK only lets rays and pokes hit inside its clippers' boxes, which the prefab saved at the
    // original size. Fit them to the panel (plus the margin) whenever the size changes.
    void SyncHitArea()
    {
        if (hitAreas == null) hitAreas = GetComponentsInChildren<BoundsClipper>(true);
        Vector2 size = ((RectTransform)transform).sizeDelta + Vector2.one * (2f * hitMarginMm);
        foreach (var clipper in hitAreas) clipper.Size = new Vector3(size.x, size.y, clipper.Size.z);
    }
}
