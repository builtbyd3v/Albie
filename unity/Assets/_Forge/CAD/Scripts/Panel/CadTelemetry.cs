using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The verdict card while a demo runs (CAD UX spec 2.4, 218 mm), fed by CadSession.DemoFrame:
/// Fly: phase pill, "T+3.0 s" (with "· ×8" during the fast descent), altitude in 40 mm digits and speed, a context line,
/// and the flight curve filling in along the dashed whole (the hub's warning under it for an unstable version).
/// Hover: phase, height, a throttle meter with the hover line, throttle in 40 mm digits, prop speed / thrust to spare /
/// hover power, and the thrust budget bar.
/// Run: phase, "Slow motion · 100× slower", input and output dials whose hands turn at the shown speed, rpm and torque
/// under each, the ratio chip and the hub's note.
/// Built once; per frame only the numbers that changed and the moving parts (no allocations).
/// </summary>
public sealed class CadTelemetry
{
    const float CardH = 218f, Corner = 20f, PadX = 16f, PadTop = 13f, PadBottom = 12f, TopRowH = 24f, Gap = 6f;
    const float PlotH = 74f, MeterW = 22f, DialIn = 64f, DialOut = 84f;

    public readonly RectTransform Root;

    // Shared: the phase pill and the time / height / slow-motion slot.
    readonly ForgeSurface phasePill, phaseDot;
    readonly TextMeshProUGUI phaseText, timeText;
    readonly CadLiveText time;
    string phaseShown;
    float pulse;

    // Fly.
    readonly RectTransform launch;
    readonly TextMeshProUGUI numsText, lineText, warnText;
    readonly CadLiveText nums;
    readonly CadShape plotBack, plotDone;
    readonly RectTransform plotClip;
    readonly ForgeSurface cursor;
    readonly CadPanelDraw.Flight flight = new CadPanelDraw.Flight();
    CadFlight shownFlight;
    int lineStage = -1;
    float plotTop, flownX = 0.01f, cursorX, cursorY;

    // Hover.
    readonly RectTransform hover;
    readonly CadShape meter;
    readonly TextMeshProUGUI hoverNums, budgetLine;
    readonly CadLiveText hoverLive, heightLive;
    readonly TextMeshProUGUI[] rowLabels = new TextMeshProUGUI[3], rowValues = new TextMeshProUGUI[3];
    readonly CadLiveText[] rowLive = new CadLiveText[3];
    readonly ForgeSurface budgetFill;
    readonly RectTransform budgetTrack;
    float throttle, hoverThrottle = 0.3f, hoverRpm, spare, power, budgetShare;

    // Run.
    readonly RectTransform spin;
    readonly CadShape dialIn, dialOut;
    readonly RectTransform handIn, handOut;
    readonly TextMeshProUGUI rpmIn, rpmOut, torqueIn, torqueOut, ratioText, noteText;
    readonly CadLiveText rpmInLive, rpmOutLive;
    readonly RectTransform ratioChip;
    float angleIn, inRpm = 3000f, outRpm = 1000f, ratio = 3f, slowdown = 100f;

    string kind;
    CadPanelState state;

    public CadTelemetry(RectTransform parent)
    {
        // Its own canvas: the numbers, the curve, the cursor and the dial hands move every frame of a demo, and only this
        // card should re-batch for it (nothing here takes the pointer).
        Root = CadUi.Layer(parent, "Telemetry");
        var fill = CadUi.Surface(Root, "Fill", CadUi.White(0.045f), Corner, 0.07f);
        CadUi.Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);
        var wash = CadUi.Shape(Root, "Wash", s =>
            s.Wash(s.Width, s.Height, Corner, 0.1f * s.Width, 0f, 0.9f * s.Width * 0.849f, s.Height * 0.849f, CadUi.Hue, 0.16f));
        CadUi.Stretch(wash.rectTransform, 0f, 0f, 0f, 0f);

        phasePill = CadUi.Pill(Root, "Phase", CadUi.HueA(0.16f));
        phaseDot = CadUi.Pill(phasePill.transform, "Dot", CadUi.Hue, glow: 0.55f);
        phaseText = CadUi.Text(phasePill.transform, "Text", CadUi.Overline, CadUi.PhaseText, bold: true);
        phaseText.characterSpacing = 10f;
        phaseText.fontStyle |= FontStyles.UpperCase;
        timeText = CadUi.Text(Root, "Time", 13f, CadUi.White(0.75f), bold: true, align: TextAlignmentOptions.Right);
        CadUi.Band(timeText.rectTransform, PadTop, TopRowH, PadX, PadX);
        time = new CadLiveText(timeText);

