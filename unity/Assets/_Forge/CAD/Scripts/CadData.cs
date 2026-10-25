using System.Collections.Generic;
using Newtonsoft.Json;

// Hub CAD REST payloads: docs/CAD_PLAN.md section 5. JSON names are the hub's snake_case.
// Newtonsoft-only DTOs (not Unity-serialized), hence no [Serializable].

public class CadParam
{
    [JsonProperty("name")] public string name;
    [JsonProperty("value")] public object value;  // double/long for numbers, string for options
    [JsonProperty("min")] public double? min;
    [JsonProperty("max")] public double? max;
    [JsonProperty("unit")] public string unit;
    [JsonProperty("step")] public double? step;
    [JsonProperty("options")] public List<string> options;
    [JsonProperty("label")] public string label;
    [JsonProperty("group")] public string group;    // the panel section ("General" if the part sets none)
    [JsonProperty("help")] public string help;      // one line, "" if none
    [JsonProperty("parts")] public List<string> parts;   // part ids it shapes (CadViewer.HighlightParam), if sent
    [JsonProperty("dimension")] public string dimension; // id in analysis.dimensions that shows it, if sent
    [JsonProperty("default")] public object defaultValue; // as built (clamped to the range): the slider's default tick

    public bool IsOptions => options != null && options.Count > 0;
    public double Number => value is string ? 0 : System.Convert.ToDouble(value);
    public bool HasDefault => defaultValue != null && !(defaultValue is string);
    public double DefaultNumber => HasDefault ? System.Convert.ToDouble(defaultValue) : Number;
}

/// <summary>A named set of values from the design's PRESETS, applied on top of the current ones.</summary>
public class CadPreset
{
    [JsonProperty("name")] public string name;
    [JsonProperty("params")] public Dictionary<string, object> parameters = new Dictionary<string, object>();
}

/// <summary>GET /cad/params: the design's rows, plus what the panel shows around them.</summary>
public class CadParams
{
    [JsonProperty("part")] public string part;
    [JsonProperty("title")] public string title;
    [JsonProperty("description")] public string description;
    [JsonProperty("version")] public int version;
    [JsonProperty("params")] public List<CadParam> parameters = new List<CadParam>();
    [JsonProperty("presets")] public List<CadPreset> presets = new List<CadPreset>();
    [JsonProperty("features")] public List<string> features = new List<string>();  // "flight": the Fly button
    [JsonProperty("groups")] public List<string> groups = new List<string>();      // parameter groups, panel order
    [JsonProperty("glyph")] public string glyph;     // the design's icon: rocket | drone | gears | box

    public CadParam Find(string name) => parameters.Find(p => p.name == name);
    public bool Has(string feature) => features != null && features.Contains(feature);
}

/// <summary>One design in GET /cad/parts.</summary>
public class CadPartInfo
{
    [JsonProperty("part")] public string part;
    [JsonProperty("title")] public string title;
    [JsonProperty("description")] public string description;
    [JsonProperty("presets")] public List<CadPreset> presets = new List<CadPreset>();
    [JsonProperty("features")] public List<string> features = new List<string>();
    [JsonProperty("version")] public int? version;   // null until it's first opened
    [JsonProperty("active")] public bool active;
    [JsonProperty("error")] public string error;     // the script doesn't parse
    [JsonProperty("glyph")] public string glyph;     // rocket | drone | gears | box
}

public class CadPartList
{
    [JsonProperty("active")] public string active;
    [JsonProperty("parts")] public List<CadPartInfo> parts = new List<CadPartInfo>();
}

/// <summary>
/// One number on the panel (analysis.metrics in cad.model_updated). With min and max it's a gauge coloured by
/// its bands; otherwise a stat coloured by its status.
/// </summary>
public class CadMetric
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label")] public string label;
    [JsonProperty("value")] public float value;
    [JsonProperty("unit")] public string unit;
    [JsonProperty("display")] public string display;
    [JsonProperty("status")] public string status;   // ok | warn | bad
    [JsonProperty("min")] public float? min;
    [JsonProperty("max")] public float? max;
    [JsonProperty("bands")] public List<CadMetricBand> bands = new List<CadMetricBand>();
    [JsonProperty("note")] public string note;

    public bool IsGauge => min.HasValue && max.HasValue && max.Value > min.Value;
}

public class CadMetricBand
{
    [JsonProperty("to")] public float to;
    [JsonProperty("status")] public string status;
}

public class CadMarkers
{
    [JsonProperty("cg")] public float[] cg;
    [JsonProperty("cp")] public float[] cp;
}

