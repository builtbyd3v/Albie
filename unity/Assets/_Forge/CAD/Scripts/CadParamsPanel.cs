using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The design panel (CadParamsPanel.prefab, a ForgePanel variant; this UI lives under Content). Works for any
/// design in the hub's library: the title and a Designs menu to switch, its presets, its metrics (gauges for the
/// ones with a range, a stats line for the rest), its parameter rows from GET /cad/params (in a scroll view, so
/// the panel can be resized), and Undo / Reset / Explode, plus Fly for designs with a flight sim.
/// Releasing a slider sends POST /cad/params. In mock mode the arrows step through the stored versions.
/// </summary>
public class CadParamsPanel : MonoBehaviour
{
    public CadMockSet mocks;

    [SerializeField] TMP_Text header;
    [SerializeField] Button designsButton;
    [SerializeField] RectTransform picker;
    [SerializeField] Button pickerEntry;
    [SerializeField] RectTransform presetRow;
    [SerializeField] Button presetTemplate;
    [SerializeField] RectTransform gauges;
    [SerializeField] CadMetricGauge gaugeTemplate;
    [SerializeField] TMP_Text stats;
    [SerializeField] ScrollRect scroll;
    [SerializeField] RectTransform rows;
    [SerializeField] CadParamRow rowTemplate;
    [SerializeField] Button undoButton, resetButton, explodeButton, flyButton, prevButton, nextButton;
    [SerializeField] TMP_Text explodeLabel;
    [SerializeField] TMP_Text status;

