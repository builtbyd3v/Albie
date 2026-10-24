using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// History (CAD UX spec 2.5): a sparkline of the verdict's metric across versions over its colour bands (the current
/// one ringed), then the versions newest first, 56 mm each: version chip (white when current), the request or label,
/// "diffs · source · time ago", and the metric with its status dot ("CURRENT" under it). Tapping a version goes back to
/// it (CadSession.GoToVersion: the stored version in mock mode; the hub rebuilds its values as a new version).
/// </summary>
public sealed class CadHistoryTab : CadPanelTab
{
    const float SparkTop = 31f, SparkH = 70f, LabelsH = 30f, CardH = 141f, RowH = 56f, Gap = 8f;
    const int MaxSpark = 8;  // versions on the sparkline (the newest)

    sealed class Row
    {
        public CadButton button;
        public ForgeSurface chip, dot;
        public TextMeshProUGUI chipText, title, meta, metric, current;
        public int version;
    }

    readonly RectTransform card;
    readonly TextMeshProUGUI sparkTitle;
    readonly CadShape spark;
    readonly List<TextMeshProUGUI> sparkLabels = new List<TextMeshProUGUI>();
    readonly List<Row> rows = new List<Row>();
    readonly List<CadVersion> chrono = new List<CadVersion>();  // the sparkline's versions, oldest first
    readonly TextMeshProUGUI empty;
    CadMetric bands;
    int current, shownRows;

    public CadHistoryTab(RectTransform parent, CadParamsPanel owner) : base(parent, "History", owner)
    {
        var fill = CadUi.Surface(Root, "Spark", CadUi.White(0.04f), 14f, 0.06f);
        card = fill.rectTransform;
        sparkTitle = CadUi.Text(card, "Title", CadUi.Body, CadUi.White(0.72f));
        CadUi.Band(sparkTitle.rectTransform, 10f, 15f, 12f, 12f);
        spark = CadUi.Shape(card, "Line", PaintSpark);
        CadUi.Band(spark.rectTransform, SparkTop, SparkH, 16f, 16f);
        empty = CadUi.Text(Root, "Empty", CadUi.Body, CadUi.White(0.55f), wrap: true);
        empty.text = "No versions yet.";
    }

    public override void Show(CadPanelState s)
    {
        current = s.version;
        bands = s.primary;
        sparkTitle.text = (s.primary != null ? CadUi.Clean(s.primary.label) : "Main metric") + " across versions";

        chrono.Clear();
        for (int i = s.versions.Count - 1; i >= 0; i--) chrono.Add(s.versions[i]);
        while (chrono.Count > MaxSpark) chrono.RemoveAt(0);
        spark.Redraw();
        while (sparkLabels.Count < chrono.Count) sparkLabels.Add(CadUi.Text(card, "Version", CadUi.Unit, CadUi.White(0.5f), align: TextAlignmentOptions.Top));
        for (int i = 0; i < sparkLabels.Count; i++)
        {
            bool used = i < chrono.Count;
            CadUi.Show(sparkLabels[i], used);
            if (!used) continue;
            var v = chrono[i];
            string value = v.primary != null ? CadUi.Clean(v.primary.display) : "";
            string color = v.version == current ? "#FFFFFF" : "#FFFFFFCC";
            sparkLabels[i].text = "v" + v.version + "\n<size=110%><b><color=" + color + ">" + value;
        }

        shownRows = s.versions.Count;
        while (rows.Count < shownRows) rows.Add(NewRow());
        for (int i = 0; i < rows.Count; i++)
        {
            bool used = i < shownRows;
            CadUi.Show(rows[i].button, used);
            if (!used) continue;
            FillRow(rows[i], s.versions[i], s);
        }
        CadUi.Show(card, s.versions.Count > 0);
        CadUi.Show(empty, s.versions.Count == 0);
    }

    Row NewRow()
    {
        var r = new Row();
        r.button = CadButton.Create(Root, "Version", CadButton.Row, 13f, null);
        r.button.Button.onClick.AddListener(() => owner.GoToVersion(r.version));
        r.chip = CadUi.Pill(r.button.transform, "Chip", CadUi.White(0.08f));
        CadUi.Place(r.chip.rectTransform, 10f, (RowH - 34f) * 0.5f, 34f, 34f);
        r.chipText = CadUi.Text(r.chip.transform, "Text", CadUi.Body, Color.white, bold: true, align: TextAlignmentOptions.Center);
        CadUi.Stretch(r.chipText.rectTransform, 0f, 0f, 0f, 0f);
        r.title = CadUi.Text(r.button.transform, "Title", 13.5f, Color.white, bold: true);
        r.meta = CadUi.Text(r.button.transform, "Meta", CadUi.Small, CadUi.White(0.5f));
        // Long labels stop before the metric (both boxes are taller than their line, so nothing hides). The bold
        // title cuts off: TMP looks for "…" in a bold Inter, which we don't ship, and warns.
        r.title.overflowMode = TextOverflowModes.Truncate;
        r.meta.overflowMode = TextOverflowModes.Ellipsis;
        r.metric = CadUi.Text(r.button.transform, "Metric", 13f, Color.white, bold: true, align: TextAlignmentOptions.Right);
        r.dot = CadUi.Pill(r.button.transform, "Dot", CadUi.Status("ok"));
        r.current = CadUi.Text(r.button.transform, "Current", CadUi.Tick, CadUi.White(0.55f), bold: true, align: TextAlignmentOptions.Right);
        r.current.characterSpacing = 8f;
        r.current.text = "CURRENT";
        return r;
    }