/// <summary>One row of GET /cad/versions (the open design's versions), or a stored mock version in mock mode.</summary>
public class CadVersion
{
    [JsonProperty("version")] public int version;
    [JsonProperty("parent")] public int? parent;
    [JsonProperty("source")] public string source;          // api | pet | preset | script | undo | reset | open | startup
    [JsonProperty("created_at")] public double? createdAt;  // unix seconds (the hub's clock); null for mocks
    [JsonProperty("head")] public bool head;                // the design's current version
    [JsonProperty("params")] public Dictionary<string, object> parameters;
    [JsonProperty("label")] public string label;            // the version in words: Bit's request, a preset, the change
    [JsonProperty("primary")] public CadPrimary primary;    // its main metric, or null
    [JsonIgnore] public CadAnalysis analysis;               // mock versions only: the whole analysis
}

/// <summary>A version's main metric in GET /cad/versions: the first metric with a range, else the first.</summary>
public class CadPrimary
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label")] public string label;
    [JsonProperty("value")] public float value;
    [JsonProperty("display")] public string display;
    [JsonProperty("status")] public string status;          // ok | warn | bad

    /// <summary>The same pick from a whole analysis (mock versions, which carry their analysis): null if no metrics.</summary>
    public static CadPrimary Of(CadAnalysis a)
    {
        if (a?.metrics == null || a.metrics.Count == 0) return null;
        CadMetric m = a.metrics.Find(x => x.IsGauge) ?? a.metrics[0];
        return new CadPrimary { id = m.id, label = m.label, value = m.value, display = m.display, status = m.status };
    }
}

// The CAD event payloads (CadAnalysis, CadModelUpdated, CadRegenFailed, CadView) live in Core/Net/ForgeEvents.cs
// with the other hub events.

/// <summary>GET /cad/flight: the climb (RocketPy) and, since round 4, the way down, as fixed-rate samples and events.
/// Flights stored before round 4 have no samples or events (empty lists, zero times).</summary>
public class CadFlight
{
    [JsonProperty("apogee_m")] public float apogeeM;
    [JsonProperty("time_to_apogee_s")] public float timeToApogeeS;
    [JsonProperty("max_speed_mps")] public float maxSpeedMps;
    [JsonProperty("warning")] public string warning;
    [JsonProperty("trajectory_m")] public List<float[]> trajectory;  // [east, up, north] from the pad, to apogee
    [JsonProperty("rail_exit_speed_mps")] public float railExitSpeedMps;
    [JsonProperty("rail_exit_margin_cal")] public float railExitMarginCal;
    [JsonProperty("liftoff_mass_g")] public float liftoffMassG;
    [JsonProperty("motor")] public string motor;
    [JsonProperty("assumptions")] public List<string> assumptions = new List<string>();
    [JsonProperty("rail_exit_time_s")] public float railExitTimeS;
    [JsonProperty("burnout_time_s")] public float burnoutTimeS;
    [JsonProperty("deploy_time_s")] public float? deployTimeS;       // null: it lands before the parachute opens
    [JsonProperty("descent_rate_mps")] public float descentRateMps;
    [JsonProperty("landing_time_s")] public float landingTimeS;
    [JsonProperty("drift_m")] public float driftM;                   // landing distance from the pad
    [JsonProperty("sample_dt_s")] public float sampleDtS;
    [JsonProperty("events")] public List<CadFlightEvent> events = new List<CadFlightEvent>();      // time order
    [JsonProperty("samples")] public List<CadFlightSample> samples = new List<CadFlightSample>();  // pad to landing

    /// <summary>
    /// The flight's moments (sim seconds) with the Fly demo's fallbacks for older flights: landing, apogee, burnout, the
    /// parachute (landing if it never opens), and where the demo stops playing in real time and speeds up ×8.
    /// </summary>
    public void Times(out float land, out float apogee, out float burnout, out float deploy, out float switchAt)
    {
        float last = samples != null && samples.Count > 0 ? samples[samples.Count - 1].t : 0f;
        land = landingTimeS > 0f ? landingTimeS : last;
        apogee = EventTime("apogee", timeToApogeeS > 0f ? timeToApogeeS : land * 0.12f);
        burnout = burnoutTimeS > 0f ? burnoutTimeS : EventTime("burnout", apogee * 0.3f);
        bool deploys = deployTimeS.HasValue && deployTimeS.Value < land;
        deploy = deploys ? System.Math.Max(deployTimeS.Value, apogee) : land;
        switchAt = deploys ? deploy : System.Math.Min(land, apogee + 0.8f);
    }

