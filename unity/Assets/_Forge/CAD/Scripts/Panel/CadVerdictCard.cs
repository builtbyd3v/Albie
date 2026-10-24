using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The verdict, always on top (CAD UX spec 2.2 and 2.3): on Overview the 218 mm card (the question, the headline with
/// its status dot, one plain sentence, the dial for the verdict's metric, and the to-scale instrument), on the other
/// tabs the 56 mm strip (dot, headline, detail, band bar; tap it for Overview). While a build runs, both carry a line of
/// light along their top edge, and the card's content dims to 42% under "Rebuilding with ...".
/// </summary>
public sealed class CadVerdictCard
{
    // Board numbers (mm). Card: padding 12 12 10 16, the instrument column 156 wide with 8 before it.
    const float CardH = 218f, Corner = 20f, PadLeft = 16f, PadRight = 12f, Column = 156f, ColumnGap = 8f;
    const float QuestionY = 12f, HeadlineY = 31f, HeadlineH = 31f, DotSize = 11f, SentenceY = 67f;
    const float DialX = 10f, DialY = 122f, StripH = 56f, StripCorner = 16f, BarW = 118f;

    public readonly RectTransform Card;
    public readonly CadButton Strip;

    readonly CadInstrument instrument = new CadInstrument();
    readonly List<TMP_Text> ticks = new List<TMP_Text>();
    readonly List<TMP_Text> instrumentLabels = new List<TMP_Text>();
    readonly List<float> tickValues = new List<float>();
    readonly ForgeSurface dot, stripDot;
    readonly CadShape wash, dial, instrumentShape, cardScan, stripBar, stripScan;
    readonly TextMeshProUGUI question, headline, sentence, busyline, value, unit, stripHeadline, stripDetail;
    readonly RectTransform instrumentRoot;
    readonly CanvasGroup dim;

    CadMetric metric;
    string status = "ok";
    bool busy, hasInstrument;
    float scanT, dimAlpha = 1f;
    int builtVersion = -1;
    CadParams builtParams;

    public CadVerdictCard(RectTransform parent, System.Action openOverview)
    {
        // ---- The card ----
        Card = CadUi.Rect(parent, "Verdict");
        var fill = CadUi.Surface(Card, "Fill", CadUi.White(0.045f), Corner, 0.07f);
        CadUi.Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);
        wash = CadUi.Shape(Card, "Wash", s =>
            s.Wash(s.Width, s.Height, Corner, 0.12f * s.Width, 0f, 0.88f * s.Width * 0.877f, s.Height * 0.877f,
                CadUi.Status(status), status == "bad" ? 0.18f : 0.16f));
        CadUi.Stretch(wash.rectTransform, 0f, 0f, 0f, 0f);
        question = CadUi.OverlineText(Card, "Question");

        var dimRoot = CadUi.Rect(Card, "Content");
        CadUi.Stretch(dimRoot, 0f, 0f, 0f, 0f);
        dim = CadUi.Group(dimRoot.gameObject);
        dim.blocksRaycasts = false;
        dot = CadUi.Pill(dimRoot, "Dot", CadUi.Status("ok"), glow: 0.5f);
        headline = CadUi.Text(dimRoot, "Headline", CadUi.Headline, CadUi.White(0.96f), bold: true);
        headline.characterSpacing = -1.5f;
        sentence = CadUi.Text(dimRoot, "Sentence", 13f, CadUi.White(0.7f), wrap: true);
        sentence.alignment = TextAlignmentOptions.TopLeft;
        sentence.overflowMode = TextOverflowModes.Ellipsis;
        CadUi.LineHeight(sentence, 1.42f);

        dial = CadUi.Shape(dimRoot, "Dial", s => CadPanelDraw.Dial(s, metric));
        CadUi.Place(dial.rectTransform, DialX, DialY, CadPanelDraw.DialW, CadPanelDraw.DialH);
        value = CadUi.Text(dial.transform, "Value", 22f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        value.characterSpacing = -1.5f;
        CadUi.Place(value.rectTransform, 0f, 42f, CadPanelDraw.DialW, 27f);
        unit = CadUi.Text(dial.transform, "Unit", CadUi.Tick, CadUi.White(0.5f), align: TextAlignmentOptions.Center);
        CadUi.Place(unit.rectTransform, 0f, 68f, CadPanelDraw.DialW, 13f);

        var instrumentFill = CadUi.Surface(dimRoot, "Instrument", new Color(0f, 0f, 0f, 0.14f), 12f);
        instrumentRoot = instrumentFill.rectTransform;
        CadUi.PlaceRight(instrumentRoot, PadRight, QuestionY, Column, CadInstrument.H);
        instrumentShape = CadUi.Shape(instrumentRoot, "Drawing", s => instrument.Paint(s));
        CadUi.Stretch(instrumentShape.rectTransform, 0f, 0f, 0f, 0f);

        busyline = CadUi.Text(Card, "Busy", CadUi.Body, CadUi.BusyText);
        cardScan = CadUi.Shape(Card, "Scan", s => Scan(s, Corner));
        CadUi.Stretch(cardScan.rectTransform, 0f, 0f, 0f, 0f);

        // ---- The strip: the same verdict in one line; a press opens Overview ----
        Strip = CadButton.Create(parent, "VerdictStrip", CadButton.Card, StripCorner, () => openOverview?.Invoke());
        stripDot = CadUi.Pill(Strip.transform, "Dot", CadUi.Status("ok"), glow: 0.5f);
        CadUi.Place(stripDot.rectTransform, 14f, (StripH - DotSize) * 0.5f, DotSize, DotSize);
        stripHeadline = CadUi.Text(Strip.transform, "Headline", 15f, CadUi.White(0.96f), bold: true);
        stripDetail = CadUi.Text(Strip.transform, "Detail", CadUi.Body, CadUi.White(0.6f));
        stripBar = CadUi.Shape(Strip.transform, "Bar", s => CadPanelDraw.BandBar(s, metric, 0f, 7f, BarW, 6f, 3f, 16f, 5f, 2f));
        CadUi.PlaceRight(stripBar.rectTransform, 14f, (StripH - 20f) * 0.5f, BarW, 20f);
        stripScan = CadUi.Shape(Strip.transform, "Scan", s => Scan(s, StripCorner));
        CadUi.Stretch(stripScan.rectTransform, 0f, 0f, 0f, 0f);
    }

    public void Show(CadPanelState s)
    {
        var v = s.verdict;
        string newStatus = v?.status ?? s.primary?.status ?? "ok";
        bool restyled = newStatus != status || metric != s.primary;
        status = newStatus;
        metric = s.primary;
        question.text = s.question;
        headline.text = CadUi.Clean(v?.headline ?? "");
        sentence.text = CadUi.Clean(v?.sentence ?? "");
        dot.color = CadUi.Status(status);
        stripDot.color = dot.color;
        if (restyled)
        {
            wash.Redraw();
            dial.Redraw();
            stripBar.Redraw();
        }

        // Dial: the value in the status colour, its unit line, tick values at the band edges.
        bool gauge = metric != null && metric.IsGauge;
        CadUi.SplitDisplay(metric, out string number, out _);
        value.text = number;
        value.color = CadUi.StatusText(metric?.status ?? status);
        unit.text = CadPanelState.UnitLine(metric);
        CadPanelState.Ticks(gauge ? metric : null, tickValues, thin: true);
        while (ticks.Count < tickValues.Count)
            ticks.Add(CadUi.Text(dial.transform, "Tick", CadUi.Tick, CadUi.White(0.45f), align: TextAlignmentOptions.Center));
        for (int i = 0; i < ticks.Count; i++)
        {
            bool used = i < tickValues.Count;
            CadUi.Show(ticks[i], used);
            if (!used) continue;
            ticks[i].text = CadPanelState.TickText(tickValues[i]);
            Vector2 at = CadPanelDraw.DialTick(metric, tickValues[i]);
            CadUi.Place(ticks[i].rectTransform, at.x - 20f, at.y - 6f, 40f, 12f);
        }

        // Instrument: worked out once per version.
        if (s.version != builtVersion || s.p != builtParams || restyled)
        {
            builtVersion = s.version;
            builtParams = s.p;
            hasInstrument = instrument.Build(s.p, s.a, status);
            instrumentShape.Redraw();
            ShowInstrumentLabels();
        }
        CadUi.Show(instrumentRoot, hasInstrument);

        // Strip: "1.60 cal static margin".
        stripHeadline.text = headline.text;
        stripDetail.text = metric != null ? CadUi.Clean(metric.display) + " " + (metric.label ?? "").ToLowerInvariant() : "";

        bool wasBusy = busy;
        busy = s.building;
        busyline.text = busy ? "Rebuilding with " + CadUi.Clean(s.buildingWhat) + "…" : "";
        CadUi.Show(busyline, busy);
        sentence.maxVisibleLines = busy ? 2 : 3;
        if (busy && !wasBusy) scanT = 0f;
        if (!busy)
        {
            cardScan.Redraw();
            stripScan.Redraw();
        }
        Layout();
    }

    /// <summary>Place the card's text for its width (the panel may have been resized).</summary>
    public void Layout()
    {
        float right = hasInstrument ? PadRight + Column + ColumnGap : PadRight;
        float width = Mathf.Max(60f, (Card.rect.width > 1f ? Card.rect.width : 444f) - PadLeft - right);
        CadUi.Band(question.rectTransform, QuestionY, 14f, PadLeft, right);
        CadUi.Place(dot.rectTransform, PadLeft, HeadlineY + (HeadlineH - DotSize) * 0.5f, DotSize, DotSize);
        CadUi.Band(headline.rectTransform, HeadlineY, HeadlineH, PadLeft + DotSize + 9f, right);
        float lines = busy ? 2f : 3f;
        float sentenceH = Mathf.Min(CadUi.Height(sentence, sentence.text, width), 13f * 1.42f * lines + 1f);
        float top = SentenceY + (1.42f - CadUi.InterLine) * 13f * 0.5f;
        CadUi.Band(sentence.rectTransform, top, sentenceH + 2f, PadLeft, right);
        CadUi.Band(busyline.rectTransform, top + sentenceH + 6f, 16f, PadLeft, right);

        float stripWidth = Strip.Rect.rect.width > 1f ? Strip.Rect.rect.width : 444f;
        float headlineW = CadUi.Width(stripHeadline, stripHeadline.text) + 2f;
        headlineW = Mathf.Min(headlineW, stripWidth - 35f - 14f - BarW - 20f);
        CadUi.Place(stripHeadline.rectTransform, 35f, 0f, headlineW, StripH);
        CadUi.Band(stripDetail.rectTransform, 0f, StripH, 35f + headlineW + 10f, 14f + BarW + 10f);
    }

    /// <summary>The line of light while building; the content dims to 42% (0.2 s).</summary>
    public void Tick(float dt)
    {
        float target = busy ? 0.42f : 1f;
        if (!Mathf.Approximately(dimAlpha, target))
        {
            dimAlpha = Mathf.MoveTowards(dimAlpha, target, dt / 0.2f * 0.58f);
            dim.alpha = dimAlpha;
        }
        if (!busy) return;
        scanT += dt;
        if (Card.gameObject.activeInHierarchy) cardScan.Redraw();
        if (Strip.gameObject.activeInHierarchy) stripScan.Redraw();
    }

    // 2 mm along the top edge, a third of the width, crossing in 1.1 s (the boards' pn-scan).
    void Scan(CadShape s, float corner)
    {
        if (!busy) return;
        float w = s.Width, length = 0.36f * w, u = scanT % 1.1f / 1.1f;
        s.Scan(-length + u * 4f * length, length, 0f, 2f, corner, w - corner, 1f);
    }

    void ShowInstrumentLabels()
    {
        var list = instrument.labels;
        while (instrumentLabels.Count < list.Count)
            instrumentLabels.Add(CadUi.Text(instrumentRoot, "Label", CadUi.Overline, Color.white, bold: true));
        for (int i = 0; i < instrumentLabels.Count; i++)
        {
            var t = instrumentLabels[i];
            bool used = hasInstrument && i < list.Count;
            CadUi.Show(t, used);
            if (!used) continue;
            var l = list[i];
            t.text = l.text;
            bool muted = l.style == CadInstrument.LabelStyle.Muted;
            t.fontSize = muted ? CadUi.Unit : CadUi.Overline;
            t.fontStyle = muted ? FontStyles.Normal : FontStyles.Bold;
            t.color = LabelColor(l.style);
            switch (l.side)
            {
                case CadInstrument.Side.Left:
                    t.alignment = TextAlignmentOptions.TopRight;
                    CadUi.Place(t.rectTransform, l.x - 100f, l.y, 100f, 14f);
                    break;
                case CadInstrument.Side.Center:
                    t.alignment = TextAlignmentOptions.Top;
                    CadUi.Place(t.rectTransform, l.x - 60f, l.y, 120f, 14f);
                    break;
                default:
                    t.alignment = TextAlignmentOptions.TopLeft;
                    CadUi.Place(t.rectTransform, l.x, l.y, 100f, 14f);
                    break;
            }
        }
    }

    static Color LabelColor(CadInstrument.LabelStyle style)
    {
        switch (style)
        {
            case CadInstrument.LabelStyle.Muted: return CadUi.White(0.55f);
            case CadInstrument.LabelStyle.Cg: return CadUi.CgText;
            case CadInstrument.LabelStyle.Cp: return CadUi.CpText;
            case CadInstrument.LabelStyle.Ok: return CadUi.OkText;
            case CadInstrument.LabelStyle.Warn: return CadUi.WarnText;
            case CadInstrument.LabelStyle.Bad: return CadUi.BadText;
            default: return CadUi.White(0.9f);
        }
    }
}
