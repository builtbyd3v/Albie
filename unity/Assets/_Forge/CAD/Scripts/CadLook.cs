using System.Globalization;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The look of what the CAD viewer draws at runtime (explode chips, markers, dimensions, the demos), from
/// HANDOFF_UI2/cad/CAD_UX_SPEC.md: Forge/CadLight for lines and beads of light (white with a faint CAD hue), graphite
/// chips with Inter text for labels (<see cref="CadLabel"/>, <see cref="CadChips"/>), status colours, and the small maths
/// they share: the hub's model frame to Unity, springs, names, number formats, and the room's ceiling.
/// </summary>
public static class CadLook
{
    public static readonly Color White = Color.white;
    public static readonly Color CgBlue = Hex(0x5AA0FF);
    public static readonly Color CpRed = Hex(0xFF5A4E);
    public static Color Hue => ForgeTheme.CAD;
    // Status: ok / warn / bad, and the lighter tints text uses on graphite.
    public static readonly Color Ok = Hex(0x34D17A), Warn = Hex(0xFFB43C), Bad = Hex(0xFF5A4E);
    public static readonly Color Chip = new Color32(17, 19, 23, 255);

    // Sizes in the room (m): lines keep their width whatever the model's scale.
    public const float Line = 0.0026f, ThinLine = 0.0019f, Bead = 0.0105f, Tick = 0.0035f;
    // Labels: title and second line (mm) at 0.8 m; they are rescaled with distance to stay about 0.9 deg tall.
    public const float TitleMm = 12.5f, SubMm = 10.5f, ReferenceDistance = 0.8f;
    // Kept for older callers of CadLabel's size constructor.
    public const float LabelMm = TitleMm, SmallLabelMm = 11.5f;

    const float DefaultCeiling = 2.5f;  // world height when there's no room scan (sandbox): the floor is at 0

    static readonly int ZTestId = Shader.PropertyToID("_ZTest");
    static Material sceneLight, overlayLight, chip;

    /// <summary>
    /// Forge/CadLight: depth-tested for things in the scene (the trail, the parachute), or drawn over the model
    /// (<paramref name="overlay"/>: markers, leaders, dimensions). One shared material each.
    /// </summary>
    public static Material Light(bool overlay)
    {
        if (overlay)
        {
            if (overlayLight == null) overlayLight = Make("CadLook/CadLight", "Forge/CadLight", CompareFunction.Always, 3030);
            return overlayLight;
        }
        if (sceneLight == null) sceneLight = Make("CadLook/CadLight", "Forge/CadLight", CompareFunction.LessEqual, 3020);
        return sceneLight;
    }

    /// <summary>
    /// Forge/CadChip: the graphite backgrounds of labels, drawn just before their text. Both draw after the panels
    /// (UI at 3000, which writes no depth), or a screen behind the model would paint over its labels.
    /// </summary>
    public static Material ChipMaterial => chip != null ? chip : chip = Make("CadLook/CadChip", "Forge/CadChip", CompareFunction.LessEqual, 3018);

    /// <summary>Inter for labels: a copy of the Core font's material that draws after the chips (the shared one stays at 3000).</summary>
    public static Material TextMaterial
    {
        get
        {
            if (text != null || ForgeTheme.Font == null) return text;
            text = new Material(ForgeTheme.Font.material) { name = "Inter (CAD labels)", renderQueue = 3019 };
            return text;
        }
    }
    static Material text;

