using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// CadViewer's exploded view (CAD UX spec, section 5.1). Each leaf part flies out along the design's own vector
/// (analysis.explode: dir x dist_mm in the model frame, times <see cref="scale"/>, and <see cref="radialBoost"/> more for
/// radial designs so their label column doesn't crowd); a version without one falls back to the old spread (away from
/// the model's centre, more sideways than along the axis). Each part rides its own damped spring (omega 10.5 rad/s,
/// zeta 0.72: settled in about 0.55 s with about 4% overshoot), started order x 70 ms after the first, plus 30 ms per
/// repeat for copies (fins, arms). Assembling runs the same spring in reverse order, 50 ms apart. On the desk the whole
/// exploded model lifts so nothing sinks into the base (<see cref="SetLiftWeight"/>); on the stage it stays centred.
/// Parts taken off in the Inspect view (detached) stay where the user left them.
/// </summary>
public sealed class CadExploder
{
    public struct Part
    {
        public string id;
        public Transform transform;
        public Vector3 home;      // assembled localPosition
        public Vector3 offset;    // fully exploded, in the parent's space
        public Vector3 lift;      // the desk lift, in the parent's space
        public int order;
        public int repeat;        // which copy of its kind (0 for the first fin, 1 for the second...)
        public string kind;       // its name without a trailing _N: copies share it ("fin")
        public string name;       // metadata name, or the id
        public string material;   // metadata material name, or ""
        public float massG;       // metadata mass, or -1
        public bool detached;     // taken off in the Inspect view
        public Vector3 centre;    // visual centre, in the part's own space (labels, leaders)
        public Vector3 extents;   // half size of its bounds, in holder space (model m)
        public float from, value, delay;
    }

    const string LayerPrefix = "Glow ";  // CadViewer's outline layers
    const float Omega = 10.5f, Zeta = 0.72f, Settle = 0.9f;
    const float OutStagger = 0.07f, BackStagger = 0.05f, RepeatStagger = 0.03f;

    public float scale = 1f;                               // the design's distances times this
    public float radialBoost = 1.5f;                       // and this much more for radial designs (the quadcopter)
    public float axial = 0.6f, radial = 1.5f;              // the fallback spread

    readonly List<Part> parts = new List<Part>();
    float target, moveStart = -100f;
    float liftWeight = 1f, liftFrom = 1f, liftTo = 1f, liftStart = -100f, liftSeconds;
    int maxOrder;
    bool moving;

    public int Count => parts.Count;
    public Part this[int i] => parts[i];
    /// <summary>Heading out (or out).</summary>
    public bool Exploded => target > 0.5f;
    /// <summary>How far out the furthest part is: 0 assembled, about 1 exploded.</summary>
    public float Progress { get; private set; }
    public bool Moving => moving;
    /// <summary>The version's own explode vectors are in use (else the generic spread).</summary>
    public bool FromData { get; private set; }
    /// <summary>stack | radial | shaft | auto from the hub; "" for the fallback.</summary>
    public string Axis { get; private set; } = "";

    /// <summary>Take the parts of a newly shown version (assembled), in the explode state the viewer is in.</summary>
    public void Build(CadModel model, CadAnalysis analysis, Transform holder)
    {
        parts.Clear();
        maxOrder = 0;
        moving = false;
        Progress = target;
        if (model == null || model.Root == null) return;
        var data = analysis?.explode?.parts;
        FromData = data != null && data.Count > 0;
        Axis = FromData ? analysis.explode.axis ?? "" : "";
        Dictionary<string, CadExplodePart> byId = null;
        if (FromData)
        {
            byId = new Dictionary<string, CadExplodePart>();
            foreach (var e in data)
                if (e != null && !string.IsNullOrEmpty(e.id)) byId[e.id] = e;
        }

        int n = model.partIds.Count;
        var transforms = new Transform[n];
        var bounds = new Bounds[n];
        var all = new Bounds();
        bool first = true;
        for (int i = 0; i < n; i++)
        {
            transforms[i] = model.objects.TryGetValue(model.partIds[i], out var go) ? go.transform : null;
            if (transforms[i] == null) continue;
            if (!HolderBounds(transforms[i], holder, out bounds[i]))
                bounds[i] = new Bounds(holder.InverseTransformPoint(transforms[i].position), Vector3.zero);
            if (first) { all = bounds[i]; first = false; }
            else all.Encapsulate(bounds[i]);
        }

        // Each part's full explode in holder space (= model metres), and its stagger step.
        var offsets = new Vector3[n];
        var orders = new int[n];
        if (FromData)
        {
            float k = scale * (Axis == "radial" ? radialBoost : 1f);
            for (int i = 0; i < n; i++)
            {
                if (!byId.TryGetValue(model.partIds[i], out var e) || e.distMm <= 0f) continue;
                Vector3 dir = CadLook.Flip(e.dir, Vector3.up);
                if (dir.sqrMagnitude < 1e-8f) continue;
                offsets[i] = dir.normalized * (e.distMm / 1000f * k);
                orders[i] = Mathf.Max(0, e.order);
            }
        }
        else
        {
            // Away from the centre, more sideways than along the axis; outermost first, in three steps.
            float reach = 1e-6f;
            for (int i = 0; i < n; i++) reach = Mathf.Max(reach, (bounds[i].center - all.center).magnitude);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = bounds[i].center - all.center;
                offsets[i] = new Vector3(d.x * radial, d.y * axial, d.z * radial) * scale;
                orders[i] = Mathf.Min(2, (int)(3f * (1f - d.magnitude / reach)));
            }
        }

