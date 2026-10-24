using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Horizon-style pill button: a dim white pill with white text when off, near-white with dark text when on, a touch
/// brighter while pointed at. Works with the mouse (DesktopRig) and Interaction SDK rays and pokes like any Button.
/// Make one with <see cref="Create"/>; it sizes itself to its label.
/// </summary>
public class ForgePill : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] ForgeSurface surface;
    [SerializeField] TMP_Text label;
    bool on;
    int hovers;

    public Button Button { get; private set; }
    public RectTransform Rect => (RectTransform)transform;

    /// <summary>Lit: the pill shows the state that's in effect (Face me while facing).</summary>
    public bool On
    {
        get => on;
        set
        {
            on = value;
            Refresh();
        }
    }

    public static ForgePill Create(RectTransform parent, string text, UnityAction onClick, float height = ForgeTheme.PillHeight, float textSize = ForgeTheme.PillText)
    {
        var rt = (RectTransform)new GameObject(text, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        var pill = rt.gameObject.AddComponent<ForgePill>();
        pill.surface = rt.gameObject.AddComponent<ForgeSurface>();
        pill.surface.Pill = true;
        pill.Button = rt.gameObject.AddComponent<Button>();
        pill.Button.targetGraphic = pill.surface;
        pill.Button.transition = Selectable.Transition.None;
        pill.Button.navigation = new Navigation { mode = Navigation.Mode.None };
        pill.Button.onClick.AddListener(onClick);

        var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
        label.rectTransform.SetParent(rt, false);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        if (ForgeTheme.Font != null) label.font = ForgeTheme.Font;
        label.fontSize = textSize;
        label.characterSpacing = 1f;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        label.text = text;
        pill.label = label;
        rt.sizeDelta = new Vector2(label.GetPreferredValues(text).x + 2f * textSize, height);
        pill.Refresh();
        return pill;
    }

    public void OnPointerEnter(PointerEventData e)
    {
        hovers++;
        Refresh();
    }

    public void OnPointerExit(PointerEventData e)
    {
        hovers = Mathf.Max(0, hovers - 1);
        Refresh();
    }

    void OnDisable()
    {
        hovers = 0;
        Refresh();
    }

    void Refresh()
    {
        if (surface == null || label == null) return;
        surface.color = on ? ForgeTheme.PillOn : hovers > 0 ? ForgeTheme.PillOffHover : ForgeTheme.PillOff;
        label.color = on ? ForgeTheme.PillOnText : ForgeTheme.PillOffText;
    }
}
