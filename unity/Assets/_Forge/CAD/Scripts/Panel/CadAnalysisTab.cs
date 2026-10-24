using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Analysis (CAD UX spec 2.5): "On the model" toggles (markers, dimensions, part labels: the session's view state, so
/// the viewer follows), a gauge card per ranged metric (band bar, needle, ticks, status chip), the other metrics as
/// two-column stats, the size, the mass breakdown (a segmented bar and rows sorted by mass, with materials from
/// metadata.json), the design's own plot (rocket: the flight profile with its events; quadcopter: the thrust budget;
/// gearbox: speed and torque in and out), the fixed inputs (analysis.constants: the motor, the power system) as info
/// rows, and the assumptions.
/// </summary>
public sealed class CadAnalysisTab : CadPanelTab
{
    const float Gap = 8f, HeadingH = 16f, ToggleH = 30f, GaugeH = 73f, StatH = 46f, MassBarH = 12f, MassRowH = 27f;
    const float PlotH = 112f;
    static readonly float[] MassShades = { 0.92f, 0.7f, 0.52f, 0.38f, 0.26f, 0.18f, 0.12f };

    sealed class Gauge
    {
        public RectTransform root;
        public TextMeshProUGUI label, value, chipText;
        public ForgeSurface chip;
        public CadShape bar;
        public CadMetric metric;
        public readonly List<TextMeshProUGUI> ticks = new List<TextMeshProUGUI>();
        public readonly List<float> values = new List<float>();
    }

    sealed class Stat
    {
        public RectTransform root;
        public TextMeshProUGUI label, value;
    }

    sealed class MassRow
    {
        public RectTransform root;
        public ForgeSurface swatch;
        public TextMeshProUGUI name, grams, share;
    }

    readonly TextMeshProUGUI onModel;
    readonly CadButton markers, dimensions, labels;
    readonly List<RectTransform> toggleRects = new List<RectTransform>();
    readonly List<Gauge> gauges = new List<Gauge>();
    readonly List<Stat> stats = new List<Stat>();
    readonly TextMeshProUGUI sizeLine, massHeading, massTotal, plotHeading, plotNote, plotWarn, assumeHeading, assumptions;
    readonly CadShape massBar;
    readonly List<MassRow> massRows = new List<MassRow>();
    readonly List<CadMassItem> mass = new List<CadMassItem>();
    readonly List<string> massSubs = new List<string>();

    // Rocket: the flight profile.
    readonly RectTransform plot;
    readonly CadShape plotShape;
    readonly CadPanelDraw.Flight flight = new CadPanelDraw.Flight();
    readonly List<TextMeshProUGUI> gridLabels = new List<TextMeshProUGUI>(), eventLabels = new List<TextMeshProUGUI>();
    // Quadcopter: the thrust budget. Gearbox: speed and torque bars.
    readonly RectTransform budget;
    readonly ForgeSurface budgetFill;
    readonly TextMeshProUGUI budgetLeft, budgetRight;
    readonly RectTransform[] pairRows = new RectTransform[4];
    readonly TextMeshProUGUI[] pairLabels = new TextMeshProUGUI[4], pairValues = new TextMeshProUGUI[4];
    readonly ForgeSurface[] pairFills = new ForgeSurface[4];
    readonly float[] pairShares = new float[4];
    readonly TextMeshProUGUI fixedHeading;
    readonly List<TextMeshProUGUI[]> fixedRows = new List<TextMeshProUGUI[]>();  // label, value, note
    int fixedCount;
    string plotKind, metadataFor;
    float budgetShare;

    public CadAnalysisTab(RectTransform parent, CadParamsPanel owner) : base(parent, "Analysis", owner)
    {
        onModel = Heading(Root, "On the model");
        markers = Toggle("Markers", () => owner.ToggleOverlay(0));
        dimensions = Toggle("Dimensions", () => owner.ToggleOverlay(1));
        labels = Toggle("Part labels", () => owner.ToggleOverlay(2));

        sizeLine = CadUi.Text(Root, "Size", CadUi.Small, CadUi.White(0.5f));
        massHeading = Heading(Root, "Mass");
        massTotal = CadUi.Text(Root, "Total", 12f, CadUi.White(0.75f), bold: true, align: TextAlignmentOptions.Right);
        massBar = CadUi.Shape(Root, "MassBar", PaintMassBar);

        plotHeading = Heading(Root, "Flight profile");
        var plotFill = CadUi.Surface(Root, "Plot", new Color(0f, 0f, 0f, 0.14f), 12f);
        plot = plotFill.rectTransform;
        plotShape = CadUi.Shape(plot, "Curve", PaintPlot);
        CadUi.Stretch(plotShape.rectTransform, 0f, 0f, 0f, 0f);
        plotNote = CadUi.Text(Root, "Note", CadUi.Overline, CadUi.White(0.48f), wrap: true);
        plotNote.alignment = TextAlignmentOptions.TopLeft;
        CadUi.LineHeight(plotNote, 1.4f);
        plotWarn = CadUi.Text(Root, "Warning", 12f, CadUi.WarnText, wrap: true);
        plotWarn.alignment = TextAlignmentOptions.TopLeft;

        var track = CadUi.Surface(Root, "Budget", CadUi.White(0.1f), 6f);
        budget = track.rectTransform;
        budgetFill = CadUi.Surface(track.transform, "Fill", CadUi.Hue, 6f);
        budgetFill.Glow = 0.4f;
        budgetLeft = CadUi.Text(Root, "Weight", CadUi.Small, CadUi.White(0.6f));
        budgetRight = CadUi.Text(Root, "Thrust", CadUi.Small, CadUi.White(0.6f), align: TextAlignmentOptions.Right);
        for (int i = 0; i < 4; i++)
        {
            pairRows[i] = CadUi.Rect(Root, "Pair" + i);
            pairLabels[i] = CadUi.Text(pairRows[i], "Label", CadUi.Small, CadUi.White(0.5f));
            var bar = CadUi.Surface(pairRows[i], "Bar", CadUi.White(0.07f), 4f);
            pairFills[i] = CadUi.Surface(bar.transform, "Fill", i % 2 == 0 ? CadUi.White(0.75f) : CadUi.Hue, 4f);
            pairValues[i] = CadUi.Text(pairRows[i], "Value", CadUi.Body, Color.white, bold: true, align: TextAlignmentOptions.Right);
        }

        fixedHeading = Heading(Root, "Fixed inputs");
        assumeHeading = Heading(Root, "Assumptions");
        assumptions = CadUi.Text(Root, "Assumptions", CadUi.Small, CadUi.White(0.55f), wrap: true);
        assumptions.alignment = TextAlignmentOptions.TopLeft;
        CadUi.LineHeight(assumptions, 1.45f);
    }

    CadButton Toggle(string text, UnityEngine.Events.UnityAction click)
    {
        var b = CadButton.Create(Root, text, CadButton.Pill, 0f, click, pill: true);
        var look = CadButton.Pill;
        look.ink = CadUi.White(0.85f);
        b.SetLook(look);
        b.AddLabel(text, CadUi.Body);
        toggleRects.Add(b.Rect);
        return b;
    }

    public override void Show(CadPanelState s)
    {
        var a = s.a;
        string kind = s.demo?.kind;
        string markerText = kind == CadDemoPlayer.Launch ? "CG · CP" : kind == CadDemoPlayer.Hover ? "Prop discs" : kind == CadDemoPlayer.Spin ? "Shaft axes" :
            kind == CadDemoPlayer.Walk ? "CG · support" : "Markers";
        markers.SetText(markerText);
        markers.On = s.session != null && s.session.MarkersShown;
        dimensions.On = s.session != null && s.session.Measuring;
        labels.On = s.session != null && s.session.LabelsShown;
        Size(markers, markerText);
        Size(dimensions, "Dimensions");
        Size(labels, "Part labels");

        // Gauges for metrics with a range; stats for the rest (but the size, which gets its own line).
        int g = 0, k = 0;
        string size = null;
        if (a?.metrics != null)
            foreach (var m in a.metrics)
            {
                if (m.IsGauge) ShowGauge(g++, m);
                else if (m.id == "size") size = m.display;
                else ShowStat(k++, m);
            }
        for (int i = g; i < gauges.Count; i++) CadUi.Show(gauges[i].root, false);
        for (int i = k; i < stats.Count; i++) CadUi.Show(stats[i].root, false);
        sizeLine.text = size != null ? "Size " + CadUi.Clean(size).Replace(" x ", " × ") : "";
        CadUi.Show(sizeLine, size != null);

        ShowMass(s);
        ShowPlot(s);
        ShowFixed(s.a?.constants);

        // Assumptions: the analysis's, the demo's, the flight's (each once).
        var lines = new List<string>();
        void AddAll(List<string> list)
        {
            if (list == null) return;
            foreach (var x in list)
                if (!string.IsNullOrEmpty(x) && !lines.Contains(x)) lines.Add(x);
        }
        AddAll(a?.assumptions);
        AddAll(s.demo?.assumptions);
        AddAll(s.flight?.assumptions);
        var b = new System.Text.StringBuilder();
        foreach (var x in lines) b.Append(b.Length > 0 ? "\n" : "").Append("•  ").Append(CadUi.Clean(x));
        assumptions.text = b.ToString();
        CadUi.Show(assumeHeading, lines.Count > 0);
        CadUi.Show(assumptions, lines.Count > 0);
    }

    static void Size(CadButton b, string text) =>
        b.Rect.sizeDelta = new Vector2(CadUi.Width(b.Label, text) + 24f, ToggleH);

    void ShowGauge(int i, CadMetric m)
    {
        while (gauges.Count <= i) gauges.Add(NewGauge());
        var gg = gauges[i];
        CadUi.Show(gg.root, true);
        gg.metric = m;
        gg.label.text = CadUi.Clean(m.label);
        gg.value.text = CadUi.Clean(m.display);
        bool chip = !string.IsNullOrEmpty(m.note);
        CadUi.Show(gg.chip, chip);
        if (chip)
        {
            gg.chipText.text = m.note;
            gg.chipText.color = CadUi.StatusText(m.status);
            gg.chip.color = CadUi.Alpha(CadUi.Status(m.status), m.status == "bad" ? 0.15f : 0.14f);
        }
        gg.bar.Redraw();
        CadPanelState.Ticks(m, gg.values, thin: false);
        while (gg.ticks.Count < gg.values.Count) gg.ticks.Add(CadUi.Text(gg.root, "Tick", CadUi.Tick, CadUi.White(0.45f)));
        for (int t = 0; t < gg.ticks.Count; t++)
        {
            bool used = t < gg.values.Count;
            CadUi.Show(gg.ticks[t], used);
            if (!used) continue;
            bool last = t == gg.values.Count - 1;
            gg.ticks[t].text = CadPanelState.TickText(gg.values[t]) + (last && !string.IsNullOrEmpty(m.unit) ? " " + CadUi.Clean(m.unit) : "");
        }
    }

    Gauge NewGauge()
    {
        var gg = new Gauge();
        var fill = CadUi.Surface(Root, "Gauge", CadUi.White(0.04f), 14f, 0.06f);
        gg.root = fill.rectTransform;
        gg.label = CadUi.Text(gg.root, "Label", CadUi.Body, CadUi.White(0.75f));
        gg.value = CadUi.Text(gg.root, "Value", 15f, Color.white, bold: true, align: TextAlignmentOptions.Right);
        gg.chip = CadUi.Surface(gg.root, "Chip", CadUi.White(0.1f), 10f);
        gg.chipText = CadUi.Text(gg.chip.transform, "Text", CadUi.Unit, Color.white, bold: true, align: TextAlignmentOptions.Center);
        gg.chipText.characterSpacing = 8f;
        gg.chipText.fontStyle |= FontStyles.UpperCase;
        CadUi.Stretch(gg.chipText.rectTransform, 0f, 0f, 0f, 0f);
        gg.bar = CadUi.Shape(gg.root, "Bar", s => CadPanelDraw.BandBar(s, gg.metric, 0f, 7f, s.Width, 8f, 4f, 18f, 5f, 2f));
        return gg;
    }

    void ShowStat(int i, CadMetric m)
    {
        while (stats.Count <= i)
        {
            var st = new Stat();
            var fill = CadUi.Surface(Root, "Stat", CadUi.White(0.04f), 11f);
            st.root = fill.rectTransform;
            st.label = CadUi.Text(st.root, "Label", CadUi.Overline, CadUi.White(0.5f));
            CadUi.Band(st.label.rectTransform, 7f, 14f, 10f, 8f);
            st.value = CadUi.Text(st.root, "Value", CadUi.Value, Color.white, bold: true);
            CadUi.Band(st.value.rectTransform, 22f, 18f, 10f, 8f);
            stats.Add(st);
        }
        var s = stats[i];
        CadUi.Show(s.root, true);
        s.label.text = CadUi.Clean(m.label);
        s.value.text = CadUi.Clean(m.display);
        s.value.color = m.status == "bad" ? CadUi.BadText : m.status == "warn" ? CadUi.WarnText : Color.white;
    }

    // ---- Mass: total, bar, rows by mass with their material ----

    void ShowMass(CadPanelState s)
    {
        mass.Clear();
        massSubs.Clear();
        if (s.a?.massBreakdown != null) mass.AddRange(s.a.massBreakdown);
        mass.Sort((x, y) => y.massG.CompareTo(x.massG));
        float total = 0f;
        foreach (var m in mass) total += m.massG;
        massTotal.text = total > 0f ? CadLook.Grams(total) : "";
        var materials = Materials(s);
        for (int i = 0; i < mass.Count; i++)
        {
            string label = CadUi.Clean(mass[i].label), sub = null;
            int paren = label.IndexOf('(');
            if (paren > 0 && label.EndsWith(")"))
            {
                sub = label.Substring(paren + 1, label.Length - paren - 2);
                label = label.Substring(0, paren).TrimEnd();
            }
            if (sub == null && materials != null)
            {
                string material = null;
                bool mixed = false;
                foreach (var id in mass[i].partIds)
                    if (materials.TryGetValue(id, out var mat) && !string.IsNullOrEmpty(mat))
                    {
                        if (material == null) material = mat;
                        else if (material != mat) mixed = true;
                    }
                if (material != null && !mixed) sub = material + (mass[i].partIds.Count > 1 ? " · ×" + mass[i].partIds.Count : "");
            }
            massSubs.Add(label + (sub != null ? "<space=6><size=88%><alpha=#6B>" + sub : ""));
        }
        while (massRows.Count < mass.Count)
        {
            var r = new MassRow();
            r.root = CadUi.Rect(Root, "Mass");
            r.swatch = CadUi.Surface(r.root, "Swatch", Color.white, 3f);
            r.name = CadUi.Text(r.root, "Name", CadUi.Body, CadUi.White(0.82f));
            r.grams = CadUi.Text(r.root, "Grams", CadUi.Body, Color.white, bold: true, align: TextAlignmentOptions.Right);
            r.share = CadUi.Text(r.root, "Share", CadUi.Overline, CadUi.White(0.5f), align: TextAlignmentOptions.Right);
            var line = CadUi.Surface(r.root, "Rule", CadUi.White(0.05f), 0f);
            CadUi.Band(line.rectTransform, MassRowH - 1f, 1f);
            massRows.Add(r);
        }
        for (int i = 0; i < massRows.Count; i++)
        {
            bool used = i < mass.Count;
            CadUi.Show(massRows[i].root, used);
            if (!used) continue;
            var r = massRows[i];
            r.swatch.color = CadUi.White(MassShades[Mathf.Min(i, MassShades.Length - 1)]);
            r.name.text = massSubs[i];
            r.grams.text = CadLook.Grams(mass[i].massG);
            r.share.text = total > 0f ? Mathf.RoundToInt(mass[i].massG / total * 100f) + "%" : "";
        }
        bool any = mass.Count > 0;
        CadUi.Show(massHeading, any);
        CadUi.Show(massTotal, any);
        CadUi.Show(massBar, any);
        massBar.Redraw();
    }

    // Part id -> material name, from this version's metadata.json (read once per version).
    Dictionary<string, string> materialCache;

    Dictionary<string, string> Materials(CadPanelState s)
    {
        var latest = s.session?.Latest;
        if (latest == null || string.IsNullOrEmpty(latest.metadata)) return null;
        string key = latest.info.version.ToString();
        if (key == metadataFor && materialCache != null) return materialCache;
        metadataFor = key;
        materialCache = new Dictionary<string, string>();
        try
        {
            var root = Newtonsoft.Json.Linq.JObject.Parse(latest.metadata);
            if (root["objects"] is Newtonsoft.Json.Linq.JArray objects)
                foreach (var o in objects)
                {
                    string id = (string)o["id"], name = (string)o["material"]?["name"];
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) materialCache[id] = name;
                }
        }
        catch (Newtonsoft.Json.JsonException) { }
        return materialCache;
    }

    void PaintMassBar(CadShape s)
    {
        float total = 0f;
        foreach (var m in mass) total += m.massG;
        if (total <= 0f) return;
        float w = s.Width, available = w - 2f * (mass.Count - 1), x = 0f;
        for (int i = 0; i < mass.Count; i++)
        {
            float bw = mass[i].massG / total * available;
            if (bw > 0.05f) s.Begin().RoundRect(x, 0f, bw, MassBarH, Mathf.Min(3f, bw * 0.5f)).Fill(CadUi.White(MassShades[Mathf.Min(i, MassShades.Length - 1)]));
            x += bw + 2f;
        }
    }

    // ---- The design's plot ----

    void ShowPlot(CadPanelState s)
    {
        var values = s.demo?.values;
        float Get(string key, float fallback) => values != null && values.TryGetValue(key, out float v) ? v : fallback;
        plotKind = s.flight != null || s.demo?.kind == CadDemoPlayer.Launch ? "flight" : s.demo?.kind == CadDemoPlayer.Hover ? "budget" : s.demo?.kind == CadDemoPlayer.Spin ? "pairs" : null;
        bool fly = plotKind == "flight" && s.flight != null;
        CadUi.Show(plot, fly);
        CadUi.Show(plotHeading, plotKind != null && (plotKind != "flight" || fly));
        CadUi.Show(plotWarn, false);
        CadUi.Show(budget, plotKind == "budget");
        CadUi.Show(budgetLeft, plotKind == "budget");
        CadUi.Show(budgetRight, plotKind == "budget");
        for (int i = 0; i < 4; i++) CadUi.Show(pairRows[i], plotKind == "pairs");
        plotNote.text = "";
        foreach (var t in gridLabels) CadUi.Show(t, false);
        foreach (var t in eventLabels) CadUi.Show(t, false);

        if (fly)
        {
            plotHeading.text = "Flight profile";
            flight.Set(s.flight);
            var f = s.flight;
            plotNote.text = "Off the rail at " + CadUi.F(f.railExitSpeedMps, "0.0") + " m/s · burnout " + CadUi.F(flight.burnout, "0.00") +
                            " s at " + CadUi.F(flight.AltitudeAt(flight.burnout), "0") + " m" +
                            (flight.deploys ? " · chute at " + CadUi.F(flight.deploy, "0.0") + " s · down at " + CadUi.F(f.descentRateMps, "0.0") + " m/s" : "") +
                            " · lands at " + CadUi.F(flight.land, "0") + " s, " + CadUi.F(f.driftM, "0") + " m out";
            plotWarn.text = CadUi.Clean(f.warning ?? "");
            CadUi.Show(plotWarn, !string.IsNullOrEmpty(f.warning));
            plotShape.Redraw();
        }
        else if (plotKind == "budget")
        {
            plotHeading.text = "Thrust budget";
            float weight = Get("weight_g", s.a != null ? s.a.massG : 0f), perMotor = Get("max_thrust_g", 0f);
            int motors = s.demo.spinParts != null && s.demo.spinParts.Count > 0 ? s.demo.spinParts.Count : 4;
            float total = perMotor * motors;
            budgetShare = total > 0f ? Mathf.Clamp01(weight / total) : 0f;
            budgetLeft.text = CadUi.F(weight, "0") + " g all-up";
            budgetRight.text = CadUi.F(total / 1000f, "0.0") + " kg max thrust";
            bool blocked = !string.IsNullOrEmpty(s.demo.blocked);
            plotNote.text = blocked ? "No hover estimate while the props collide."
                : "Hover needs " + Mathf.RoundToInt(budgetShare * 100f) + "% of max thrust, about " + CadUi.F(Get("hover_throttle_pct", 0f), "0") +
                  "% throttle at " + CadUi.Thousands(Get("hover_rpm", 0f)) + " rpm.";
        }
        else if (plotKind == "pairs")
        {
            plotHeading.text = "Speed and torque";
            float inRpm = Get("input_rpm", 3000f), ratio = Mathf.Max(0.01f, Get("ratio", 1f)), outRpm = Get("output_rpm", inRpm / ratio);
            float inNm = Get("input_torque_nm", 0.5f), outNm = Get("output_torque_nm", inNm * ratio);
            float fastest = Mathf.Max(inRpm, outRpm, 1f), strongest = Mathf.Max(inNm, outNm, 0.01f);
            Pair(0, "Speed", "In " + CadUi.Thousands(inRpm) + " rpm", inRpm / fastest);
            Pair(1, "", "Out " + CadUi.Thousands(outRpm) + " rpm", outRpm / fastest);
            Pair(2, "Torque", "In " + CadUi.F(inNm, "0.00") + " N·m", inNm / strongest);
            Pair(3, "", "Out " + CadUi.F(outNm, "0.00") + " N·m", outNm / strongest);
        }
        CadUi.Show(plotNote, !string.IsNullOrEmpty(plotNote.text));
    }

    void Pair(int i, string label, string value, float share)
    {
        pairLabels[i].text = label;
        pairValues[i].text = value;
        pairShares[i] = Mathf.Clamp01(share);
    }

    // Altitude against time, launch to landing: grid, a faint orange area, the line, the events.
    void PaintPlot(CadShape s)
    {
        if (flight.Empty) return;
        float w = s.Width, h = s.Height;
        for (int a = 50; a <= 400; a += 50)
        {
            if (a > flight.apogeeAlt * 1.08f) break;
            float y = CadPanelDraw.PlotY(flight, a, h);
            s.Line(6f, y, w - 6f, y, 1f, CadUi.White(0.07f));
        }
        CadPanelDraw.CurvePath(s, flight, w, h, 1f);
        s.FillUnder(CadPanelDraw.PlotY(flight, 0f, h), CadUi.HueA(0.1f));
        s.Stroke(1.6f, CadUi.White(0.9f));
        PlotEvent(s, flight.burnout, w, h);
        PlotEvent(s, flight.apogeeT, w, h);
        if (flight.deploys) PlotEvent(s, flight.deploy, w, h);
        PlotEvent(s, flight.land, w, h);
    }

    void PlotEvent(CadShape s, float t, float w, float h) =>
        s.Disc(CadPanelDraw.PlotX(flight, t, w, 1f), CadPanelDraw.PlotY(flight, flight.AltitudeAt(t), h), 3f, Color.white);

    // The plot's labels follow its width.
    void LayoutPlotLabels(float w)
    {
        int g = 0;
        for (int a = 50; a <= 400; a += 50)
        {
            if (a > flight.apogeeAlt * 1.08f) break;
            var t = Label(gridLabels, g++, CadUi.Tick, CadUi.White(0.35f), false);
            t.text = a + " m";
            t.alignment = TextAlignmentOptions.TopRight;
            CadUi.Place(t.rectTransform, w - 6f - 60f, CadPanelDraw.PlotY(flight, a, PlotH) - 7f, 60f, 12f);
        }
        int e = 0;
        EventLabel(ref e, "Burnout " + CadUi.F(flight.burnout, "0.0") + " s", flight.burnout, w, 0);
        EventLabel(ref e, "Apogee " + CadUi.F(flight.apogeeAlt, "0") + " m", flight.apogeeT, w, 0);
        if (flight.deploys) EventLabel(ref e, "Chute " + CadUi.F(flight.deploy, "0.0") + " s", flight.deploy, w, 1);
        EventLabel(ref e, "Lands " + CadUi.F(flight.land, "0.0") + " s", flight.land, w, 2);
    }

    void EventLabel(ref int index, string text, float t, float w, int style)
    {
        var l = Label(eventLabels, index++, CadUi.Unit, CadUi.White(0.85f), true);
        l.text = text;
        float x = CadPanelDraw.PlotX(flight, t, w, 1f), y = CadPanelDraw.PlotY(flight, flight.AltitudeAt(t), PlotH);
        if (style == 2)
        {
            l.alignment = TextAlignmentOptions.TopRight;
            CadUi.Place(l.rectTransform, x - 6f - 120f, y - 20f, 120f, 13f);
        }
        else
        {
            l.alignment = TextAlignmentOptions.TopLeft;
            CadUi.Place(l.rectTransform, style == 1 ? x + 8f : x + 6f, style == 1 ? y + 6f : y - 18f, 120f, 13f);
        }
    }

    TextMeshProUGUI Label(List<TextMeshProUGUI> pool, int i, float size, Color color, bool bold)
    {
        while (pool.Count <= i) pool.Add(CadUi.Text(plot, "Label", size, color, bold));
        CadUi.Show(pool[i], true);
        return pool[i];
    }

    // ---- Fixed inputs: label, value, note ----

    void ShowFixed(List<CadConstant> list)
    {
        fixedCount = list != null ? list.Count : 0;
        while (fixedRows.Count < fixedCount)
        {
            var label = CadUi.Text(Root, "Fixed", CadUi.Body, CadUi.White(0.8f));
            var value = CadUi.Text(Root, "Value", CadUi.Body, CadUi.White(0.75f), wrap: true);
            value.alignment = TextAlignmentOptions.TopLeft;
            var note = CadUi.Text(Root, "Note", CadUi.Unit, CadUi.White(0.45f), wrap: true);
            note.alignment = TextAlignmentOptions.TopLeft;
            fixedRows.Add(new[] { label, value, note });
        }
        for (int i = 0; i < fixedRows.Count; i++)
        {
            bool used = i < fixedCount;
            foreach (var t in fixedRows[i]) CadUi.Show(t, used);
            if (!used) continue;
            fixedRows[i][0].text = CadUi.Clean(list[i].label);
            fixedRows[i][1].text = CadUi.Clean(list[i].value);
            fixedRows[i][2].text = CadUi.Clean(list[i].note ?? "");
        }
        CadUi.Show(fixedHeading, fixedCount > 0);
    }

    // ---- Layout ----

    public override float Layout(float w)
    {
        float y = 0f;
        CadUi.Band(onModel.rectTransform, y + 4f, HeadingH);
        y += 4f + HeadingH + Gap;
        y += CadUi.Flow(toggleRects, w, 6f, 6f, y) + Gap;

        foreach (var gg in gauges)
        {
            if (!gg.root.gameObject.activeSelf) continue;
            CadUi.Band(gg.root, y, GaugeH);
            float chipW = gg.chip.gameObject.activeSelf ? CadUi.Width(gg.chipText, (gg.metric?.note ?? "").ToUpperInvariant()) + 18f : 0f;
            float valueW = CadUi.Width(gg.value, gg.value.text) + 2f;
            if (chipW > 0f) CadUi.PlaceRight(gg.chip.rectTransform, 12f, 10f + 1f, chipW, 18f);
            CadUi.PlaceRight(gg.value.rectTransform, 12f + (chipW > 0f ? chipW + 8f : 0f), 10f, valueW, 20f);
            CadUi.Band(gg.label.rectTransform, 10f, 20f, 12f, 12f + valueW + 8f + (chipW > 0f ? chipW + 8f : 0f));
            CadUi.Band(gg.bar.rectTransform, 10f + 20f + 9f - 7f, 22f, 12f, 12f);
            int n = 0;
            foreach (var t in gg.ticks) if (t.gameObject.activeSelf) n++;
            float barW = w - 24f;
            for (int i = 0; i < n; i++)
            {
                var t = gg.ticks[i];
                float share = gg.metric != null && gg.metric.IsGauge && i < gg.values.Count
                    ? Mathf.Clamp01((gg.values[i] - gg.metric.min.Value) / (gg.metric.max.Value - gg.metric.min.Value)) : 0f;
                float x = 12f + share * barW;
                if (i == 0) { t.alignment = TextAlignmentOptions.TopLeft; CadUi.Place(t.rectTransform, x, 10f + 20f + 9f + 8f + 5f, 80f, 13f); }
                else if (i == n - 1) { t.alignment = TextAlignmentOptions.TopRight; CadUi.Place(t.rectTransform, x - 80f, 10f + 20f + 9f + 8f + 5f, 80f, 13f); }
                else { t.alignment = TextAlignmentOptions.Top; CadUi.Place(t.rectTransform, x - 40f, 10f + 20f + 9f + 8f + 5f, 80f, 13f); }
            }
            y += GaugeH + Gap;
        }

        int shown = 0;
        float colW = (w - 6f) * 0.5f;
        foreach (var st in stats)
        {
            if (!st.root.gameObject.activeSelf) continue;
            int col = shown % 2, row = shown / 2;
            CadUi.Place(st.root, col * (colW + 6f), y + row * (StatH + 6f), colW, StatH);
            shown++;
        }
        if (shown > 0) y += ((shown + 1) / 2) * (StatH + 6f) - 6f + Gap;
        if (sizeLine.gameObject.activeSelf)
        {
            CadUi.Band(sizeLine.rectTransform, y, 15f);
            y += 15f + Gap;
        }

        if (massHeading.gameObject.activeSelf)
        {
            CadUi.Band(massHeading.rectTransform, y + 4f, HeadingH, 0f, 120f);
            CadUi.PlaceRight(massTotal.rectTransform, 0f, y + 4f, 120f, HeadingH);
            y += 4f + HeadingH + Gap;
            CadUi.Band(massBar.rectTransform, y, MassBarH);
            y += MassBarH + Gap;
            foreach (var r in massRows)
            {
                if (!r.root.gameObject.activeSelf) continue;
                CadUi.Band(r.root, y, MassRowH);
                CadUi.Place(r.swatch.rectTransform, 1.5f, (MassRowH - 9f) * 0.5f, 9f, 9f);
                CadUi.PlaceRight(r.share.rectTransform, 0f, 0f, 38f, MassRowH);
                float gw = CadUi.Width(r.grams, r.grams.text) + 2f;
                CadUi.PlaceRight(r.grams.rectTransform, 38f + 8f, 0f, gw, MassRowH);
                CadUi.Band(r.name.rectTransform, 0f, MassRowH, 20f, 38f + 8f + gw + 8f);
                y += MassRowH;
            }
            y += Gap;
        }

        if (plotHeading.gameObject.activeSelf)
        {
            CadUi.Band(plotHeading.rectTransform, y + 4f, HeadingH);
            y += 4f + HeadingH + Gap;
            if (plot.gameObject.activeSelf)
            {
                CadUi.Band(plot, y, PlotH);
                LayoutPlotLabels(w);
                y += PlotH + Gap;
            }
            if (budget.gameObject.activeSelf)
            {
                CadUi.Band(budget, y, 12f);
                var fill = budgetFill.rectTransform;
                fill.anchorMin = Vector2.zero;
                fill.anchorMax = new Vector2(Mathf.Max(0.015f, budgetShare), 1f);
                fill.offsetMin = fill.offsetMax = Vector2.zero;
                y += 12f + 6f;
                CadUi.Band(budgetLeft.rectTransform, y, 15f, 0f, w * 0.5f);
                CadUi.Band(budgetRight.rectTransform, y, 15f, w * 0.5f, 0f);
                y += 15f + Gap;
            }
            if (pairRows[0].gameObject.activeSelf)
            {
                for (int i = 0; i < 4; i++)
                {
                    CadUi.Band(pairRows[i], y, 18f);
                    CadUi.Place(pairLabels[i].rectTransform, 0f, 0f, 52f, 18f);
                    CadUi.PlaceRight(pairValues[i].rectTransform, 0f, 0f, 120f, 18f);
                    var bar = (RectTransform)pairFills[i].transform.parent;
                    CadUi.Band(bar, 5f, 8f, 60f, 128f);
                    var fill = pairFills[i].rectTransform;
                    fill.anchorMin = Vector2.zero;
                    fill.anchorMax = new Vector2(Mathf.Max(0.02f, pairShares[i]), 1f);
                    fill.offsetMin = fill.offsetMax = Vector2.zero;
                    y += 18f + 4f;
                }
                y += Gap - 4f;
            }
            if (plotNote.gameObject.activeSelf)
            {
                float nh = CadUi.Height(plotNote, plotNote.text, w);
                CadUi.Band(plotNote.rectTransform, y, nh + 1f);
                y += nh + Gap;
            }
            if (plotWarn.gameObject.activeSelf)
            {
                float wh = CadUi.Height(plotWarn, plotWarn.text, w);
                CadUi.Band(plotWarn.rectTransform, y, wh + 1f);
                y += wh + Gap;
            }
        }

        if (fixedCount > 0)
        {
            CadUi.Band(fixedHeading.rectTransform, y + 4f, HeadingH);
            y += 4f + HeadingH + 2f;
            for (int i = 0; i < fixedCount; i++)
            {
                var r = fixedRows[i];
                float iw = w - 124f;
                float vh = CadUi.Height(r[1], r[1].text, iw), nh = CadUi.Height(r[2], r[2].text, iw);
                float hgt = Mathf.Max(36f, 6f + vh + nh + 6f);
                CadUi.Place(r[0].rectTransform, 4f, y, 112f, Mathf.Min(hgt, 36f));
                CadUi.Band(r[1].rectTransform, y + 6f, vh + 1f, 124f, 0f);
                CadUi.Band(r[2].rectTransform, y + 6f + vh, nh + 1f, 124f, 0f);
                y += hgt + 2f;
            }
            y += Gap - 2f;
        }

        if (assumptions.gameObject.activeSelf)
        {
            CadUi.Band(assumeHeading.rectTransform, y + 4f, HeadingH);
            y += 4f + HeadingH + Gap;
            float ah = CadUi.Height(assumptions, assumptions.text, w - 4f);
            CadUi.Band(assumptions.rectTransform, y, ah + 2f, 4f, 0f);
            y += ah + 4f;
        }
        return y;
    }
}
