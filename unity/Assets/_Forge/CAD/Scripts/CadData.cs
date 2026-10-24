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
