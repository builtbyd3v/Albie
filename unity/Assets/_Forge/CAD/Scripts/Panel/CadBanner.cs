using TMPro;
using UnityEngine;

/// <summary>
/// The failed-build banner above the verdict (CAD UX spec 2.4): "Couldn't build that. Kept v4.", the error, where it
/// happened ("rocket.py, line 76"), and Undo / Dismiss. It stays until dismissed or the next good build; nothing else on
/// the panel changes.
/// </summary>
public sealed class CadBanner
{
    const float PadX = 14f, PadY = 11f, Gap = 5f, IconSize = 16f, MiniH = 28f;

    public readonly RectTransform Root;
    readonly TextMeshProUGUI title, error, where;
    readonly CadButton undo, dismiss;
    float height = 92f;

    public float Height => height;

    public CadBanner(RectTransform parent, System.Action onUndo, System.Action onDismiss)
    {
        var fill = CadUi.Surface(parent, "Banner", CadUi.Alpha(CadLook.Bad, 0.1f), 16f, 0.32f);
        Root = fill.rectTransform;
        var icon = CadUi.Shape(Root, "Icon", s => CadIcons.Warning(s, 0f, 0f, IconSize, CadUi.BadText));
        CadUi.Place(icon.rectTransform, PadX, PadY + 1f, IconSize, IconSize);
        title = CadUi.Text(Root, "Title", Value, CadUi.BannerTitle, bold: true);
        error = CadUi.Text(Root, "Error", 12f, CadUi.White(0.82f), wrap: true);
        error.alignment = TextAlignmentOptions.TopLeft;
        where = CadUi.Text(Root, "Where", CadUi.Small, CadUi.White(0.55f));
        undo = CadButton.Create(Root, "Undo", CadButton.Pill, 0f, () => onUndo?.Invoke(), pill: true);
        undo.AddLabel("Undo", 12f);
        dismiss = CadButton.Create(Root, "Dismiss", CadButton.Pill, 0f, () => onDismiss?.Invoke(), pill: true);
        dismiss.AddLabel("Dismiss", 12f);
    }

    const float Value = 14f;

    /// <summary>Show this failure; returns the banner's height for the layout.</summary>
    public float Show(CadRegenFailed f, CadPanelState s, float width)
    {
        if (f == null) return 0f;
        int kept = f.keptVersion ?? s.version;
        title.text = "Couldn't build that. Kept v" + kept + ".";
        error.text = CadUi.Clean(string.IsNullOrEmpty(f.error) ? "The part script failed." : f.error);
        string part = string.IsNullOrEmpty(s.p?.part) ? "the part script" : s.p.part + ".py";
        where.text = f.errorLine.HasValue ? part + ", line " + f.errorLine.Value
            : f.kind == "params" ? "a parameter it can't take" : f.kind == "timeout" ? part + " took too long" : part;
        undo.Interactable = s.canUndo && !s.demoRunning;

        float inner = Mathf.Max(60f, width - 2f * PadX);
        CadUi.Band(title.rectTransform, PadY, 18f, PadX + IconSize + 8f, PadX);
        float errorH = Mathf.Min(CadUi.Height(error, error.text, inner), 12f * 1.25f * 3f);
        CadUi.Band(error.rectTransform, PadY + 18f + Gap, errorH + 1f, PadX, PadX);
        float rowY = PadY + 18f + Gap + errorH + Gap;
        float dismissW = CadUi.Width(dismiss.Label, "Dismiss") + 24f, undoW = CadUi.Width(undo.Label, "Undo") + 24f;
        CadUi.PlaceRight(dismiss.Rect, PadX, rowY, dismissW, MiniH);
        CadUi.PlaceRight(undo.Rect, PadX + dismissW + 6f, rowY, undoW, MiniH);
        CadUi.Band(where.rectTransform, rowY, MiniH, PadX, PadX + dismissW + undoW + 12f);
        height = rowY + MiniH + PadY;
        return height;
    }
}