    static Material Make(string resource, string shaderName, CompareFunction zTest, int queue)
    {
        var shader = Resources.Load<Shader>(resource);
        if (shader == null) shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[CadLook] Shader {shaderName} is missing (CAD/Resources/{resource}); falling back to Sprites/Default.");
            shader = Shader.Find("Sprites/Default");
        }
        var material = new Material(shader) { name = shaderName + " (CAD)", renderQueue = queue };
        if (material.HasProperty(ZTestId)) material.SetFloat(ZTestId, (float)zTest);
        return material;
    }

    /// <summary>A marker's colour by kind: CG blue, CP red, thrust in the CAD hue, the rest white.</summary>
    public static Color MarkerColor(string kind)
    {
        switch (kind)
        {
            case "cg": return CgBlue;
            case "cp": return CpRed;
            case "thrust": return Hue;
            default: return White;
        }
    }

    /// <summary>Rich-text colour for text on graphite: ok / warn / bad (else white).</summary>
    public static string StatusText(string status) => status == "bad" ? "#FF9A90" : status == "warn" ? "#FFD27F" : status == "ok" ? "#7BF0AE" : "#FFFFFF";

    public static Color StatusColor(string status) => status == "bad" ? Bad : status == "warn" ? Warn : Ok;

    public static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

    /// <summary>A model-frame point or direction from the hub (glTF: m, Y up) in CadViewer's holder space: glTFast flips X.</summary>
    public static Vector3 Flip(float[] p, Vector3 fallback) =>
        p != null && p.Length >= 3 ? new Vector3(-p[0], p[1], p[2]) : fallback;

    public static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    public static float EaseInOut(float t)
    {
        t = Mathf.Clamp01(t);
        if (t < 0.5f) return 4f * t * t * t;
        float u = 1f - t;
        return 1f - 4f * u * u * u;
    }

    public static float EaseOut(float t)
    {
        float u = 1f - Mathf.Clamp01(t);
        return 1f - u * u * u;
    }

    /// <summary>
    /// A damped spring's step response at <paramref name="seconds"/> after release: 0 to 1, with
    /// <paramref name="omega"/> rad/s and damping ratio <paramref name="zeta"/> (1 = no overshoot, 0.72 about 4%).
    /// </summary>
    public static float SpringAt(float seconds, float omega, float zeta)
    {
        if (seconds <= 0f) return 0f;
        if (zeta >= 0.999f) return 1f - Mathf.Exp(-omega * seconds) * (1f + omega * seconds);
        float wd = omega * Mathf.Sqrt(1f - zeta * zeta);
        return 1f - Mathf.Exp(-zeta * omega * seconds) * (Mathf.Cos(wd * seconds) + zeta * omega / wd * Mathf.Sin(wd * seconds));
    }

    /// <summary>
    /// A damped spring at progress <paramref name="t"/> (0..1 of a tween) that lands on exactly 1: what motion is left is
    /// eased out over the last 30%. <paramref name="frequency"/>: oscillations per tween.
    /// </summary>
    public static float Spring(float t, float damping, float frequency)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        float w = 2f * Mathf.PI * Mathf.Max(0.01f, frequency), x;
        if (damping < 0.999f)
        {
            float wd = w * Mathf.Sqrt(1f - damping * damping);
            x = 1f - Mathf.Exp(-damping * w * t) * (Mathf.Cos(wd * t) + damping * w / wd * Mathf.Sin(wd * t));
        }
        else x = 1f - Mathf.Exp(-w * t) * (1f + w * t);
        float u = Mathf.Clamp01((t - 0.7f) / 0.3f);
        return Mathf.LerpUnclamped(x, 1f, u * u * (3f - 2f * u));
    }

    /// <summary>"centering_ring_aft" -> "Centering ring aft".</summary>
    public static string Pretty(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name.Replace('_', ' ').Trim();
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant() : s;
    }

    /// <summary>"fin_3" -> "fin": ids and names with a trailing _N are copies of one kind of part.</summary>
    public static string BaseName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        if (end == name.Length || end == 0) return name;
        while (end > 0 && (name[end - 1] == '_' || name[end - 1] == ' ' || name[end - 1] == '-')) end--;
        return end > 0 ? name.Substring(0, end) : name;
    }

    /// <summary>"Fin" -> "Fins" (for "Fins ×3").</summary>
    public static string Plural(string name) =>
        string.IsNullOrEmpty(name) || name.EndsWith("s") ? name : name + "s";

    static CultureInfo Inv => CultureInfo.InvariantCulture;

    /// <summary>"390 mm", "95.5 mm".</summary>
    public static string Millimetres(float mm)
    {
        float r = Mathf.Round(mm * 10f) / 10f;
        return (Mathf.Abs(r - Mathf.Round(r)) < 0.05f ? r.ToString("0", Inv) : r.ToString("0.0", Inv)) + " mm";
    }

    /// <summary>"1.04 kg", "308 g", "28.3 g".</summary>
    public static string Grams(double grams)
    {
        if (grams >= 1000.0) return (grams / 1000.0).ToString("0.00", Inv) + " kg";
        if (grams >= 100.0) return grams.ToString("0", Inv) + " g";
        return grams.ToString("0.0", Inv) + " g";
    }

    /// <summary>"3,000".</summary>
    public static string Thousands(float value) => value.ToString("#,0", Inv);

    static readonly System.Collections.Generic.List<Vector3> vertices = new System.Collections.Generic.List<Vector3>();

    /// <summary>
    /// How far a part reaches from an axis (holder units = model m): its swept radius (a prop) or tip radius (a gear),
    /// from its mesh vertices (box corners would overstate a disc by 41%). Done once per demo or version, not per frame.
    /// </summary>
    public static float PartRadius(Transform part, Transform holder, Vector3 origin, Vector3 axis)
    {
        if (part == null || holder == null || axis.sqrMagnitude < 1e-10f) return 0f;
        axis.Normalize();
        float best = 0f;
        var toHolder = holder.worldToLocalMatrix;
        foreach (var filter in part.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = filter.sharedMesh;
            if (mesh == null || filter.name.StartsWith("Glow ", System.StringComparison.Ordinal)) continue;
            var m = toHolder * filter.transform.localToWorldMatrix;
            if (mesh.isReadable)
            {
                mesh.GetVertices(vertices);
                for (int i = 0; i < vertices.Count; i++)
                    best = Mathf.Max(best, Vector3.ProjectOnPlane(m.MultiplyPoint3x4(vertices[i]) - origin, axis).magnitude);
            }
            else
            {
                var b = mesh.bounds;  // not readable: the box's corners, a little generous
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    best = Mathf.Max(best, Vector3.ProjectOnPlane(m.MultiplyPoint3x4(corner) - origin, axis).magnitude);
                }
            }
        }
        vertices.Clear();
        return best;
    }

    static MRUKRoom Room => MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;

    /// <summary>The room's ceiling height (world y): the scan's ceiling, else 2.5 m (floor at 0, as in Main and the sandbox).</summary>
    public static float CeilingY()
    {
        var room = Room;
        if (room != null && room.CeilingAnchors.Count > 0 && room.CeilingAnchors[0] != null)
            return room.CeilingAnchors[0].transform.position.y;
        return DefaultCeiling;
    }

    /// <summary>The room's floor (world y): the scan's floor, else 0 (as in Main and the sandbox).</summary>
    public static float FloorY()
    {
        var room = Room;
        return room != null && room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : 0f;
    }

    /// <summary>Inside the room's walls (true when there's no room scan to ask).</summary>
    public static bool InRoom(Vector3 world)
    {
        var room = Room;
        return room == null || room.IsPositionInRoom(world, false);
    }

    static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
}
