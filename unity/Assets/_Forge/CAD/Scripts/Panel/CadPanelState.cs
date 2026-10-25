using System.Collections.Generic;

/// <summary>
/// What the design panel shows, worked out by <see cref="CadParamsPanel"/> whenever something changes and read by its
/// sections: the open version and its verdict, primary metric and demo; its parent (the "was" ghosts, Last change);
/// the design's versions (History); the rocket's flight; and the live state (building, failed, demo, exploded,
/// expanded, mock). Plain data: the sections only read it.
/// </summary>
public sealed class CadPanelState
{
    public CadSession session;
    public CadParams p;                 // the open version's parameters (GET /cad/params)
    public CadModelUpdated info;        // the open version (cad.model_updated)
    public CadAnalysis a;
    public int version;
    public string title, glyph;

    public CadVerdict verdict;          // the hub's, or worked out from the metrics
    public CadMetric primary;           // the verdict's metric: the dial, the strip, the sparkline
    public string question;             // "Will it fly?"
    public CadDemo demo;                // Fly / Hover / Run, or null
    public CadFlight flight;            // this version's flight, once fetched (rocket)

    public int? parent;                 // the version this one was made from
    public Dictionary<string, object> parentParams;
    public CadAnalysis parentAnalysis;
    public CadFlight parentFlight;
    public string label;                // this version in words (Bit's request, a preset, the change)
    public string source;               // how it was made (pet, preset, api, ...)
    public double? createdAt;
    public readonly List<CadVersion> versions = new List<CadVersion>();  // newest first

    public bool ready, mock, live, building, demoRunning, exploded, expanded;
    public string buildingWhat;
    public int buildingVersion;         // the version a build will become (best guess: the newest known + 1)
    public CadRegenFailed failure;
    public CadDemoFrame frame;          // the running demo's latest frame
    public bool hasFrame;
    public string pendingParam;         // the row whose change is building (its value shows orange)
    public object pendingValue;
    public bool canUndo;
    public int partsOff;                // parts taken off on the stage (Reassemble)
    public bool lifeSize;               // on the stage at its real size (the Life size pill)

    public CadMetric Metric(string id) => a?.metrics?.Find(m => m.id == id);

    /// <summary>The verdict card's question, from the demo: launch / hover fly, spin runs, walk stands.</summary>
    public static string Question(CadDemo d)
    {
        string kind = d?.kind;
        if (kind == CadDemoPlayer.Launch || kind == CadDemoPlayer.Hover) return "Will it fly?";
        if (kind == CadDemoPlayer.Spin) return "Will it run?";
        if (kind == CadDemoPlayer.Walk) return "Will it stand?";
        return "Is it sound?";
    }

    /// <summary>
    /// The hub's verdict, or one from the metrics for versions built before it sent one (CAD_PLAN round 4 part 2's
    /// default): the first of the worst metrics speaks ("Needs work" / "Check it"), else "Looks good".
    /// </summary>
    public static CadVerdict Verdict(CadAnalysis a)
    {
        if (a?.verdict != null && !string.IsNullOrEmpty(a.verdict.headline)) return a.verdict;
        CadMetric worst = null;
        int rank = 0;
        if (a?.metrics != null)
            foreach (var m in a.metrics)
            {
                int r = m.status == "bad" ? 2 : m.status == "warn" ? 1 : 0;
                if (r > rank) { rank = r; worst = m; }
            }
        var primary = Primary(a, null);
        if (worst == null)
            return new CadVerdict { status = "ok", headline = "Looks good", sentence = "Every number is in its good range.", metric = primary?.id };
        string note = string.IsNullOrEmpty(worst.note) ? "" : " (" + worst.note + ")";
        return new CadVerdict
        {
            status = worst.status, headline = rank == 2 ? "Needs work" : "Check it",
            sentence = worst.label + " is " + worst.display + note + ".", metric = worst.id,
        };
    }

    /// <summary>The verdict's metric, else the first with a range, else the first.</summary>
    public static CadMetric Primary(CadAnalysis a, CadVerdict v)
    {
        if (a?.metrics == null || a.metrics.Count == 0) return null;
        if (v != null && !string.IsNullOrEmpty(v.metric))
        {
            var m = a.metrics.Find(x => x.id == v.metric);
            if (m != null) return m;
        }
        return a.metrics.Find(x => x.IsGauge) ?? a.metrics[0];
    }

    /// <summary>"cal margin", "mm clearance", "mm rim": the unit and the label's last word.</summary>
    public static string UnitLine(CadMetric m)
    {
        if (m == null) return "";
        string label = m.label ?? "";
        int space = label.LastIndexOf(' ');
        string word = (space >= 0 ? label.Substring(space + 1) : label).ToLowerInvariant();
        string unit = CadUi.Clean(m.unit ?? "").Trim();
        if (unit.Length == 0) return word;
        return unit + " " + word;
    }

    /// <summary>Tick values at a gauge's band edges, crowded ones dropped (the boards' rule): first and last always.</summary>
    public static void Ticks(CadMetric m, List<float> into, bool thin)
    {
        into.Clear();
        if (m == null || !m.IsGauge) return;
        float min = m.min.Value, max = m.max.Value, span = max - min;
        var all = new List<float> { min };
        foreach (var b in m.bands)
            if (b.to > min && b.to <= max + 1e-4f && System.Math.Abs(b.to - all[all.Count - 1]) > 1e-4f) all.Add(b.to);
        if (System.Math.Abs(all[all.Count - 1] - max) > 1e-4f) all.Add(max);
        for (int i = 0; i < all.Count; i++)
        {
            bool keep = i == 0 || i == all.Count - 1 || !thin
                || ((all[i] - all[i - 1]) / span > 0.09f && (all[i + 1] - all[i]) / span > 0.04f);
            if (keep) into.Add(all[i]);
        }
    }

    /// <summary>"1", "1.5", "3.75".</summary>
    public static string TickText(float v)
    {
        if (System.Math.Abs(v - System.Math.Round(v)) < 1e-4) return ((int)System.Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