        // ---- Fly ----
        launch = CadUi.Rect(Root, "Fly");
        CadUi.Stretch(launch, 0f, 0f, 0f, 0f);
        numsText = CadUi.Text(launch, "Numbers", CadUi.Digits, Color.white, align: TextAlignmentOptions.TopLeft);
        numsText.characterSpacing = -2f;
        numsText.overflowMode = TextOverflowModes.Overflow;
        nums = new CadLiveText(numsText);
        float numsTop = PadTop + TopRowH + Gap;
        CadUi.Band(numsText.rectTransform, numsTop + (1f - CadUi.InterLine) * CadUi.Digits * 0.5f, 52f, PadX, PadX);
        lineText = CadUi.Text(launch, "Line", CadUi.Body, CadUi.White(0.62f));
        CadUi.Band(lineText.rectTransform, numsTop + CadUi.Digits + Gap, 17f, PadX, PadX);
        warnText = CadUi.Text(launch, "Warning", 12f, CadUi.WarnText);
        plotBack = CadUi.Shape(launch, "Plot", PaintPlotBack);
        plotClip = CadUi.Rect(launch, "Flown");
        plotClip.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        plotDone = CadUi.Shape(plotClip, "Curve", PaintPlotDone);
        cursor = CadUi.Pill(launch, "Cursor", Color.white, glow: 0.3f);

        // ---- Hover ----
        hover = CadUi.Rect(Root, "Hover");
        CadUi.Stretch(hover, 0f, 0f, 0f, 0f);
        meter = CadUi.Shape(hover, "Meter", PaintMeter);
        hoverNums = CadUi.Text(hover, "Numbers", CadUi.Digits, Color.white, align: TextAlignmentOptions.TopLeft);
        hoverNums.characterSpacing = -2f;
        hoverNums.overflowMode = TextOverflowModes.Overflow;
        hoverLive = new CadLiveText(hoverNums);
        heightLive = time;
        for (int i = 0; i < 3; i++)
        {
            var cell = CadUi.Surface(hover, "Readout" + i, CadUi.White(0.05f), 10f);
            rowLabels[i] = CadUi.Text(cell.transform, "Label", CadUi.Unit, CadUi.White(0.5f));
            CadUi.Band(rowLabels[i].rectTransform, 6f, 13f, 8f, 6f);
            rowValues[i] = CadUi.Text(cell.transform, "Value", 13.5f, Color.white, bold: true);
            CadUi.Band(rowValues[i].rectTransform, 21f, 17f, 8f, 6f);
            rowLive[i] = new CadLiveText(rowValues[i]);
        }
        rowLabels[0].text = "Prop speed";
        rowLabels[1].text = "Thrust to spare";
        rowLabels[2].text = "Hover power";
        var track = CadUi.Surface(hover, "Budget", CadUi.White(0.1f), 4f);
        budgetTrack = track.rectTransform;
        budgetFill = CadUi.Surface(track.transform, "Fill", CadUi.Hue, 4f);
        budgetFill.Glow = 0.4f;
        budgetLine = CadUi.Text(hover, "Line", CadUi.Body, CadUi.White(0.62f), wrap: true);
        budgetLine.alignment = TextAlignmentOptions.TopLeft;
        CadUi.LineHeight(budgetLine, 1.35f);

