using TMPro;
using UnityEngine;

/// <summary>
/// A floating flat window: world-space canvas with a title bar and a content area.
/// Put your module's UI under <see cref="Content"/>. 1 canvas unit = 1 mm, so a 600 x 400 rect is 60 x 40 cm.
/// The prefab (Core/Prefabs/ForgePanel.prefab) already has Interaction SDK ray + poke wired to the canvas,
/// so Buttons, Sliders, ScrollRects etc. work with hands and controllers.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class ForgePanel : MonoBehaviour
{
    public const float MetersPerUnit = 0.001f;

    [Tooltip("Matches the panel IDs in PROJECT_CONTEXT section 7 (e.g. ml-results, cad-params).")]
    public string panelId;

    [SerializeField] TMP_Text titleText;
    [SerializeField] RectTransform content;

    public RectTransform Content => content;

    public string Title
    {
        get => titleText != null ? titleText.text : "";
        set { if (titleText != null) titleText.text = value; }
    }

    public Vector2 SizeMeters
    {
        get => ((RectTransform)transform).sizeDelta * MetersPerUnit;
        set => ((RectTransform)transform).sizeDelta = value / MetersPerUnit;
    }
}
