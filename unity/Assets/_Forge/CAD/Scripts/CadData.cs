using System.Collections.Generic;
using Newtonsoft.Json;

// Hub CAD payloads: mirrors hub/events.py and CAD_PLAN.md section 5. JSON names are the hub's snake_case.
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

public class CadParams
{
    [JsonProperty("part")] public string part;
    [JsonProperty("version")] public int version;
    [JsonProperty("params")] public List<CadParam> parameters = new List<CadParam>();

    public CadParam Find(string name) => parameters.Find(p => p.name == name);
}

public class CadMarkers
{
    [JsonProperty("cg")] public float[] cg;
    [JsonProperty("cp")] public float[] cp;
}

public class CadAnalysis
{
    [JsonProperty("mass_g")] public float massG;
    [JsonProperty("length_mm")] public float? lengthMm;
    [JsonProperty("diameter_mm")] public float? diameterMm;
    [JsonProperty("cg_mm")] public float? cgMm;               // from the nose tip
    [JsonProperty("cp_mm")] public float? cpMm;               // from the nose tip
    [JsonProperty("margin_cal")] public float? marginCal;
    [JsonProperty("stable")] public bool? stable;
    [JsonProperty("stability")] public string stability;      // unstable | marginal | stable | overstable
    [JsonProperty("motor")] public string motor;
    [JsonProperty("markers_gltf")] public CadMarkers markers; // GLB scene space: metres, Y-up, on the axis
}

public class CadModelUpdated
{
    [JsonProperty("version")] public int version;
    [JsonProperty("params")] public Dictionary<string, object> parameters;
    [JsonProperty("previous_version")] public int? previousVersion;
    [JsonProperty("changed_ids")] public List<string> changedIds = new List<string>();
    [JsonProperty("removed_ids")] public List<string> removedIds = new List<string>();
    [JsonProperty("source")] public string source;
    [JsonProperty("analysis")] public CadAnalysis analysis;
}

public class CadRegenFailed
{
    [JsonProperty("source")] public string source;
    [JsonProperty("kind")] public string kind;  // params | build | timeout | crash
    [JsonProperty("error")] public string error;
    [JsonProperty("error_line")] public int? errorLine;
    [JsonProperty("kept_version")] public int? keptVersion;
}

public class CadView
{
    [JsonProperty("explode")] public bool? explode;
    [JsonProperty("focus_id")] public string focusId;
}

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
