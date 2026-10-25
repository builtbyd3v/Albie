using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Overview (CAD UX spec 2.5): four tiles (rocket: Apogee, Top speed, To apogee, Off the rail from /cad/flight; other
/// designs: their first four metrics after the verdict's), the demo's note, and the Last change card (the request in
/// quotes, "v1 → v2 · by Bit · 4 min ago", the parameter diffs, and effect chips in status colours; "Opened with the
/// defaults" for a first version).
/// </summary>
public sealed class CadOverviewTab : CadPanelTab
{
    const float TileH = 50f, Gap = 8f, TileGap = 6f, CardPadX = 13f, CardPadTop = 10f, CardPadBottom = 11f, ChipH = 22f;

    readonly RectTransform[] tiles = new RectTransform[4];
    readonly TextMeshProUGUI[] tileValues = new TextMeshProUGUI[4], tileLabels = new TextMeshProUGUI[4];
    static readonly float[] Equal = { 1f, 1f, 1f, 1f };
    readonly TextMeshProUGUI note;
    readonly RectTransform card;
    readonly TextMeshProUGUI cardHeading, meta, cardTitle, diffLine, hint;
    readonly List<CadButton> chips = new List<CadButton>();
    readonly List<RectTransform> chipRects = new List<RectTransform>();
    int chipCount;

    public CadOverviewTab(RectTransform parent, CadParamsPanel owner) : base(parent, "Overview", owner)
    {
        for (int i = 0; i < 4; i++)
        {
            var tile = CadUi.Surface(Root, "Tile" + i, CadUi.White(0.05f), 14f, 0.05f);
            tiles[i] = tile.rectTransform;
            tileValues[i] = CadUi.Text(tile.transform, "Value", CadUi.TileValue, Color.white, bold: true);
            CadUi.Band(tileValues[i].rectTransform, 7f, 21f, 10f, 6f);
            tileLabels[i] = CadUi.Text(tile.transform, "Label", CadUi.Overline, CadUi.White(0.55f));
            CadUi.Band(tileLabels[i].rectTransform, 30f, 14f, 10f, 6f);
        }
        note = CadUi.Text(Root, "Note", CadUi.Overline, CadUi.White(0.48f), wrap: true);
        note.alignment = TextAlignmentOptions.TopLeft;
        CadUi.LineHeight(note, 1.4f);

        var cardFill = CadUi.Surface(Root, "LastChange", CadUi.White(0.04f), 16f, 0.06f);
        card = cardFill.rectTransform;
        cardHeading = CadUi.OverlineText(card, "Heading");
        cardHeading.text = "Last change";
        meta = CadUi.Text(card, "Meta", CadUi.Small, CadUi.White(0.5f), align: TextAlignmentOptions.Right);
        cardTitle = CadUi.Text(card, "Title", CadUi.Value, Color.white, bold: true);
        diffLine = CadUi.Text(card, "Diffs", 12f, CadUi.White(0.62f));
        hint = CadUi.Text(card, "Hint", CadUi.Body, CadUi.White(0.55f), wrap: true);
        hint.alignment = TextAlignmentOptions.TopLeft;
        hint.text = "Drag a parameter, pick a preset, or ask Albie to change it.";
    }

    public override void Show(CadPanelState s)
    {
        ShowTiles(s);
        ShowLastChange(s);
    }

    void ShowTiles(CadPanelState s)
    {
        bool rocket = s.demo?.kind == CadDemoPlayer.Launch || (s.p != null && s.p.Has("flight"));
        if (rocket)
        {
            var f = s.flight;
            Tile(0, f != null ? CadUi.F(f.apogeeM, "0") : "–", "m", "Apogee");
            Tile(1, f != null ? CadUi.F(f.maxSpeedMps, "0.0") : "–", "m/s", "Top speed");
            Tile(2, f != null ? CadUi.F(f.timeToApogeeS, "0.0") : "–", "s", "To apogee");
            Tile(3, f != null && f.railExitSpeedMps > 0f ? CadUi.F(f.railExitSpeedMps, "0.0") : "–", "m/s", "Off the rail");
            string liftoff = f != null && f.liftoffMassG > 0f ? " " + CadUi.F(f.liftoffMassG, "0") + " g at liftoff." : "";
            note.text = CadUi.Clean(s.demo?.note ?? "") + liftoff;
        }
        else
        {
            int k = 0;
            if (s.a?.metrics != null)
                foreach (var m in s.a.metrics)
                {
                    if (k == 4) break;
                    if (m == s.primary || m.id == "size" || m.id == "parts") continue;
                    CadUi.SplitDisplay(m, out string v, out string u);
                    Tile(k++, v, u, TileLabel(m));
                }
            for (; k < 4; k++) Tile(k, "", "", "");
            bool blocked = !string.IsNullOrEmpty(s.demo?.blocked);
            note.text = blocked && s.demo.kind == CadDemoPlayer.Hover ? "The props overlap, so it can’t fly; the numbers assume they clear."
                : CadUi.Clean(s.demo?.note ?? "");
        }
    }

    static string TileLabel(CadMetric m)
    {
        switch (m.label)
        {
            case "Thrust to weight": return "Thrust / weight";
            case "Thrust per motor": return "Thrust / motor";
            default: return CadUi.Clean(m.label);
        }
    }

    void Tile(int i, string value, string unit, string label)
    {
        tileValues[i].text = string.IsNullOrEmpty(unit) ? value : value + "<space=3><size=67%><alpha=#8C>" + unit;
        tileLabels[i].text = label;
        CadUi.Show(tiles[i], !string.IsNullOrEmpty(label));
    }

    void ShowLastChange(CadPanelState s)
    {
        string ago = CadUi.Ago(s.createdAt);
        string source = CadUi.Source(s.source);
        string tail = (string.IsNullOrEmpty(source) ? "" : " · " + source) + (ago != null ? " · " + ago : "");
        bool hasParent = s.parent.HasValue;
        CadUi.Show(diffLine, hasParent);
        CadUi.Show(hint, !hasParent);
        chipCount = 0;
        if (!hasParent)
        {
            cardTitle.text = "Opened with the defaults";
            meta.text = "v" + s.version + tail;
        }
        else
        {
            string label = string.IsNullOrEmpty(s.label) ? "Edited" : CadUi.Clean(s.label);
            cardTitle.text = "“" + label + "”";
            meta.text = "v" + s.parent.Value + " → v" + s.version + tail;
            diffLine.text = Diffs(s.p, s.parentParams, s.p?.parameters, 2, "  ·  ");
            Effects(s);
        }
        for (int i = 0; i < chips.Count; i++) CadUi.Show(chips[i], i < chipCount);
    }

    /// <summary>"Fin root chord 70 → 42 mm  ·  Fin tip chord 35 → 21 mm  ·  +2": what changed from the parent.</summary>
    public static string Diffs(CadParams schema, Dictionary<string, object> before, List<CadParam> afterRows, int show, string separator,
        Dictionary<string, object> after = null)
    {
        if (schema == null || before == null) return "";
        var parts = new List<string>();
        int more = 0;
        foreach (var q in schema.parameters)
        {
            object now = after != null ? (after.TryGetValue(q.name, out var a) ? a : null) : q.value;
            if (!before.TryGetValue(q.name, out var was) || now == null || Same(was, now)) continue;
            if (parts.Count >= show) { more++; continue; }
            string unit = string.IsNullOrEmpty(q.unit) ? "" : " " + q.unit;
            parts.Add(CadUi.Clean(q.label ?? q.name) + " " + CadUi.ParamValue(q, was) + " → " + CadUi.ParamValue(q, now) + unit);
        }
        if (parts.Count == 0) return "";
        string line = string.Join(separator, parts);
        return more > 0 ? line + separator + "+" + more : line;
    }

    public static bool Same(object a, object b)
    {
        if (a == null || b == null) return a == b;
        if (a is string || b is string) return a.ToString() == b.ToString();
        try { return System.Math.Abs(System.Convert.ToDouble(a) - System.Convert.ToDouble(b)) < 1e-9; }
        catch (System.Exception) { return a.ToString() == b.ToString(); }
    }

    // Effect chips: the verdict's metric in its status colour, then the design's own (apogee, hover time, torque), then mass.
    void Effects(CadPanelState s)
    {
        var pm = s.primary;
        if (pm != null)
        {
            string was = null;
            var before = s.parentAnalysis?.metrics?.Find(m => m.id == pm.id);
            if (before != null) was = before.display;
            else
            {
                foreach (var v in s.versions)
                    if (v.version == s.parent && v.primary != null && v.primary.id == pm.id) was = v.primary.display;
            }
            if (was != null) Chip(CadUi.Clean(pm.label) + "  " + CadUi.Clean(was) + " → " + CadUi.Clean(pm.display), pm.status);
        }
        if (s.flight != null && s.parentFlight != null)
            Chip("Apogee  " + CadUi.F(s.parentFlight.apogeeM, "0") + " → " + CadUi.F(s.flight.apogeeM, "0") + " m", null);
        else
        {
            string id = s.demo?.kind == CadDemoPlayer.Hover ? "hover" : s.demo?.kind == CadDemoPlayer.Spin ? "torque" : null;
            if (id != null) MetricChip(s, id, id == "hover" ? "Hover" : "Torque");
        }
        MetricChip(s, "mass", "Mass");
    }

    void MetricChip(CadPanelState s, string id, string name)
    {
        if (chipCount >= 3) return;
        var now = s.Metric(id);
        var was = s.parentAnalysis?.metrics?.Find(m => m.id == id);
        if (now == null || was == null) return;
        Chip(name + "  " + CadUi.Clean(was.display) + " → " + CadUi.Clean(now.display), null);
    }

    void Chip(string text, string status)
    {
        if (chipCount >= 3) return;
        if (chipCount == chips.Count)
        {
            var c = CadButton.Create(card, "Effect", CadButton.Pill, 8f, null);
            c.Button.enabled = false;  // a label, not a control
            c.Surface.raycastTarget = false;
            c.AddLabel("", 12f);
            chips.Add(c);
            chipRects.Add(c.Rect);
        }
        var chip = chips[chipCount++];
        var look = CadButton.Pill;
        if (status == "ok" || status == "warn" || status == "bad")
        {
            look.rest = look.hover = CadUi.Alpha(CadUi.Status(status), status == "bad" ? 0.12f : 0.1f);
            look.ink = CadUi.StatusText(status);
            look.outline = 0f;
        }
        else
        {
            look.rest = look.hover = CadUi.White(0.04f);
            look.ink = CadUi.White(0.7f);
            look.outline = 0.07f;
        }
        chip.SetLook(look);
        chip.SetText(text);
        chip.Rect.sizeDelta = new Vector2(CadUi.Width(chip.Label, text) + 16f, ChipH);
    }

    public override float Layout(float width)
    {
        CadUi.Share(tiles, Equal, TileGap, 0f, TileH);
        float y = TileH + Gap;
        float noteH = CadUi.Height(note, note.text, width);
        CadUi.Band(note.rectTransform, y, noteH + 1f);
        y += noteH > 0f ? noteH + Gap : 0f;

        float inner = width - 2f * CardPadX;
        float cy = CardPadTop;
        CadUi.Band(cardHeading.rectTransform, cy, 15f, CardPadX, CardPadX + 160f);
        CadUi.Band(meta.rectTransform, cy, 15f, CardPadX + 90f, CardPadX);
        cy += 15f + 6f;
        CadUi.Band(cardTitle.rectTransform, cy, 18f, CardPadX, CardPadX);
        cy += 18f + 6f;
        if (diffLine.gameObject.activeSelf)
        {
            if (!string.IsNullOrEmpty(diffLine.text))
            {
                CadUi.Band(diffLine.rectTransform, cy, 15f, CardPadX, CardPadX);
                cy += 15f + 6f;
            }
            if (chipCount > 0)
            {
                float used = Flow(inner, cy);
                cy += used + 4f;
            }
        }
        else
        {
            float hh = CadUi.Height(hint, hint.text, inner);
            CadUi.Band(hint.rectTransform, cy, hh + 1f, CardPadX, CardPadX);
            cy += hh + 2f;
        }
        float cardH = cy - 6f + CardPadBottom;
        CadUi.Band(card, y, cardH);
        return y + cardH;
    }

    float Flow(float inner, float top)
    {
        // Flow the chips inside the card's padding.
        float x = CardPadX, y = top, row = 0f;
        for (int i = 0; i < chipCount; i++)
        {
            var rt = chipRects[i];
            Vector2 size = rt.sizeDelta;
            if (x > CardPadX && x + size.x > CardPadX + inner + 0.01f)
            {
                x = CardPadX;
                y += row + 5f;
                row = 0f;
            }
            CadUi.Place(rt, x, y, size.x, size.y);
            x += size.x + 5f;
            row = Mathf.Max(row, size.y);
        }
        return y + row - top;
    }
}
