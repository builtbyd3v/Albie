using UnityEngine;

/// <summary>
/// One of the design panel's tabs (Overview, Parameters, Analysis, History): a root under the scroll view's content,
/// filled from <see cref="CadPanelState"/> in <see cref="Show"/> and laid out top to bottom for the panel's width in
/// <see cref="Layout"/> (which returns the height it used, for the scroll view). Only the active tab's root is on.
/// </summary>
public abstract class CadPanelTab
{
    public readonly RectTransform Root;
    protected readonly CadParamsPanel owner;

    protected CadPanelTab(RectTransform parent, string name, CadParamsPanel owner)
    {
        this.owner = owner;
        Root = CadUi.Rect(parent, name);
        Root.anchorMin = new Vector2(0f, 1f);
        Root.anchorMax = new Vector2(1f, 1f);
        Root.pivot = new Vector2(0.5f, 1f);
        Root.offsetMin = new Vector2(0f, -10f);
        Root.offsetMax = Vector2.zero;
    }

    /// <summary>The data changed: refill (cheap when nothing it shows changed).</summary>
    public abstract void Show(CadPanelState s);

    /// <summary>Place everything for this width; the height used.</summary>
    public abstract float Layout(float width);

    /// <summary>Every frame while the tab shows (timers: the help card, the steppers' debounce).</summary>
    public virtual void Tick(float dt) { }

    /// <summary>The tab went away (or the panel did): drop pointing state.</summary>
    public virtual void Hidden() { }

    /// <summary>A section heading: overline caps text in a 16 mm line.</summary>
    protected static TMPro.TextMeshProUGUI Heading(Transform parent, string text)
    {
        var t = CadUi.OverlineText(parent, "Heading");
        t.text = text;
        return t;
    }
}
