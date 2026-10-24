using TMPro;
using UnityEngine;

/// <summary>
/// The design panel's footer (CAD UX spec 2.6), 44 mm: Undo (flex 1) · Expand / Back to desk (1.3) · Explode /
/// Assemble (1.3) · the design's demo (1.15, primary): its label from analysis.demo (Fly / Hover / Run), Stop while it
/// runs, disabled while building or when the hub says it can't run (the reason in a red line above). Active states
/// (expanded, exploded) in the CAD hue; Undo and Explode pause during a demo; Expand pauses during Fly (the rocket
/// leaves the desk).
/// </summary>
public sealed class CadPanelFooter
{
    public const float Height = 44f, HintH = 14f, ReassembleH = 28f;
    const float Gap = 6f, IconSize = 16f, IconGap = 7f, Text = 13.5f;
    static readonly float[] Flex = { 1f, 1.3f, 1.3f, 1.15f };

    public readonly RectTransform Root;
    public readonly TextMeshProUGUI Hint;
    readonly CadButton undo, expand, explode, demo, reassemble, lifeSize;
    readonly RectTransform[] rects = new RectTransform[4];

    public bool HintShown => Hint.gameObject.activeSelf;
    public bool ReassembleShown => reassemble.gameObject.activeSelf;
    public bool LifeSizeShown => lifeSize.gameObject.activeSelf;

    public CadPanelFooter(RectTransform parent, CadParamsPanel owner)
    {
        Root = CadUi.Rect(parent, "Footer");
        undo = Button("Undo", CadButton.Footer, owner.Undo, CadIcons.Undo);
        expand = Button("Expand", CadButton.Footer, owner.ToggleExpand, CadIcons.Expand);
        explode = Button("Explode", CadButton.Footer, owner.ToggleExplode, CadIcons.Explode);
        demo = Button("Fly", CadButton.Primary, owner.ToggleDemo, CadIcons.Play);
        rects[0] = undo.Rect;
        rects[1] = expand.Rect;
        rects[2] = explode.Rect;
        rects[3] = demo.Rect;
        Hint = CadUi.Text(parent, "DemoBlocked", CadUi.Small, CadUi.BadText, align: TextAlignmentOptions.Right);
        Hint.gameObject.SetActive(false);
        // Expand's take-apart: put back every part taken off (only while some are off).
        reassemble = CadButton.Create(parent, "Reassemble", CadButton.Pill, 0f, owner.Reassemble, pill: true);
        reassemble.AddLabel("Reassemble", 12f);
        reassemble.gameObject.SetActive(false);
        // On the stage: the model at its real size, standing on the floor (or fitted to the stage again).
        lifeSize = CadButton.Create(parent, "LifeSize", CadButton.Pill, 0f, owner.ToggleLifeSize, pill: true);
        lifeSize.AddLabel("Life size", 12f);
        lifeSize.gameObject.SetActive(false);
    }

    CadButton Button(string label, CadButton.Look look, UnityEngine.Events.UnityAction click, System.Action<CadShape, Color> icon)
    {
        var b = CadButton.Create(Root, label, look, 0f, click, pill: true);
        b.AddLabel(label, Text);
        b.AddIcon(icon, IconSize, IconGap);
        return b;
    }

    public void Show(CadPanelState s)
    {
        bool running = s.demoRunning;
        bool flying = running && s.demo?.kind == CadDemoPlayer.Launch;
        undo.Interactable = s.canUndo && !running;

        expand.SetText(s.expanded ? "Back to desk" : "Expand");
        expand.SetIcon(s.expanded ? (System.Action<CadShape, Color>)CadIcons.Back : CadIcons.Expand);
        expand.Active = s.expanded;
        expand.Interactable = !flying;

        explode.SetText(s.exploded ? "Assemble" : "Explode");
        explode.SetIcon(s.exploded ? (System.Action<CadShape, Color>)CadIcons.Assemble : CadIcons.Explode);
        explode.Active = s.exploded;
        explode.Interactable = !running;

        bool hasDemo = s.demo != null || running;
        CadUi.Show(demo, hasDemo);
        string blocked = s.demo?.blocked;
        demo.SetText(running ? "Stop" : string.IsNullOrEmpty(s.demo?.label) ? "Fly" : s.demo.label);
        demo.SetIcon(running ? (System.Action<CadShape, Color>)CadIcons.Stop : CadIcons.Play);
        demo.Interactable = running || (!s.building && string.IsNullOrEmpty(blocked));
        CadUi.Show(reassemble, s.expanded && s.partsOff > 0);
        CadUi.Show(lifeSize, s.expanded);
        lifeSize.SetText(s.lifeSize ? "Fit to stage" : "Life size");
        lifeSize.Active = s.lifeSize;
        lifeSize.Interactable = !flying;
        bool hint = hasDemo && !running && !string.IsNullOrEmpty(blocked);
        Hint.text = hint ? CadUi.Clean(blocked) : "";
        CadUi.Show(Hint, hint);
    }

    /// <summary>Reassemble's pill on the line above the footer, at its left.</summary>
    public void PlaceReassemble(float y, float h)
    {
        if (!reassemble.gameObject.activeSelf) return;
        float w = CadUi.Width(reassemble.Label, "Reassemble") + 26f;
        CadUi.Place(reassemble.Rect, 0f, y + (h - ReassembleH) * 0.5f, w, ReassembleH);
    }

    /// <summary>The Life size pill on the line above the footer, at its right; its width (0 if hidden).</summary>
    public float PlaceLifeSize(float y, float h, float width)
    {
        if (!lifeSize.gameObject.activeSelf) return 0f;
        float w = Mathf.Max(CadUi.Width(lifeSize.Label, "Fit to stage"), CadUi.Width(lifeSize.Label, "Life size")) + 26f;
        CadUi.Place(lifeSize.Rect, width - w, y + (h - ReassembleH) * 0.5f, w, ReassembleH);
        return w;
    }

    /// <summary>The buttons' shares of the width (hidden ones take none).</summary>
    public void Layout() => CadUi.Share(rects, Flex, Gap, 0f, Height);
}