    CadSession session;
    readonly List<CadParamRow> built = new List<CadParamRow>();
    readonly List<CadMetricGauge> gaugeViews = new List<CadMetricGauge>();
    readonly List<Button> presetButtons = new List<Button>();
    readonly List<Button> pickerButtons = new List<Button>();
    readonly StringBuilder line = new StringBuilder();
    string schema, presetKey;
    CadFlight flight;
    int flightVersion = -1;

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        session.ParamsChanged += Refresh;
        session.PartsChanged += RebuildPicker;
        session.ViewChanged += OnViewChanged;
        session.StatusChanged += OnStatus;
        session.FlightReady += OnFlight;
        designsButton.onClick.AddListener(TogglePicker);
        undoButton.onClick.AddListener(session.Undo);
        resetButton.onClick.AddListener(session.ResetDesign);
        explodeButton.onClick.AddListener(ToggleExplode);
        flyButton.onClick.AddListener(session.RequestFlight);
        prevButton.onClick.AddListener(() => session.StepMock(-1));
        nextButton.onClick.AddListener(() => session.StepMock(1));
        picker.gameObject.SetActive(false);
        OnStatus(session.Status);
        OnViewChanged(session.View);
        RebuildPicker();
        Refresh();
    }

    void OnDisable()
    {
        if (session == null) return;
        session.ParamsChanged -= Refresh;
        session.PartsChanged -= RebuildPicker;
        session.ViewChanged -= OnViewChanged;
        session.StatusChanged -= OnStatus;
        session.FlightReady -= OnFlight;
        foreach (var b in new[] { designsButton, undoButton, resetButton, explodeButton, flyButton, prevButton, nextButton })
            b.onClick.RemoveAllListeners();
    }

    void Refresh()
    {
        bool mock = session.Ready && session.IsMock;
        prevButton.gameObject.SetActive(mock);
        nextButton.gameObject.SetActive(mock);
        var p = session.Params;
        if (p == null) return;

        string names = p.part + ":" + string.Join(",", p.parameters.ConvertAll(x => x.name));
        if (names != schema)
        {
            Rebuild(p);
            scroll.verticalNormalizedPosition = 1f;  // another design: back to its first row
        }
        else foreach (var row in built) row.SetValue(p.Find(row.Name));
        BuildPresets(p);
        flyButton.gameObject.SetActive(p.Has("flight"));

        string title = string.IsNullOrEmpty(p.title) ? p.part : p.title;
        header.text = mock ? $"{title}  v{p.version}  <size=70%><alpha=#88>mock {session.MockIndex + 1}/{session.MockCount}" : $"{title}  v{p.version}";
        ShowMetrics(session.Latest?.info.analysis, p);
    }

    /// <summary>Gauges for metrics with a range; the rest on one wrapping line, coloured by status.</summary>
    void ShowMetrics(CadAnalysis a, CadParams p)
    {
        int g = 0;
        line.Clear();
        if (a?.metrics != null)
        {
            foreach (var m in a.metrics)
            {
                if (m.IsGauge)
                {
                    if (g == gaugeViews.Count)
                    {
                        var view = Instantiate(gaugeTemplate, gauges);
                        view.name = "Gauge";
                        gaugeViews.Add(view);
                    }
                    gaugeViews[g].gameObject.SetActive(true);
                    gaugeViews[g++].Show(m);
                    continue;
                }
                if (line.Length > 0) line.Append("    ");
                string color = m.status == "bad" ? "#EE4D45" : m.status == "warn" ? "#FAB333" : "#FFFFFF";
                // <nobr>: a stat never breaks across lines ("389" / "g"); the line wraps between stats.
                line.Append("<nobr><alpha=#99>").Append(m.label).Append(" <alpha=#FF><color=").Append(color).Append("><b>")
                    .Append(m.display).Append("</b></color></nobr>");
            }
        }
        for (int i = g; i < gaugeViews.Count; i++) gaugeViews[i].gameObject.SetActive(false);
        if (flight != null && flightVersion == p.version) line.Append("    <nobr><alpha=#99>Apogee <alpha=#FF><b>~").Append(flight.apogeeM.ToString("0")).Append(" m</b></nobr>");
        stats.text = line.ToString();
    }

    void Rebuild(CadParams p)
    {
        foreach (var row in built) Discard(row.gameObject);
        built.Clear();
        foreach (var param in p.parameters)
        {
            var row = Instantiate(rowTemplate, rows);
            row.gameObject.SetActive(true);
            row.name = "Row " + param.name;
            row.Bind(param, Commit);
            built.Add(row);
        }
        schema = p.part + ":" + string.Join(",", p.parameters.ConvertAll(x => x.name));
    }

    void BuildPresets(CadParams p)
    {
        string key = p.part + ":" + string.Join(",", p.presets.ConvertAll(x => x.name));
        if (key == presetKey) return;
        presetKey = key;
        foreach (var b in presetButtons) Discard(b.gameObject);
        presetButtons.Clear();
        foreach (var preset in p.presets)
        {
            var button = Instantiate(presetTemplate, presetRow);
            button.gameObject.SetActive(true);
            button.name = "Preset " + preset.name;
            button.GetComponentInChildren<TMP_Text>().text = preset.name;
            var chosen = preset;
            button.onClick.AddListener(() => session.ApplyPreset(chosen));
            presetButtons.Add(button);
        }
        presetRow.gameObject.SetActive(p.presets.Count > 0);
    }

    // Designs menu

    void TogglePicker()
    {
        bool open = !picker.gameObject.activeSelf;
        picker.gameObject.SetActive(open);
        if (open)
        {
            picker.SetAsLastSibling();
            session.RefreshParts();  // new part scripts appear without a restart
        }
    }

    void RebuildPicker()
    {
        foreach (var b in pickerButtons) Discard(b.gameObject);
        pickerButtons.Clear();
        foreach (var part in session.Parts.parts)
        {
            var button = Instantiate(pickerEntry, picker);
            button.gameObject.SetActive(true);
            button.name = "Design " + part.part;
            string title = string.IsNullOrEmpty(part.title) ? part.part : part.title;
            string detail = part.error ?? part.description;
            button.GetComponentInChildren<TMP_Text>().text =
                $"<b>{title}</b>{(part.active ? "  <alpha=#88>(open)" : "")}\n<size=75%><alpha=#99>{detail}";
            button.interactable = part.error == null;
            string id = part.part;
            button.onClick.AddListener(() =>
            {
                picker.gameObject.SetActive(false);
                session.OpenPart(id);
            });
            pickerButtons.Add(button);
        }
    }

    /// <summary>Destroy waits for the end of the frame; hide it now so layouts (and a rebuild this frame) skip it.</summary>
    static void Discard(GameObject go)
    {
        go.SetActive(false);
        Destroy(go);
    }

    void Commit(string name, object value) => session.SetParams(new Dictionary<string, object> { { name, value } });

    void ToggleExplode() => session.SetView(session.View.explode != true, session.View.focusId);

    void OnViewChanged(CadView view) => explodeLabel.text = view.explode == true ? "Assemble" : "Explode";

    void OnStatus(string text) => status.text = text;

    void OnFlight(CadFlight f)
    {
        flight = f;
        flightVersion = session.Params != null ? session.Params.version : -1;
        if (!string.IsNullOrEmpty(f.warning)) status.text = f.warning;
        Refresh();
    }
}
