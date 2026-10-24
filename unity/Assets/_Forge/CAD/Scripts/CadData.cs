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

public class CadFlight
{
    [JsonProperty("apogee_m")] public float apogeeM;
    [JsonProperty("time_to_apogee_s")] public float timeToApogeeS;
    [JsonProperty("max_speed_mps")] public float maxSpeedMps;
    [JsonProperty("warning")] public string warning;
    [JsonProperty("trajectory_m")] public List<float[]> trajectory;  // [east, up, north] from the pad
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
