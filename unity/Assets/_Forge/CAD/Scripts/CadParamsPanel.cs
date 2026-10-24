using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The parameters panel (CadParamsPanel.prefab, a ForgePanel variant; this UI lives under Content).
/// Rows come from GET /cad/params; releasing a slider sends POST /cad/params. Shows the stability gauge,
/// mass and apogee, plus Undo / Reset / Explode / Fly. In mock mode the arrows step through the stored designs.
/// </summary>
public class CadParamsPanel : MonoBehaviour
{
    public CadMockSet mocks;

    [SerializeField] TMP_Text header;
    [SerializeField] Image stabilityChip;
    [SerializeField] TMP_Text stabilityText;
    [SerializeField] RectTransform gaugeNeedle;
    [SerializeField] TMP_Text gaugeLabel;
    [SerializeField] TMP_Text stats;
    [SerializeField] RectTransform rows;
    [SerializeField] CadParamRow rowTemplate;
    [SerializeField] Button undoButton, resetButton, explodeButton, flyButton, prevButton, nextButton;
    [SerializeField] TMP_Text explodeLabel;
    [SerializeField] TMP_Text status;

    /// <summary>The gauge spans 0 to this many calibers.</summary>
    public const float GaugeMaxCal = 3f;
    public static readonly Color Unstable = new Color(0.93f, 0.3f, 0.27f);
    public static readonly Color Marginal = new Color(0.98f, 0.7f, 0.2f);
    public static readonly Color Stable = new Color(0.3f, 0.8f, 0.45f);

    CadSession session;
    readonly List<CadParamRow> built = new List<CadParamRow>();
    string schema;
    CadFlight flight;
    int flightVersion = -1;

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        session.ParamsChanged += Refresh;
        session.ViewChanged += OnViewChanged;
        session.StatusChanged += OnStatus;
        session.FlightReady += OnFlight;
        undoButton.onClick.AddListener(session.Undo);
        resetButton.onClick.AddListener(session.ResetDesign);
        explodeButton.onClick.AddListener(ToggleExplode);
        flyButton.onClick.AddListener(session.RequestFlight);
        prevButton.onClick.AddListener(() => session.StepMock(-1));
        nextButton.onClick.AddListener(() => session.StepMock(1));
        OnStatus(session.Status);
        OnViewChanged(session.View);
        Refresh();
    }

    void OnDisable()
    {
        if (session == null) return;
        session.ParamsChanged -= Refresh;
        session.ViewChanged -= OnViewChanged;
        session.StatusChanged -= OnStatus;
        session.FlightReady -= OnFlight;
        foreach (var b in new[] { undoButton, resetButton, explodeButton, flyButton, prevButton, nextButton })
            b.onClick.RemoveAllListeners();
    }

    void Refresh()
    {
        bool mock = session.Ready && session.IsMock;
        prevButton.gameObject.SetActive(mock);
        nextButton.gameObject.SetActive(mock);
        var p = session.Params;
        if (p == null) return;

        string names = string.Join(",", p.parameters.ConvertAll(x => x.name));
        if (names != schema) Rebuild(p);
        else foreach (var row in built) row.SetValue(p.Find(row.Name));

        var a = session.Latest?.info.analysis;
        header.text = mock ? $"{p.part}  v{p.version}  <size=70%><alpha=#88>mock {session.MockIndex + 1}/{session.mocks.versions.Count}" : $"{p.part}  v{p.version}";
        if (a == null || !a.marginCal.HasValue) return;
        float margin = a.marginCal.Value;
        var color = a.stability == "stable" ? Stable : a.stability == "unstable" ? Unstable : Marginal;
        stabilityChip.color = color;
        stabilityText.text = a.stability.ToUpperInvariant();
        float x = Mathf.Clamp01(margin / GaugeMaxCal);
        gaugeNeedle.anchorMin = gaugeNeedle.anchorMax = new Vector2(x, 0.5f);
        gaugeLabel.text = $"Static margin <b>{margin:0.00}</b> cal";
        string apogee = flight != null && flightVersion == p.version ? $"  |  apogee ~{flight.apogeeM:0} m" : "";
        stats.text = $"{a.massG:0} g  |  CG {a.cgMm:0} mm  |  CP {a.cpMm:0} mm from tip{apogee}";
    }

    void Rebuild(CadParams p)
    {
        foreach (var row in built) Destroy(row.gameObject);
        built.Clear();
        foreach (var param in p.parameters)
        {
            var row = Instantiate(rowTemplate, rows);
            row.gameObject.SetActive(true);
            row.name = "Row " + param.name;
            row.Bind(param, Commit);
            built.Add(row);
        }
        schema = string.Join(",", p.parameters.ConvertAll(x => x.name));
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