        // ---- Run ----
        spin = CadUi.Rect(Root, "Run");
        CadUi.Stretch(spin, 0f, 0f, 0f, 0f);
        dialIn = CadUi.Shape(spin, "DialIn", s => PaintSpinDial(s));
        dialOut = CadUi.Shape(spin, "DialOut", s => PaintSpinDial(s));
        handIn = Hand(dialIn.rectTransform, DialIn);
        handOut = Hand(dialOut.rectTransform, DialOut);
        rpmIn = CadUi.Text(spin, "RpmIn", 20f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        rpmOut = CadUi.Text(spin, "RpmOut", 20f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        rpmInLive = new CadLiveText(rpmIn);
        rpmOutLive = new CadLiveText(rpmOut);
        torqueIn = CadUi.Text(spin, "TorqueIn", CadUi.Small, CadUi.White(0.55f), align: TextAlignmentOptions.Center);
        torqueOut = CadUi.Text(spin, "TorqueOut", CadUi.Small, CadUi.White(0.55f), align: TextAlignmentOptions.Center);
        var chip = CadUi.Surface(spin, "Ratio", CadUi.White(0.08f), 9f);
        ratioChip = chip.rectTransform;
        ratioText = CadUi.Text(chip.transform, "Text", 12f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        CadUi.Stretch(ratioText.rectTransform, 0f, 0f, 0f, 0f);
        noteText = CadUi.Text(spin, "Note", CadUi.Body, CadUi.White(0.62f), wrap: true);
        noteText.alignment = TextAlignmentOptions.TopLeft;
        CadUi.LineHeight(noteText, 1.35f);
    }

    // A dial's hand: from the centre to 7 mm short of the rim, turning about its foot.
    static RectTransform Hand(RectTransform dial, float size)
    {
        var hand = CadUi.Pill(dial, "Hand", CadUi.Hue, glow: 0.5f);
        var rt = hand.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(3f, size * 0.5f - 7f);
        rt.anchoredPosition = Vector2.zero;
        return rt;
    }

    /// <summary>A demo started: set the card up for its kind and this version's numbers.</summary>
    public void Begin(CadPanelState s)
    {
        state = s;
        kind = s.demo?.kind;
        phaseShown = null;
        lineStage = -1;
        time.Invalidate();
        nums.Invalidate();
        hoverLive.Invalidate();
        for (int i = 0; i < 3; i++) rowLive[i].Invalidate();
        rpmInLive.Invalidate();
        rpmOutLive.Invalidate();
        CadUi.Show(launch, kind == CadDemoPlayer.Launch);
        CadUi.Show(hover, kind == CadDemoPlayer.Hover);
        CadUi.Show(spin, kind == CadDemoPlayer.Spin);
        SetPhase(kind == CadDemoPlayer.Launch ? "Countdown" : kind == CadDemoPlayer.Hover ? "Spin-up" : "Opening");
        if (kind == CadDemoPlayer.Launch) BeginLaunch(s);
        else if (kind == CadDemoPlayer.Hover) BeginHover(s);
        else BeginSpin(s);
        Layout();
    }

    /// <summary>The data changed mid-demo (the flight arrived after Fly was pressed): pick it up.</summary>
    public void Refresh(CadPanelState s)
    {
        state = s;
        if (kind == CadDemoPlayer.Launch && s.flight != shownFlight)
        {
            BeginLaunch(s);
            Layout();
        }
    }

    /// <summary>The panel's width changed: re-place what depends on it.</summary>
    public void Layout()
    {
        float w = Root.rect.width > 1f ? Root.rect.width : 444f;
        if (kind == CadDemoPlayer.Launch)
        {
            bool warn = warnText.gameObject.activeSelf;
            float warnH = warn ? CadUi.Height(warnText, warnText.text, w - 2f * PadX) : 0f;
            plotTop = CardH - PadBottom - PlotH - (warn ? warnH + Gap : 0f);
            CadUi.Band(plotBack.rectTransform, plotTop, PlotH, PadX, PadX);
            CadUi.Place(plotDone.rectTransform, 0f, 0f, w - 2f * PadX, PlotH);
            CadUi.Place(plotClip, PadX, plotTop, flownX, PlotH);
            CadUi.Place(cursor.rectTransform, PadX + cursorX - 4f, plotTop + cursorY - 4f, 8f, 8f);
            if (warn) CadUi.Band(warnText.rectTransform, plotTop + PlotH + Gap, warnH + 1f, PadX, PadX);
            plotBack.Redraw();
            plotDone.Redraw();
        }
        else if (kind == CadDemoPlayer.Hover) LayoutHover(w);
        else if (kind == CadDemoPlayer.Spin) LayoutSpin(w);
    }

    /// <summary>A frame of the running demo (every frame): numbers that changed, the curve, the hands.</summary>
    public void Frame(CadDemoFrame f, float dt)
    {
        if (!string.IsNullOrEmpty(f.phase)) SetPhase(f.phase);
        pulse += dt;
        phaseDot.canvasRenderer.SetAlpha(0.45f + 0.55f * (0.5f + 0.5f * Mathf.Cos(pulse * 2f * Mathf.PI)));
        if (kind == CadDemoPlayer.Launch) FrameLaunch(f);
        else if (kind == CadDemoPlayer.Hover) FrameHover(f);
        else FrameSpin(f, dt);
    }

    void SetPhase(string phase)
    {
        if (phase == phaseShown) return;
        phaseShown = phase;
        phaseText.text = phase;
        float w = CadUi.Width(phaseText, phase.ToUpperInvariant()) + 2f;
        CadUi.Place(phasePill.rectTransform, PadX, PadTop, 10f + 7f + 7f + w + 10f, TopRowH);
        CadUi.Place(phaseDot.rectTransform, 10f, (TopRowH - 7f) * 0.5f, 7f, 7f);
        CadUi.Place(phaseText.rectTransform, 24f, 0f, w, TopRowH);
    }

    // ---- Fly ----

    void BeginLaunch(CadPanelState s)
    {
        shownFlight = s.flight;
        flight.Set(s.flight);
        string warning = s.flight != null && !string.IsNullOrEmpty(s.flight.warning)
            ? "No-wind sim: at " + CadUi.F(s.flight.railExitMarginCal, "0.00") + " cal a gust could make it tumble."
            : null;
        warnText.text = warning ?? "";
        CadUi.Show(warnText, warning != null);
        cursor.gameObject.SetActive(!flight.Empty);
        plotClip.gameObject.SetActive(!flight.Empty);
        // The cursor waits on the pad; nothing is flown yet.
        flownX = 0.01f;
        cursorX = 6f;
        cursorY = flight.Empty ? PlotH - 6f : CadPanelDraw.PlotY(flight, flight.AltitudeAt(0f), PlotH);
    }

    void FrameLaunch(CadDemoFrame f)
    {
        var fl = state?.flight;
        if (f.phase == "Countdown") time.Begin().Add("T−").Fixed(Mathf.Max(0f, f.countdownS), 1).Add(" s").Apply();
        else
        {
            time.Begin().Add("T+").Fixed(f.timeS, 1).Add(" s");
            if (f.playback > 1.01f) time.Add("  ·  ×").Int(Mathf.RoundToInt(f.playback), false);
            time.Apply();
        }
        nums.Begin().Add("<b>").Int(Mathf.RoundToInt(f.altitudeM), false).Add("</b><space=6><size=30%><alpha=#8C>m altitude<alpha=#FF></size><space=20><size=60%><b>")
            .Int(Mathf.RoundToInt(f.speedMps), false).Add("</b></size><space=6><size=30%><alpha=#8C>m/s").Apply();

        // The context line changes with the stage, not every frame.
        int stage = f.phase == "Countdown" ? 0 : f.phase == "Landed" ? 3 : fl != null && f.timeS >= flight.apogeeT ? 2 : 1;
        if (stage != lineStage && fl != null)
        {
            lineStage = stage;
            var values = state.demo?.values;
            switch (stage)
            {
                case 0:
                    float rail = values != null && values.TryGetValue("rail_m", out float r) ? r : 0.9f;
                    float tilt = values != null && values.TryGetValue("inclination_deg", out float inc) ? inc : 85f;
                    lineText.text = (string.IsNullOrEmpty(fl.motor) ? "Motor" : fl.motor) + " · " + CadUi.F(fl.liftoffMassG, "0") +
                                    " g on the pad · " + CadUi.F(rail, "0.0") + " m rod at " + CadUi.F(tilt, "0") + "°";
                    break;
                case 1:
                    lineText.text = "Apogee " + CadUi.F(fl.apogeeM, "0") + " m at " + CadUi.F(flight.apogeeT, "0.0") +
                                    " s · top speed " + CadUi.F(fl.maxSpeedMps, "0.0") + " m/s";
                    break;
                case 2:
                    lineText.text = flight.deploys
                        ? "Under the chute at " + CadUi.F(fl.descentRateMps, "0.0") + " m/s"  // the time chip says how fast it plays
                        : "Coming down without a parachute";
                    break;
                default:
                    lineText.text = "Down after " + CadUi.F(flight.land, "0.0") + " s, " + CadUi.F(fl.driftM, "0") + " m from the pad";
                    break;
            }
        }

        // The flown part of the curve: the clip opens to the cursor.
        if (flight.Empty) return;
        float w = plotBack.rectTransform.rect.width;
        float speedup = Mathf.Max(1f, CadViewer.Active != null ? CadViewer.Active.descentSpeedup : 8f);
        float t = f.phase == "Countdown" ? 0f : f.timeS;
        cursorX = CadPanelDraw.PlotX(flight, t, w, speedup);
        cursorY = CadPanelDraw.PlotY(flight, flight.AltitudeAt(t), PlotH);
        flownX = Mathf.Max(0.01f, cursorX);
        CadUi.Place(plotClip, PadX, plotTop, flownX, PlotH);
        CadUi.Place(cursor.rectTransform, PadX + cursorX - 4f, plotTop + cursorY - 4f, 8f, 8f);
    }

    // The whole curve: a faint area, dashed, with event dots.
    void PaintPlotBack(CadShape s)
    {
        if (flight.Empty) return;
        float w = s.Width, h = s.Height;
        float speedup = Mathf.Max(1f, CadViewer.Active != null ? CadViewer.Active.descentSpeedup : 8f);
        CadPanelDraw.CurvePath(s, flight, w, h, speedup);
        s.FillUnder(CadPanelDraw.PlotY(flight, 0f, h), CadUi.White(0.05f));
        s.Dashed(1.2f, CadUi.White(0.3f), 3f, 4f);
        Event(s, flight.burnout, w, h, speedup);
        Event(s, flight.apogeeT, w, h, speedup);
        if (flight.deploys) Event(s, flight.deploy, w, h, speedup);
        Event(s, flight.land, w, h, speedup);
    }

    void Event(CadShape s, float t, float w, float h, float speedup) =>
        s.Disc(CadPanelDraw.PlotX(flight, t, w, speedup), CadPanelDraw.PlotY(flight, flight.AltitudeAt(t), h), 2.2f, CadUi.White(0.6f));

    // The flown part, drawn whole and revealed by its clip: an orange glow under a warm white line.
    void PaintPlotDone(CadShape s)
    {
        if (flight.Empty) return;
        float w = s.Width, h = s.Height;
        float speedup = Mathf.Max(1f, CadViewer.Active != null ? CadViewer.Active.descentSpeedup : 8f);
        CadPanelDraw.CurvePath(s, flight, w, h, speedup);
        s.Stroke(5f, CadUi.HueA(0.25f));
        s.Stroke(1.8f, CadUi.Ember);
    }

    // ---- Hover ----

    void BeginHover(CadPanelState s)
    {
        var v = s.demo?.values;
        float Get(string key, float fallback) => v != null && v.TryGetValue(key, out float x) ? x : fallback;
        hoverThrottle = Mathf.Clamp01(Get("hover_throttle_pct", 30f) / 100f);
        hoverRpm = Get("hover_rpm", 0f);
        spare = Get("thrust_margin_pct", 0f);
        power = Get("hover_power_w", 0f);
        float weight = Get("weight_g", s.a != null ? s.a.massG : 0f), perMotor = Get("max_thrust_g", 0f);
        int motors = s.demo != null && s.demo.spinParts != null && s.demo.spinParts.Count > 0 ? s.demo.spinParts.Count : 4;
        float total = perMotor * motors;
        budgetShare = total > 0f ? Mathf.Clamp01(weight / total) : 0f;
        budgetLine.text = CadUi.F(weight, "0") + " g to lift, " + CadUi.F(total / 1000f, "0.0") + " kg of thrust · each motor lifts " +
                          CadUi.F(weight / Mathf.Max(1, motors), "0") + " g of its " + CadUi.F(perMotor / 1000f, "0.00") + " kg";
        throttle = 0f;
        rowLive[1].Begin().Int(Mathf.RoundToInt(spare), false).Add(" %").Apply();
        rowLive[2].Begin().Int(Mathf.RoundToInt(power), false).Add(" W").Apply();
        meter.Redraw();
    }

    void LayoutHover(float w)
    {
        float top = PadTop + TopRowH + Gap, bottom = CardH - PadBottom;
        CadUi.Place(meter.rectTransform, PadX, top, MeterW, bottom - top);
        float x = PadX + MeterW + 14f, mainW = w - x - PadX;
        CadUi.Band(hoverNums.rectTransform, top + (1f - CadUi.InterLine) * CadUi.Digits * 0.5f, 52f, x, PadX);
        float rowsY = top + CadUi.Digits + Gap, cellW = (mainW - 12f) / 3f;
        for (int i = 0; i < 3; i++) CadUi.Place((RectTransform)rowLabels[i].transform.parent, x + i * (cellW + 6f), rowsY, cellW, 43f);
        float lineH = CadUi.Height(budgetLine, budgetLine.text, mainW);
        CadUi.Band(budgetLine.rectTransform, bottom - lineH, lineH + 1f, x, PadX);
        CadUi.Band(budgetTrack, bottom - lineH - Gap - 8f, 8f, x, PadX);
        var fill = budgetFill.rectTransform;
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(Mathf.Max(0.015f, budgetShare), 1f);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
    }

    void FrameHover(CadDemoFrame f)
    {
        // Throttle as the demo flies it (the boards' story): spooling up to 60% of hover, a push to lift, steady at
        // hover, a little under it to land.
        float spinUp = Mathf.Clamp01(f.progress);
        float hoverHeight = CadViewer.Active != null ? Mathf.Max(0.01f, CadViewer.Active.hoverHeight) : 0.22f;
        float up = Mathf.Clamp01(f.altitudeM / hoverHeight);
        float target = f.phase == "Take-off" ? hoverThrottle + 0.18f * (1f - up)
            : f.phase == "Landing" ? hoverThrottle - 0.06f
            : f.phase == "Hover" ? hoverThrottle
            : hoverThrottle * spinUp * 0.6f;
        target = Mathf.Clamp01(target);
        if (Mathf.Abs(target - throttle) > 0.002f)
        {
            throttle = target;
            meter.Redraw();
        }
        heightLive.Begin().Int(Mathf.RoundToInt(f.altitudeM * 100f), false).Add(" cm up").Apply();
        hoverLive.Begin().Add("<b>").Int(Mathf.RoundToInt(throttle * 100f), false).Add("</b><space=6><size=30%><alpha=#8C>% throttle").Apply();
        float rpm = hoverThrottle > 0f ? hoverRpm * Mathf.Sqrt(Mathf.Max(0f, throttle) / hoverThrottle) : 0f;
        rowLive[0].Begin().Int(Mathf.RoundToInt(rpm / 5f) * 5).Add(" rpm").Apply();
    }

    // The throttle meter: a 22 mm column, filled orange to the throttle, the hover line marked across it.
    void PaintMeter(CadShape s)
    {
        float w = s.Width, h = s.Height;
        s.Begin().RoundRect(0f, 0f, w, h, 11f).Fill(CadUi.White(0.08f));
        float fh = h * throttle;
        if (fh > 1f)
        {
            float r = Mathf.Min(11f, fh * 0.5f);
            s.Begin().RoundRect(0f, h - fh, w, fh, r).Fill(CadUi.Hue);
            // Lighter toward the top of the fill (the boards' orange to #FFD2B3).
            s.GradientV(2f, h - fh + r * 0.5f, w - 4f, Mathf.Max(0f, fh - r), CadUi.Alpha(CadUi.PhaseText, 0.85f), CadUi.HueA(0f));
        }
        float y = h - h * hoverThrottle;
        s.FillRect(-2f, y - 1f, w + 4f, 2f, CadUi.White(0.85f));
    }

    // ---- Run ----

    void BeginSpin(CadPanelState s)
    {
        var v = s.demo?.values;
        float Get(string key, float fallback) => v != null && v.TryGetValue(key, out float x) ? x : fallback;
        ratio = Mathf.Max(0.01f, Get("ratio", 3f));
        inRpm = Get("input_rpm", 3000f);
        outRpm = Get("output_rpm", inRpm / ratio);
        float inNm = Get("input_torque_nm", 0.5f), outNm = Get("output_torque_nm", inNm * ratio);
        float efficiency = Get("efficiency", 0.98f);
        slowdown = CadViewer.Active != null ? Mathf.Max(1f, CadViewer.Active.spinSlowdown) : 100f;
        time.Begin().Add("Slow motion  ·  ").Int(Mathf.RoundToInt(slowdown), false).Add("× slower").Apply();
        torqueIn.text = "Input · " + CadUi.F(inNm, "0.00") + " N·m";
        torqueOut.text = "Output · " + CadUi.F(outNm, "0.00") + " N·m";
        ratioText.text = CadUi.F(ratio, "0.00") + " : 1";
        noteText.text = "Output turns the other way, " + CadUi.F(ratio, ratio >= 10f ? "0" : "0.#") + "× slower and " +
                        CadUi.F(outNm / Mathf.Max(inNm, 1e-4f), "0.00") + "× the torque (" + CadUi.F(efficiency * 100f, "0") + "% mesh).";
        angleIn = 0f;
        rpmInLive.Begin().Add("0<size=55%><alpha=#8C> rpm").Apply();
        rpmOutLive.Begin().Add("0<size=55%><alpha=#8C> rpm").Apply();
    }

    void LayoutSpin(float w)
    {
        float noteH = CadUi.Height(noteText, noteText.text, w - 2f * PadX);
        float bottom = CardH - PadBottom;
        CadUi.Band(noteText.rectTransform, bottom - noteH, noteH + 1f, PadX, PadX);
        float top = PadTop + TopRowH + Gap, room = bottom - noteH - Gap - top;
        float chipW = CadUi.Width(ratioText, ratioText.text) + 18f;
        float colW = (w - 2f * PadX - chipW - 16f) * 0.5f;
        float blockOut = DialOut + 8f + 24f + 16f, y0 = top + Mathf.Max(0f, (room - blockOut) * 0.5f);
        float cxIn = PadX + colW * 0.5f, cxOut = w - PadX - colW * 0.5f;
        CadUi.Place(dialIn.rectTransform, cxIn - DialIn * 0.5f, y0 + (DialOut - DialIn) * 0.5f, DialIn, DialIn);
        CadUi.Place(dialOut.rectTransform, cxOut - DialOut * 0.5f, y0, DialOut, DialOut);
        float textY = y0 + DialOut + 8f;
        CadUi.Place(rpmIn.rectTransform, cxIn - colW * 0.5f, textY, colW, 24f);
        CadUi.Place(rpmOut.rectTransform, cxOut - colW * 0.5f, textY, colW, 24f);
        CadUi.Place(torqueIn.rectTransform, cxIn - colW * 0.5f, textY + 24f, colW, 15f);
        CadUi.Place(torqueOut.rectTransform, cxOut - colW * 0.5f, textY + 24f, colW, 15f);
        CadUi.Place(ratioChip, w * 0.5f - chipW * 0.5f, y0 + DialOut * 0.5f - 12f, chipW, 24f);
    }

    void FrameSpin(CadDemoFrame f, float dt)
    {
        float k = Mathf.Clamp01(f.progress);
        // Degrees per second as shown: rpm x 6 deg / s, slowed like the model.
        angleIn += inRpm * 6f * k / slowdown * dt;
        angleIn %= 360f;
        handIn.localRotation = Quaternion.Euler(0f, 0f, -angleIn);
        handOut.localRotation = Quaternion.Euler(0f, 0f, angleIn / ratio);
        rpmInLive.Begin().Int(Mathf.RoundToInt(inRpm * k / 10f) * 10).Add("<size=55%><alpha=#8C> rpm").Apply();
        rpmOutLive.Begin().Int(Mathf.RoundToInt(outRpm * k / 10f) * 10).Add("<size=55%><alpha=#8C> rpm").Apply();
    }

    // A dial: a white rim with a soft glow and twelve ticks.
    static void PaintSpinDial(CadShape s)
    {
        float size = Mathf.Min(s.Width, s.Height), c = size * 0.5f;
        s.Ring(c, c, c - 3f, 4f, CadUi.White(0.08f));
        s.Ring(c, c, c - 0.8f, 1.6f, CadUi.White(0.8f));
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.PI / 6f, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            s.Line(c + (c - 6f) * ca, c - (c - 6f) * sa, c + (c - 11f) * ca, c - (c - 11f) * sa, 1.2f, CadUi.White(0.55f));
        }
    }
}