    void FillRow(Row r, CadVersion v, CadPanelState s)
    {
        r.version = v.version;
        bool isCurrent = v.version == current;
        r.button.On = isCurrent;
        r.button.Interactable = isCurrent || (!s.demoRunning && !s.building);  // the current one isn't dimmed; a tap on it does nothing
        r.chip.color = isCurrent ? CadUi.White(0.92f) : CadUi.White(0.08f);
        r.chipText.color = isCurrent ? CadUi.Dark : Color.white;
        r.chipText.text = "v" + v.version;
        r.title.text = v.parent.HasValue ? CadUi.Clean(string.IsNullOrEmpty(v.label) ? "Edited" : v.label) : "Opened with the defaults";

        string diffs = null;
        if (v.parent.HasValue)
            foreach (var other in s.versions)
                if (other.version == v.parent.Value && other.parameters != null && v.parameters != null)
                {
                    diffs = CadOverviewTab.Diffs(s.p, other.parameters, null, 2, " · ", v.parameters);
                    break;
                }
        if (!v.parent.HasValue) diffs = "defaults";
        string source = CadUi.Source(v.source), ago = CadUi.Ago(v.createdAt);
        var b = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(diffs)) b.Append(diffs);
        if (!string.IsNullOrEmpty(source)) b.Append(b.Length > 0 ? " · " : "").Append(source);
        if (ago != null) b.Append(b.Length > 0 ? " · " : "").Append(ago);
        r.meta.text = b.ToString();

        bool hasMetric = v.primary != null;
        r.metric.text = hasMetric ? CadUi.Clean(v.primary.display) : "";
        r.dot.color = CadUi.Status(v.primary?.status ?? "ok");
        CadUi.Show(r.dot, hasMetric);
        CadUi.Show(r.current, isCurrent);
    }

    public override float Layout(float w)
    {
        float y = 0f;
        if (card.gameObject.activeSelf)
        {
            CadUi.Band(card, y, CardH);
            float plotW = w - 32f;
            int n = chrono.Count;
            for (int i = 0; i < n; i++)
            {
                float x = 16f + SparkX(i, n, plotW);
                CadUi.Place(sparkLabels[i].rectTransform, x - 40f, SparkTop + SparkH + 2f, 80f, LabelsH);
            }
            y += CardH + Gap;
        }
        else
        {
            CadUi.Band(empty.rectTransform, y, 20f);
            y += 20f + Gap;
        }
        for (int i = 0; i < shownRows; i++)
        {
            var r = rows[i];
            CadUi.Band(r.button.Rect, y, RowH);
            bool isCurrent = r.version == current;
            float metricW = CadUi.Width(r.metric, r.metric.text) + 2f;
            float right = Mathf.Max(metricW + 14f, isCurrent ? CadUi.Width(r.current, "CURRENT") + 2f : 0f);
            float metricY = isCurrent ? 10f : (RowH - 18f) * 0.5f;
            CadUi.PlaceRight(r.metric.rectTransform, 10f, metricY, metricW, 18f);
            CadUi.PlaceRight(r.dot.rectTransform, 10f + metricW + 6f, metricY + 5f, 8f, 8f);
            CadUi.PlaceRight(r.current.rectTransform, 10f, metricY + 20f, 90f, 13f);
            CadUi.Band(r.title.rectTransform, 10f, 18f, 56f, 10f + right + 10f);
            CadUi.Band(r.meta.rectTransform, 30f, 15f, 56f, 10f + right + 10f);
            y += RowH + Gap;
        }
        return Mathf.Max(0f, y - Gap);
    }

    static float SparkX(int i, int n, float w) => 18f + i * (w - 36f) / Mathf.Max(1, n - 1);

    // The metric's bands as faint stripes, the path through the versions, a dot each (the current one ringed).
    void PaintSpark(CadShape s)
    {
        int n = chrono.Count;
        if (n == 0) return;
        float w = s.Width, h = s.Height, lo, hi;
        if (bands != null && bands.IsGauge)
        {
            lo = bands.min.Value;
            hi = bands.max.Value;
        }
        else
        {
            lo = float.MaxValue;
            hi = float.MinValue;
            foreach (var v in chrono)
                if (v.primary != null)
                {
                    lo = Mathf.Min(lo, v.primary.value);
                    hi = Mathf.Max(hi, v.primary.value);
                }
            if (lo > hi) { lo = 0f; hi = 1f; }
            float pad = Mathf.Max(1e-3f, (hi - lo) * 0.15f);
            lo -= pad;
            hi += pad;
        }
        float Y(float v) => h - 8f - Mathf.Clamp01((v - lo) / Mathf.Max(1e-6f, hi - lo)) * (h - 16f);
        if (bands != null && bands.IsGauge && bands.bands != null)
        {
            float from = lo;
            foreach (var b in bands.bands)
            {
                float y0 = Y(Mathf.Min(b.to, hi)), y1 = Y(from);
                if (y1 > y0) s.FillRect(0f, y0, w, y1 - y0, CadUi.Alpha(CadUi.Status(b.status), 0.09f));
                from = b.to;
            }
        }
        s.Begin();
        int drawn = 0;
        for (int i = 0; i < n; i++)
        {
            if (chrono[i].primary == null) continue;
            float x = SparkX(i, n, w), y = Y(chrono[i].primary.value);
            if (drawn++ == 0) s.M(x, y);
            else s.L(x, y);
        }
        if (drawn > 1) s.Stroke(1.4f, CadUi.White(0.55f));
        for (int i = 0; i < n; i++)
        {
            var v = chrono[i];
            if (v.primary == null) continue;
            float x = SparkX(i, n, w), y = Y(v.primary.value);
            bool cur = v.version == current;
            if (cur) s.Ring(x, y, 8.5f, 1.2f, CadUi.White(0.8f));
            s.Disc(x, y, cur ? 5f : 3.6f, CadUi.Status(v.primary.status));
        }
    }
}
