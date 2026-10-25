using Forge.ML;
using UnityEngine;

/// <summary>
/// Bit's hands on the workspace (hub/agent/ui_tools.py). ui.highlight {panel, item_ids?, class_idx?} pulses the open panel's frame, and on ml-results selects the sample (first id) or
/// the class. Opening a mode is WorkspaceRemote's job (ui.mode, ui_open_view). Sits on the Workspace object in Main (MainSceneBuilder adds it). Event-driven only, no Update.
/// </summary>
[RequireComponent(typeof(Workspace))]
public class BitUiDirector : MonoBehaviour
{
    public int pulses = 2;

    Workspace workspace;

    void Awake() => workspace = GetComponent<Workspace>();

    void OnEnable() => ForgeEvents.OnUiHighlight += OnHighlight;

    void OnDisable() => ForgeEvents.OnUiHighlight -= OnHighlight;

    void OnHighlight(UiHighlight h)
    {
        if (h == null || string.IsNullOrEmpty(h.panel)) return;
        // ponytail: only panels of the open mode; Bit opens the mode first (ui_open_view).
        if (!workspace.TryGetItemTransform(h.panel, out Transform t))
        {
            Debug.Log($"[BitUiDirector] {h.panel} isn't open; nothing to highlight.");
            return;
        }
        var handles = t.GetComponent<PanelManipulator>();
        if (handles != null) handles.Pulse(pulses);

        if (h.panel != "ml-results") return;
        var wall = t.GetComponentInChildren<ResultsWall>();
        if (wall == null) return;
        if (h.itemIds != null && h.itemIds.Count > 0) wall.SelectById(h.itemIds[0]);
        else if (h.classIdx.HasValue) wall.ChooseClass(h.classIdx.Value);
    }
}