    public float EventTime(string id, float fallback)
    {
        if (events != null)
            foreach (var e in events)
                if (e != null && e.id == id) return e.t;
        return fallback;
    }
}

/// <summary>liftoff | rail_exit | burnout | apogee | deploy | landing.</summary>
public class CadFlightEvent
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label")] public string label;
    [JsonProperty("t")] public float t;
    [JsonProperty("alt_m")] public float altM;
    [JsonProperty("speed_mps")] public float speedMps;
}

public class CadFlightSample
{
    [JsonProperty("t")] public float t;
    [JsonProperty("alt_m")] public float altM;
    [JsonProperty("downrange_m")] public float downrangeM;  // horizontal distance from the pad along the launch heading
    [JsonProperty("vz_mps")] public float vzMps;
    [JsonProperty("speed_mps")] public float speedMps;
}

/// <summary>
/// GET /cad/motion (mock motion.json): a keyframed demo, the humanoid's Walk. Model frame (CAD_PLAN section 5). Each joint
/// is a GLB assembly node that turns about origin + dir by angles_deg[i] at sample i, relative to the version's pose
/// (right-hand rule in glTF, like spin's rpm); root is where the whole model is at sample i: rot * p + pos.
/// </summary>
public class CadMotion
{
    [JsonProperty("rate_hz")] public float rateHz;
    [JsonProperty("loop")] public bool loop;
    [JsonProperty("samples")] public int samples;
    [JsonProperty("duration_s")] public float durationS;
    [JsonProperty("joints")] public List<CadMotionJoint> joints = new List<CadMotionJoint>();
    [JsonProperty("root")] public CadMotionRoot root;                 // null: the model stays put
    [JsonProperty("centre")] public float[] centre;                   // the point the path goes round, if any
    [JsonProperty("values")] public Dictionary<string, float> values = new Dictionary<string, float>();  // radius_mm, lap_s
    [JsonProperty("tracks")] public Dictionary<string, float[]> tracks = new Dictionary<string, float[]>(); // load_pct
}

public class CadMotionJoint
{
    [JsonProperty("id")] public string id;
    [JsonProperty("origin")] public float[] origin;
    [JsonProperty("dir")] public float[] dir;
    [JsonProperty("angles_deg")] public float[] anglesDeg;
}

public class CadMotionRoot
{
    [JsonProperty("pos")] public List<float[]> pos = new List<float[]>();   // metres
    [JsonProperty("rot")] public List<float[]> rot = new List<float[]>();   // x, y, z, w
}

public class CadEnvelope<T>
{
    [JsonProperty("type")] public string type;
    [JsonProperty("payload")] public T payload;
}

/// <summary>Everything the viewer needs to show one version.</summary>
public class CadUpdate
{
    public CadModelUpdated info;
    public byte[] glb;
    public string metadata;
    public int generation;  // CadSession.Generation when it was published: a mock's v1 and the hub's v1 differ
}

/// <summary>
/// One frame of a running demo (Fly / Hover / Run), for panels: CadViewer.DemoFrame and CadSession.DemoFrame, raised
/// every frame while it runs. Numbers are the design's real ones (the flight sim's altitude and speed), not the
/// scaled-down motion in the room.
/// </summary>
public struct CadDemoFrame
{
    public string kind;               // launch | hover | spin | walk
    public string phase;              // launch: the last flight event passed (Liftoff, Off the rail, Burnout, Apogee,
                                      // Parachute, Landing); hover: Spooling up, Lifting off, Hovering, Landing,
                                      // Spooling down; spin: Starting, Running, Stopping; walk: Stepping out,
                                      // Walking, Stepping back
    public float timeS;               // launch: flight time (sim seconds); hover / spin: seconds since it started;
                                      // walk: seconds into the lap
    public float altitudeM;           // launch: altitude (m); hover: height above its base in the room (m); spin: 0
    public float speedMps;            // launch: speed (m/s); walk: walking speed (m/s, real); otherwise 0
    public float progress;            // launch: share of the flight flown; hover / spin: how spun up; walk: share of the lap
    public float load;                // walk: how hard the hardest-working joint works now, % of its rating
    public float playback;            // launch: how much faster than real time it plays (1, then 8 on the way down)
    public float countdownS;          // launch: seconds left in the countdown (0 once it's off the pad)
    public List<CadMetric> readouts;  // hover / spin / walk: the design's demo.readouts (fixed per version); launch: null
}
