using System.Collections.Generic;
using Newtonsoft.Json;

// Hub CAD REST payloads: CAD_PLAN.md section 5. JSON names are the hub's snake_case.
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

    public bool IsOptions => options != null && options.Count > 0;
    public double Number => value is string ? 0 : System.Convert.ToDouble(value);
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
}

/// <summary>
/// One frame of a running demo (Fly / Hover / Run), for panels: CadViewer.DemoFrame and CadSession.DemoFrame, raised
/// every frame while it runs. Numbers are the design's real ones (the flight sim's altitude and speed), not the
/// scaled-down motion in the room.
/// </summary>
public struct CadDemoFrame
{
    public string kind;               // launch | hover | spin
    public string phase;              // launch: the last flight event passed (Liftoff, Off the rail, Burnout, Apogee,
                                      // Parachute, Landing); hover: Spooling up, Lifting off, Hovering, Landing,
                                      // Spooling down; spin: Starting, Running, Stopping
    public float timeS;               // launch: flight time (sim seconds); hover / spin: seconds since it started
    public float altitudeM;           // launch: altitude (m); hover: height above its base in the room (m); spin: 0
    public float speedMps;            // launch: speed (m/s); otherwise 0
    public float progress;            // launch: share of the flight flown; hover / spin: how spun up (0..1)
    public float playback;            // launch: how much faster than real time it plays (1, then 8 on the way down)
    public List<CadMetric> readouts;  // hover / spin: the design's demo.readouts (fixed per version); launch: null
}