        // On the desk, lift everything so no part sinks below the base.
        float lowest = all.min.y;
        for (int i = 0; i < n; i++)
            if (transforms[i] != null) lowest = Mathf.Min(lowest, bounds[i].min.y + offsets[i].y);
        var lift = Vector3.up * Mathf.Max(0f, all.min.y - lowest);

        var copies = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            var t = transforms[i];
            if (t == null) continue;
            string id = model.partIds[i];
            string name = id, material = "";
            float massG = -1f;
            try
            {
                JObject o = model.Metadata?.GetObject(id);
                name = (string)o?["name"] ?? id;
                if (o?["material"] is JObject m) material = (string)m["name"] ?? "";
                double? kg = (double?)o?["mass"];
                if (kg.HasValue) massG = (float)(kg.Value * 1000.0);
            }
            catch (KeyNotFoundException) { }
            string kind = CadLook.BaseName(name);
            copies.TryGetValue(kind, out int repeat);
            copies[kind] = repeat + 1;
            var parent = t.parent;
            parts.Add(new Part
            {
                id = id,
                transform = t,
                home = t.localPosition,
                offset = parent.InverseTransformVector(holder.TransformVector(offsets[i])),
                lift = parent.InverseTransformVector(holder.TransformVector(lift)),
                order = orders[i],
                repeat = repeat,
                kind = kind,
                name = name,
                material = material,
                massG = massG,
                centre = t.InverseTransformPoint(holder.TransformPoint(bounds[i].center)),
                extents = bounds[i].extents,
                from = target,
                value = target,
            });
            maxOrder = Mathf.Max(maxOrder, orders[i]);
        }
        Apply();
    }

    /// <summary>Explode (true) or reassemble; <paramref name="instant"/> jumps there.</summary>
    public void SetTarget(bool exploded, bool instant = false)
    {
        float t = exploded ? 1f : 0f;
        if (t == target && !instant) return;
        target = t;
        moveStart = Time.time;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            p.from = instant ? target : p.value;
            if (instant) p.value = target;
            p.delay = exploded
                ? p.order * OutStagger + p.repeat * RepeatStagger
                : (maxOrder - p.order) * BackStagger + p.repeat * RepeatStagger * 0.5f;
            parts[i] = p;
        }
        moving = !instant && parts.Count > 0;
        if (instant)
        {
            Progress = target;
            Apply();
        }
    }

    /// <summary>1 on the desk (lift the exploded model off the base), 0 on the stage (keep it centred).</summary>
    public void SetLiftWeight(float weight, float seconds)
    {
        liftFrom = liftWeight;
        liftTo = weight;
        liftStart = Time.time;
        liftSeconds = seconds;
        if (seconds <= 0f)
        {
            liftWeight = weight;
            Apply();
        }
    }

    /// <summary>Move the parts (CadViewer, every frame).</summary>
    public void Tick()
    {
        float now = Time.time;
        bool lifting = liftWeight != liftTo;
        if (lifting)
        {
            float k = liftSeconds > 0f ? (now - liftStart) / liftSeconds : 1f;
            liftWeight = k >= 1f ? liftTo : Mathf.Lerp(liftFrom, liftTo, CadLook.Smooth(k));
        }
        if (!moving && !lifting) return;
        bool still = true;
        float progress = 0f;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (moving)
            {
                float t = now - moveStart - p.delay;
                if (t < Settle)
                {
                    still = false;
                    p.value = Mathf.LerpUnclamped(p.from, target, CadLook.SpringAt(t, Omega, Zeta));
                }
                else p.value = target;
                parts[i] = p;
            }
            progress = Mathf.Max(progress, Mathf.Abs(p.value));
            if (!p.detached && p.transform != null) p.transform.localPosition = p.home + (p.offset + p.lift * liftWeight) * p.value;
        }
        if (moving && still) moving = false;
        Progress = progress;
    }

    void Apply()
    {
        float progress = 0f;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            progress = Mathf.Max(progress, Mathf.Abs(p.value));
            if (!p.detached && p.transform != null) p.transform.localPosition = p.home + (p.offset + p.lift * liftWeight) * p.value;
        }
        Progress = progress;
    }

    /// <summary>Where <paramref name="part"/> belongs right now (assembled, or its exploded spot), in its parent's space.</summary>
    public bool TryGetSlot(Transform part, out Vector3 localPosition)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].transform != part) continue;
            // Where explode is heading, so a part sent home lands where the rest end up.
            localPosition = parts[i].home + (parts[i].offset + parts[i].lift * liftTo) * target;
            return true;
        }
        localPosition = default;
        return false;
    }

    public void SetDetached(Transform part, bool detached)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].transform != part) continue;
            var p = parts[i];
            p.detached = detached;
            parts[i] = p;
            return;
        }
    }

    /// <summary>The part with this id's index, or -1.</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < parts.Count; i++)
            if (parts[i].id == id) return i;
        return -1;
    }

    /// <summary>A part's full explode offset in holder space (model m), for parts the viewer lifts by itself (a lid).</summary>
    public Vector3 HolderOffset(int index, Transform holder)
    {
        if (index < 0 || index >= parts.Count || parts[index].transform == null) return Vector3.zero;
        var p = parts[index];
        return holder.InverseTransformVector(p.transform.parent.TransformVector(p.offset));
    }

    /// <summary>A part's mesh bounds in holder space (skipping the outline layers).</summary>
    static bool HolderBounds(Transform part, Transform holder, out Bounds b)
    {
        b = default;
        bool first = true;
        var toHolder = holder.worldToLocalMatrix;
        foreach (var filter in part.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null || filter.name.StartsWith(LayerPrefix, StringComparison.Ordinal)) continue;
            var m = toHolder * filter.transform.localToWorldMatrix;
            var mb = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                else b.Encapsulate(p);
            }
        }
        return !first;
    }
}
