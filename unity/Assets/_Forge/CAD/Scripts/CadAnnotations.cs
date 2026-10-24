using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What CadViewer draws on and around the model (CAD UX spec, sections 3, 5.1, 5.4, 5.5), as light plus graphite chips:
/// - While exploded: one chip per kind of part ("Fins  ×3", material · mass, "each" for copies; an orange dot if this
///   version changed it), stacked in one column beside the model on the side away from the design panel, at least
///   46 mm apart, ordered by height and centred on their parts. A leader with an elbow 16 mm from the column ends in a
///   ring and dot on the part, and draws on as the part arrives (55% to 90% of its travel), with its chip.
/// - Markers (analysis.markers): CG as a blue ⊕ (on the rocket its collar), CP red, thrust in the CAD hue, the rest
///   white, each with a small tag ("CG 246 mm", "In 3,000 rpm"); between the rocket's CG and CP collars a bracket in
///   the margin's status colour, and on the stage the margin itself ("1.57 cal · static margin").
///   A hovering design's prop discs (from its demo's spin_parts) show with the markers.
/// - Dimensions (analysis.dimensions; with Measure, and on their own for a highlighted parameter): extension lines,
///   the line with open arrowheads, and a value chip ("390 mm" over "Length").
/// Positions come from the hub in the model frame, which is CadViewer's holder space with X flipped. Lines and chips
/// are each one batched mesh; labels keep their angular size; nothing allocates per frame.
/// </summary>
public sealed class CadAnnotations
{
    /// <summary>What to show this frame (CadViewer fills it in).</summary>
    public struct State
    {
        public bool markers, measure, names, stage;
        public string demo;                // the running demo's kind, or null
        public Transform cgCollar, cpCollar;
        public float floorY;               // labels stay above this (the desk top); very low on the stage
    }

    const float ColumnGapMm = 34f, ElbowMm = 16f, ChipPitchMm = 46f, MarkPitchMm = 30f;

    sealed class Callout
    {
        public CadLabel label;
        public bool active;
        public Vector3 anchor;             // where the leader starts (world)
        public float reach;                // the leader starts this far from the anchor, toward the column (a collar's rim)
        public float v, y, alpha, draw;    // height from the centre as seen; placed height; chip alpha; share of leader drawn
        public bool ring;                  // a ring and dot at the anchor (explode)
        public Color leader;
    }

    sealed class Marker
    {
        public string kind;
        public Vector3 local;              // holder space
        public Color color;
        public Callout callout;
    }

    sealed class Dimension
    {
        public string id;
        public Vector3 a, b, normal;       // holder space; normal: the unit offset direction
        public float offset;               // holder units (model m)
        public CadLabel label;
    }

    sealed class Group
    {
        public readonly List<int> members = new List<int>();  // CadExploder part indices
        public Callout callout;
    }

    struct Disc
    {
        public Vector3 origin, axis;       // holder space
        public float radius;               // holder units
    }

    readonly Transform root;
    readonly CadLines lines;
    readonly CadChips chips;
    readonly List<CadLabel> pool = new List<CadLabel>();
    int poolUsed;
    readonly List<Marker> markers = new List<Marker>();
    readonly List<Dimension> dimensions = new List<Dimension>();
    readonly List<Group> groups = new List<Group>();
    readonly List<Disc> discs = new List<Disc>();
    readonly List<Callout> laid = new List<Callout>();
    int[] order = new int[32];
    Transform holder;
    CadExploder exploder;
    Vector3 centreLocal;
    Marker cgMarker, cpMarker;
    Callout margin;
    Color marginColor = Color.white;
    string focusId;
    float markerFade, measureFade, namesFade, focusFade, marginFade;
    int awaySide = -1;
    bool built, shown, labelling;

    // What the meshes were last drawn for: they're rebuilt only when one of these changes (and for a moment after, while
    // labels ease into place), not every frame. The eyes count after 1 cm.
    const float EyeStep = 0.01f, SettleSeconds = 0.75f;
    Matrix4x4 drawnHolder;
    Vector3 drawnEye, drawnParts;
    Vector4 drawnFades = new Vector4(-1f, -1f, -1f, -1f);
    float drawnMargin = -1f, settle;
    int drawnState;
    string drawnDemo, drawnFocus;

    public CadAnnotations(Transform overlayRoot)
    {
        root = overlayRoot;
        lines = new CadLines("CadAnnotations", overlayRoot, CadLook.Light(overlay: true));
        chips = new CadChips("CadAnnotationChips", overlayRoot);
    }

    /// <summary>Read a newly shown version: markers, dimensions, part kinds, prop discs (CadViewer, after the exploder).</summary>
    public void Build(CadModel model, CadModelUpdated info, CadExploder parts, Transform holderTransform, Bounds assembled)
    {
        drawnFades.x = -1f;  // draw the new version on the next Tick
        holder = holderTransform;
        exploder = parts;
        centreLocal = assembled.center;
        for (int i = 0; i < poolUsed; i++) pool[i].Hide();
        poolUsed = 0;
        markers.Clear();
        dimensions.Clear();
        groups.Clear();
        discs.Clear();
        cgMarker = cpMarker = null;
        margin = null;
        built = model != null && holder != null && parts != null;
        if (!built)
        {
            HideAll();
            return;
        }
        var analysis = info?.analysis;
        var values = analysis?.demo?.values;

        if (analysis?.markerList != null)
        {
            foreach (var m in analysis.markerList)
            {
                if (m == null || m.pos == null || m.pos.Length < 3) continue;
                var label = Take(CadLabel.Style.Mark);
                label.SetText(MarkerTitle(m, analysis, values));
                var marker = new Marker
                {
                    kind = m.kind ?? "",
                    local = CadLook.Flip(m.pos, Vector3.zero),
                    color = CadLook.MarkerColor(m.kind),
                    callout = new Callout { label = label, leader = Color.white },
                };
                markers.Add(marker);
                if (marker.kind == "cg" && cgMarker == null) cgMarker = marker;
                if (marker.kind == "cp" && cpMarker == null) cpMarker = marker;
            }
        }
        var primary = Primary(analysis);
        if (cgMarker != null && cpMarker != null && primary != null)
        {
            var label = Take(CadLabel.Style.Mark);
            label.SetText("<color=" + CadLook.StatusText(primary.status) + ">" + primary.display + "</color>",
                string.IsNullOrEmpty(primary.label) ? null : primary.label.ToLowerInvariant());
            marginColor = CadLook.StatusColor(primary.status);
            margin = new Callout { label = label, leader = marginColor };
        }

        if (analysis?.dimensions != null)
        {
            foreach (var d in analysis.dimensions)
            {
                if (d == null || d.fromPos == null || d.toPos == null) continue;
                Vector3 a = CadLook.Flip(d.fromPos, Vector3.zero), b = CadLook.Flip(d.toPos, Vector3.zero);
                Vector3 n = CadLook.Flip(d.offsetDir, Vector3.zero);
                if (n.sqrMagnitude < 1e-8f) n = Vector3.Cross(b - a, Vector3.up);
                if (n.sqrMagnitude < 1e-8f) n = Vector3.forward;
                var label = Take(CadLabel.Style.Dim);
                label.SetAnchor(0);
                label.SetText(CadLook.Millimetres(d.valueMm > 0f ? d.valueMm : (b - a).magnitude * 1000f),
                    string.IsNullOrEmpty(d.label) ? CadLook.Pretty(d.id) : d.label);
                dimensions.Add(new Dimension { id = d.id, a = a, b = b, normal = n.normalized, offset = (d.offsetMm ?? 0f) / 1000f, label = label });
            }
        }

        // One chip per kind of part (copies share one), in the order the parts come. Copies are numbered names
        // ("Fin 1"), or names that start alike in the same material and mass ("Bearing in front", "Bearing out back").
        var changed = info?.changedIds != null ? new HashSet<string>(info.changedIds) : null;
        var keyCounts = new Dictionary<string, int>();
        for (int i = 0; i < parts.Count; i++)
        {
            string key = KindKey(parts[i]);
            keyCounts.TryGetValue(key, out int n);
            keyCounts[key] = n + 1;
        }
        var kinds = new string[parts.Count];
        var distinct = new HashSet<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            string kind = KindKey(parts[i]);
            // Alike names merge only as a set of three or more (bearings), not a pair that merely matches (housing halves).
            if (kind.IndexOf('|') >= 0 && keyCounts[kind] < 3) kind = parts[i].name;
            kinds[i] = kind;
            distinct.Add(kind);
        }
        // Too many kinds for one readable column (the humanoid has about 50): one chip per mass group instead
        // ("Legs  ×37", its mass), pointing at the group's part nearest the eyes.
        if (distinct.Count > MaxNameChips && MassGroups(parts, analysis?.massBreakdown, changed)) { }
        else BuildKinds(parts, kinds, changed);
        BuildDiscs(model, analysis);
        int needed = markers.Count + groups.Count + 2;
        if (order.Length < needed) order = new int[needed + 8];
    }

    const int MaxNameChips = 12;

    bool MassGroups(CadExploder parts, List<CadMassItem> rows, HashSet<string> changed)
    {
        if (rows == null) return false;
        var index = new Dictionary<string, int>();
        for (int i = 0; i < parts.Count; i++) index[parts[i].id] = i;
        foreach (var row in rows)
        {
            if (row?.partIds == null || row.partIds.Count == 0) continue;
            var group = new Group { callout = new Callout { label = Take(CadLabel.Style.Chip), leader = Color.white, ring = true } };
            bool isNew = false;
            foreach (var id in row.partIds)
                if (id != null && index.TryGetValue(id, out int i))
                {
                    group.members.Add(i);
                    if (changed != null && changed.Contains(id)) isNew = true;
                }
            if (group.members.Count == 0) continue;
            int count = group.members.Count;
            string title = (isNew ? "<color=#FF8A3D>•</color> " : "") + (row.label ?? CadLook.Pretty(row.id)) + (count > 1 ? "  ×" + count : "");
            group.callout.label.SetText(title, CadLook.Grams(row.massG) + "  ·  " + Mathf.RoundToInt(row.share * 100f) + "% of the mass");
            groups.Add(group);
        }
        return groups.Count > 0;
    }

    void BuildKinds(CadExploder parts, string[] kinds, HashSet<string> changed)
    {
        var byKind = new Dictionary<string, Group>();
        for (int i = 0; i < parts.Count; i++)
        {
            string kind = kinds[i];
            if (!byKind.TryGetValue(kind, out var group))
            {
                group = new Group { callout = new Callout { label = Take(CadLabel.Style.Chip), leader = Color.white, ring = true } };
                byKind.Add(kind, group);
                groups.Add(group);
            }
            group.members.Add(i);
        }
        foreach (var group in groups)
        {
            var first = parts[group.members[0]];
            int count = group.members.Count;
            bool isNew = false;
            if (changed != null)
                foreach (int m in group.members)
                    if (changed.Contains(parts[m].id)) isNew = true;
            string title = (isNew ? "<color=#FF8A3D>•</color> " : "") +
                           (count > 1 ? CadLook.Plural(CommonName(group)) + "  ×" + count : CadLook.Pretty(first.name));
            string sub = first.material ?? "";
            if (first.massG >= 0f) sub += (sub.Length > 0 ? "  ·  " : "") + CadLook.Grams(first.massG) + (count > 1 ? " each" : "");
            group.callout.label.SetText(title, sub.Length > 0 ? sub : null);
        }
    }

    void BuildDiscs(CadModel model, CadAnalysis analysis)
    {
        // A hovering design's prop discs, from its demo.
        var demo = analysis?.demo;
        if (demo != null && demo.kind == CadDemoPlayer.Hover && demo.spinParts != null)
        {
            foreach (var s in demo.spinParts)
            {
                if (s == null || string.IsNullOrEmpty(s.id) || !model.objects.TryGetValue(s.id, out var go) || go == null) continue;
                Vector3 o = CadLook.Flip(s.origin, Vector3.zero), axis = CadLook.Flip(s.dir, Vector3.up);
                if (axis.sqrMagnitude < 1e-8f) continue;
                float radius = s.radiusMm.HasValue && s.radiusMm.Value > 0f
                    ? s.radiusMm.Value / 1000f
                    : CadLook.PartRadius(go.transform, holder, o, axis);
                if (radius > 0f) discs.Add(new Disc { origin = o, axis = axis.normalized, radius = radius });
            }
        }
    }

    /// <summary>Older callers: no changed-part dots.</summary>
    public void Build(CadModel model, CadAnalysis analysis, CadExploder parts, Transform holderTransform, Bounds assembled) =>
        Build(model, new CadModelUpdated { analysis = analysis }, parts, holderTransform, assembled);

    /// <summary>Draw one dimension (by its id in analysis.dimensions) even while Measure is off; null clears it.</summary>
    public void FocusDimension(string id) => focusId = string.IsNullOrEmpty(id) ? null : id;

    /// <summary>Older callers.</summary>
    public void Tick(float dt, Vector3 eye, bool showMarkers, bool showMeasure, bool showNames, Transform cgCollar, Transform cpCollar) =>
        Tick(dt, eye, new State { markers = showMarkers, measure = showMeasure, names = showNames, cgCollar = cgCollar, cpCollar = cpCollar, floorY = -1000f });

    // Copies share a key: the numbered base name, or the first word with the material and mass.
    static string KindKey(CadExploder.Part part)
    {
        string kind = part.kind ?? part.id;
        if (kind != part.name) return kind;  // "Fin 1" -> "Fin"
        string name = CadLook.Pretty(part.name);
        int space = name.IndexOf(' ');
        string first = space > 0 ? name.Substring(0, space) : name;
        return first + "|" + (part.material ?? "") + "|" + Mathf.Round(part.massG * 10f);
    }

    // The words a group's names start with ("Bearing", "Centering ring"), else the first part's kind.
    string CommonName(Group group)
    {
        string common = CadLook.Pretty(exploder[group.members[0]].kind ?? exploder[group.members[0]].name);
        if (exploder[group.members[0]].kind != exploder[group.members[0]].name) return common;
        string[] words = CadLook.Pretty(exploder[group.members[0]].name).Split(' ');
        int keep = words.Length;
        foreach (int m in group.members)
        {
            string[] other = CadLook.Pretty(exploder[m].name).Split(' ');
            int k = 0;
            while (k < keep && k < other.Length && other[k] == words[k]) k++;
            keep = k;
        }
        return keep > 0 ? string.Join(" ", words, 0, keep) : common;
    }

    /// <summary>Draw this frame (CadViewer.LateUpdate, after anything that moves parts). Each kind fades in and out by itself.</summary>
    public void Tick(float dt, Vector3 eye, State s)
    {
        bool moving = s.demo == CadDemoPlayer.Launch || s.demo == CadDemoPlayer.Hover || s.demo == CadDemoPlayer.Walk;
        bool markersOn = s.markers && !moving && (markers.Count > 0 || discs.Count > 0);
        markerFade = Mathf.MoveTowards(markerFade, markersOn ? 1f : 0f, dt / 0.25f);
        measureFade = Mathf.MoveTowards(measureFade, s.measure && dimensions.Count > 0 ? 1f : 0f, dt / 0.25f);
        focusFade = Mathf.MoveTowards(focusFade, focusId != null ? 1f : 0f, dt / 0.15f);
        namesFade = Mathf.MoveTowards(namesFade, s.names && groups.Count > 0 ? 1f : 0f, dt / (s.names ? 0.2f : 0.12f));
        marginFade = Mathf.MoveTowards(marginFade, markersOn && s.stage && margin != null ? 1f : 0f, dt / 0.3f);
        if (!built || holder == null || (markerFade <= 0f && measureFade <= 0f && namesFade <= 0f && focusFade <= 0f))
        {
            if (shown) HideAll();
            labelling = false;
            return;
        }
        shown = true;
        // Nothing it shows has moved since the last draw (and labels have settled): keep the meshes as they are.
        if (Unchanged(eye, s))
        {
            if (settle <= 0f) return;
            settle -= dt;
        }
        else settle = SettleSeconds;

        // The model's centre and the eyes' view of it: label columns stand beside it, in the plane through it.
        Vector3 centre = holder.TransformPoint(centreLocal);
        Vector3 forward = centre - eye;
        float distance = forward.magnitude;
        forward = distance > 1e-4f ? forward / distance : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
        right.Normalize();
        Vector3 up = Vector3.Cross(forward, right);
        float k = Mathf.Clamp(distance / CadLook.ReferenceDistance, 0.4f, 3f);  // spacing keeps its angular size, like the labels
        bool labels = markerFade > 0f || namesFade > 0f;
        if (labels && !labelling) awaySide = AwaySide(centre, right);  // decided when labels come on
        labelling = labels;
        Silhouette(centre, right, out float halfLeft, out float halfRight);
        float half = awaySide > 0 ? halfRight : halfLeft;
        float scale = holder.lossyScale.x;

        lines.Begin(eye);
        chips.Begin();
        for (int i = 0; i < markers.Count; i++) markers[i].callout.active = false;
        for (int i = 0; i < groups.Count; i++) groups[i].callout.active = false;
        if (margin != null) margin.active = false;

        if (markerFade > 0f)
        {
            laid.Clear();
            AddMarkers(centre, right, up, s, k);
            DrawDiscs(scale);
            Column(awaySide, centre, right, up, half, MarkPitchMm * 0.001f * k, s.floorY, eye, dt, k);
        }
        if (namesFade > 0f)
        {
            laid.Clear();
            AddNames(centre, right, up, k);
            Column(awaySide, centre, right, up, half, ChipPitchMm * 0.001f * k, s.floorY, eye, dt, k);
        }
        for (int i = 0; i < markers.Count; i++)
            if (!markers[i].callout.active) markers[i].callout.label.Hide();
        for (int i = 0; i < groups.Count; i++)
            if (!groups[i].callout.active) groups[i].callout.label.Hide();
        if (margin != null && !margin.active) margin.label.Hide();

        for (int i = 0; i < dimensions.Count; i++)
        {
            var d = dimensions[i];
            float a = Mathf.Max(measureFade, focusId != null && d.id == focusId ? focusFade : 0f);
            if (a > 0f) DrawDimension(d, a, eye, dt, scale, k);
            else d.label.Hide();
        }
        lines.End();
        chips.End();
    }

    // True when the eyes (within 1 cm), the model, its parts, the fades and the state are as they were last drawn;
    // otherwise remembers them and says so.
    bool Unchanged(Vector3 eye, State s)
    {
        var fades = new Vector4(markerFade, measureFade, namesFade, focusFade);
        var m = holder.localToWorldMatrix;
        Vector3 parts = Vector3.zero;
        if (exploder != null)
            for (int i = 0; i < exploder.Count; i++)
            {
                var t = exploder[i].transform;
                if (t != null) parts += t.position;
            }
        if (s.cgCollar != null) parts += s.cgCollar.position;
        if (s.cpCollar != null) parts += s.cpCollar.position * 2f;
        int state = (s.markers ? 1 : 0) | (s.measure ? 2 : 0) | (s.names ? 4 : 0) | (s.stage ? 8 : 0) |
                    (s.cgCollar != null ? 16 : 0) | (s.cpCollar != null ? 32 : 0) | (labelling ? 64 : 0);
        bool same = fades == drawnFades && Mathf.Approximately(marginFade, drawnMargin) && state == drawnState &&
                    s.demo == drawnDemo && focusId == drawnFocus && (eye - drawnEye).sqrMagnitude <= EyeStep * EyeStep &&
                    (parts - drawnParts).sqrMagnitude < 1e-10f && Mathf.Approximately(s.floorY, drawnFloor) && SameMatrix(m, drawnHolder);
        if (same) return true;
        drawnFades = fades;
        drawnMargin = marginFade;
        drawnState = state;
        drawnDemo = s.demo;
        drawnFocus = focusId;
        drawnEye = eye;
        drawnParts = parts;
        drawnFloor = s.floorY;
        drawnHolder = m;
        return false;
    }

    float drawnFloor;

    static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++)
            if (Mathf.Abs(a[i] - b[i]) > 1e-6f) return false;
        return true;
    }

    public void HideAll()
    {
        shown = false;
        drawnFades.x = -1f;  // shown again: draw at once
        lines.Hide();
        chips.Hide();
        for (int i = 0; i < poolUsed; i++) pool[i].Hide();
    }

    /// <summary>Snap everything out (the viewer was disabled): nothing fades back from where it was.</summary>
    public void Hide()
    {
        markerFade = measureFade = namesFade = focusFade = marginFade = 0f;
        labelling = false;
        HideAll();
    }

    public void Dispose()
    {
        lines.Destroy();
        chips.Destroy();
        foreach (var label in pool) label.Destroy();
        pool.Clear();
        poolUsed = 0;
    }

    // ---- Content ----

    static string MarkerTitle(CadMarker m, CadAnalysis analysis, Dictionary<string, float> values)
    {
        string kind = m.kind ?? "";
        string name = string.IsNullOrEmpty(m.label) ? CadLook.Pretty(m.id) : m.label;
        if (kind == "cg" || kind == "cp")
        {
            var metric = analysis?.metrics?.Find(x => x != null && x.id == kind);  // the rocket's "CG from tip"
            string text = metric != null ? name + " " + metric.display : name;
            return "<color=" + (kind == "cg" ? "#9CC6FF" : "#FF9A90") + ">" + text + "</color>";
        }
        if (values != null && kind == "input" && values.TryGetValue("input_rpm", out float inRpm))
            return "In " + CadLook.Thousands(inRpm) + " rpm";
        if (values != null && kind == "output" && values.TryGetValue("output_rpm", out float outRpm))
            return "Out " + CadLook.Thousands(outRpm) + " rpm";
        return name;
    }

    // The design's primary metric: the first with a range (the rocket's static margin).
    static CadMetric Primary(CadAnalysis analysis)
    {
        if (analysis?.metrics == null) return null;
        foreach (var m in analysis.metrics)
            if (m != null && m.IsGauge) return m;
        return null;
    }

    // The side away from the design panel (from the eyes); left when there's no panel to go by.
    int AwaySide(Vector3 centre, Vector3 right)
    {
        foreach (var p in Object.FindObjectsByType<ForgePanel>())
            if (p != null && p.isActiveAndEnabled && p.panelId == "cad-params")
                return Vector3.Dot(p.transform.position - centre, right) > 0f ? -1 : 1;
        return -1;
    }

    // How far the parts reach left and right of the centre as seen from the eyes (m).
    void Silhouette(Vector3 centre, Vector3 right, out float left, out float rightReach)
    {
        left = rightReach = 0f;
        if (exploder == null) return;
        Vector3 r = holder.InverseTransformDirection(right);
        float scale = holder.lossyScale.x;
        for (int i = 0; i < exploder.Count; i++)
        {
            var p = exploder[i];
            if (p.transform == null) continue;
            float u = Vector3.Dot(p.transform.TransformPoint(p.centre) - centre, right);
            float across = (Mathf.Abs(p.extents.x * r.x) + Mathf.Abs(p.extents.y * r.y) + Mathf.Abs(p.extents.z * r.z)) * scale;
            rightReach = Mathf.Max(rightReach, u + across);
            left = Mathf.Max(left, across - u);
        }
    }

    void AddMarkers(Vector3 centre, Vector3 right, Vector3 up, State s, float k)
    {
        bool spinning = s.demo == CadDemoPlayer.Spin;  // the Run demo tags its own shaft ends
        Transform cgCollar = s.cgCollar != null && s.cgCollar.gameObject.activeInHierarchy ? s.cgCollar : null;
        Transform cpCollar = s.cpCollar != null && s.cpCollar.gameObject.activeInHierarchy ? s.cpCollar : null;
        for (int i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            if (spinning && (m.kind == "input" || m.kind == "output")) continue;
            var c = m.callout;
            Transform collar = m.kind == "cg" ? cgCollar : m.kind == "cp" ? cpCollar : null;
            Vector3 p = collar != null ? collar.position : holder.TransformPoint(m.local);
            c.anchor = p;
            c.reach = collar != null ? collar.lossyScale.x * 0.5f : 0.0045f * k;
            c.alpha = markerFade;
            c.draw = 1f;
            Enlist(c, centre, up);
            if (collar != null) continue;
            float r = 0.0045f * k;
            Color col = CadLook.Alpha(m.color, 0.95f * markerFade);
            lines.Ring(p, r, CadLook.ThinLine, col);
            if (m.kind == "cg")
            {
                // The CG symbol: a circle with a cross through it.
                lines.Segment(p - right * r, p + right * r, CadLook.ThinLine, col);
                lines.Segment(p - up * r, p + up * r, CadLook.ThinLine, col);
            }
            else lines.Dot(p, 0.004f * k, col);
        }
        // The rocket's margin: a bracket from the CG collar to the CP collar, beside the body on the label side.
        if (cgCollar != null && cpCollar != null && cgMarker != null && cpMarker != null)
        {
            float gap = 0.006f * k, tick = 0.003f * k;
            Vector3 a = cgCollar.position + right * (awaySide * (cgCollar.lossyScale.x * 0.5f + gap));
            Vector3 b = cpCollar.position + right * (awaySide * (cpCollar.lossyScale.x * 0.5f + gap));
            Color col = CadLook.Alpha(marginColor, 0.9f * markerFade);
            lines.Segment(a, b, CadLook.ThinLine, col);
            lines.Segment(a, a - right * (awaySide * tick), CadLook.ThinLine, col);
            lines.Segment(b, b - right * (awaySide * tick), CadLook.ThinLine, col);
            if (margin != null && marginFade > 0f)
            {
                margin.anchor = (a + b) * 0.5f;
                margin.reach = 0f;
                margin.alpha = marginFade;
                margin.draw = 1f;
                Enlist(margin, centre, up);
            }
        }
    }

    void DrawDiscs(float scale)
    {
        if (discs.Count == 0) return;
        Color fill = CadLook.Alpha(Color.white, 0.06f * markerFade), rim = CadLook.Alpha(Color.white, 0.55f * markerFade);
        for (int i = 0; i < discs.Count; i++)
        {
            var d = discs[i];
            Vector3 c = holder.TransformPoint(d.origin), axis = holder.TransformDirection(d.axis);
            float r = d.radius * scale;
            lines.Disc(c, axis, r, fill);
            lines.Circle(c, axis, r, CadLook.ThinLine, rim, 40);
        }
    }

    // Each kind's chip points at the copy nearest the eyes, at that part's edge toward the column.
    void AddNames(Vector3 centre, Vector3 right, Vector3 up, float k)
    {
        Vector3 eyeward = -Vector3.Cross(right, up);  // toward the eyes
        Vector3 r = holder.InverseTransformDirection(right);
        float scale = holder.lossyScale.x;
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var c = group.callout;
            int best = -1;
            float bestScore = float.MinValue, value = 0f;
            Vector3 bestPoint = default;
            for (int j = 0; j < group.members.Count; j++)
            {
                var p = exploder[group.members[j]];
                if (p.detached || p.transform == null) continue;
                Vector3 point = p.transform.TransformPoint(p.centre);
                float score = Vector3.Dot(point - centre, eyeward);
                if (score <= bestScore) continue;
                bestScore = score;
                best = group.members[j];
                bestPoint = point;
                value = p.value;
            }
            if (best < 0) continue;  // every copy is off in the user's hands
            var part = exploder[best];
            float across = (Mathf.Abs(part.extents.x * r.x) + Mathf.Abs(part.extents.y * r.y) + Mathf.Abs(part.extents.z * r.z)) * scale;
            c.anchor = bestPoint + right * (awaySide * across * 0.7f);
            c.reach = 0f;
            // It appears as its part arrives: 55% to 90% of the way out.
            float arrived = Mathf.Clamp01((value - 0.55f) / 0.35f);
            c.draw = arrived;
            c.alpha = arrived * namesFade;
            if (c.alpha <= 0.003f) continue;
            Enlist(c, centre, up);
        }
    }

    void Enlist(Callout c, Vector3 centre, Vector3 up)
    {
        c.v = Vector3.Dot(c.anchor - centre, up);
        c.active = true;
        laid.Add(c);
    }

    // One column on `side`: sorted by height, `pitch` apart, centred on what they point at, above the floor; then the
    // chips and their leaders (anchor, elbow, chip edge).
    void Column(int side, Vector3 centre, Vector3 right, Vector3 up, float half, float pitch, float floorY, Vector3 eye, float dt, float k)
    {
        int n = laid.Count;
        if (n == 0) return;
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = 1; i < n; i++)  // top first
        {
            int key = order[i];
            float vk = laid[key].v;
            int j = i - 1;
            while (j >= 0 && laid[order[j]].v < vk)
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }
        float previous = float.MaxValue, wanted = 0f, placed = 0f;
        for (int i = 0; i < n; i++)
        {
            var c = laid[order[i]];
            c.y = Mathf.Min(c.v, previous - pitch);
            previous = c.y;
            wanted += c.v;
            placed += c.y;
        }
        float shift = (wanted - placed) / n;  // keep the column centred on what it points at
        float lowest = laid[order[n - 1]].y + shift;
        float floorV = (floorY - centre.y) / Mathf.Max(up.y, 0.3f) + pitch * 0.4f;
        if (lowest < floorV) shift += floorV - lowest;  // and off the desk
        float x = side * (half + ColumnGapMm * 0.001f * k);
        float elbow = ElbowMm * 0.001f * k;
        for (int i = 0; i < n; i++)
        {
            var c = laid[order[i]];
            c.y += shift;
            c.label.SetAnchor(side);
            c.label.MoveTo(centre + right * x + up * c.y, eye, dt);
            c.label.SetAlpha(c.alpha);
            c.label.Draw(chips);
            Leader(c, c.label.Edge(-side), right * side, elbow, k);
        }
    }

    // Anchor to an elbow short of the column, then level into the chip; drawn on by `draw`.
    void Leader(Callout c, Vector3 end, Vector3 outward, float elbowLength, float k)
    {
        if (c.alpha <= 0.003f) return;
        Vector3 start = c.anchor + outward * c.reach;
        Vector3 elbow = end - outward * elbowLength;
        float first = Vector3.Distance(start, elbow), second = Vector3.Distance(elbow, end), total = first + second;
        float drawn = total * Mathf.Clamp01(c.draw);
        Color line = CadLook.Alpha(c.leader, 0.9f * c.alpha);
        float width = 0.0032f * k;
        if (drawn <= first) lines.Segment(start, Vector3.Lerp(start, elbow, first > 1e-6f ? drawn / first : 1f), width, line);
        else
        {
            lines.Segment(start, elbow, width, line);
            lines.Segment(elbow, Vector3.Lerp(elbow, end, second > 1e-6f ? (drawn - first) / second : 1f), width, line);
        }
        if (c.ring)
        {
            lines.Ring(c.anchor, 0.0036f * k, CadLook.ThinLine, line);
            lines.Dot(c.anchor, 0.004f * k, line);
        }
    }

    void DrawDimension(Dimension d, float a, Vector3 eye, float dt, float scale, float k)
    {
        Vector3 from = holder.TransformPoint(d.a), to = holder.TransformPoint(d.b);
        Vector3 n = holder.TransformDirection(d.normal);
        float offset = d.offset * scale;
        Vector3 from2 = from + n * offset, to2 = to + n * offset;
        Color main = CadLook.Alpha(Color.white, 0.92f * a), faint = CadLook.Alpha(Color.white, 0.45f * a);
        if (offset > 0.004f)
        {
            // Extension lines: a small gap off the model, a little past the dimension line.
            float gap = Mathf.Min(0.003f, offset * 0.25f);
            lines.Segment(from + n * gap, from2 + n * 0.003f, CadLook.ThinLine, faint);
            lines.Segment(to + n * gap, to2 + n * 0.003f, CadLook.ThinLine, faint);
        }
        lines.Segment(from2, to2, CadLook.Line, main);
        Vector3 along = to2 - from2;
        if (along.sqrMagnitude > 1e-10f)
        {
            // Open arrowheads, pointing out at the extension lines.
            along.Normalize();
            float length = 0.0045f * k, spread = 0.0022f * k;
            lines.Segment(from2, from2 + along * length + n * spread, CadLook.ThinLine, main);
            lines.Segment(from2, from2 + along * length - n * spread, CadLook.ThinLine, main);
            lines.Segment(to2, to2 - along * length + n * spread, CadLook.ThinLine, main);
            lines.Segment(to2, to2 - along * length - n * spread, CadLook.ThinLine, main);
        }
        d.label.SetAnchor(0);
        d.label.MoveTo((from2 + to2) * 0.5f + n * (0.004f * k + d.label.Height * 0.5f), eye, dt, snap: true);
        d.label.SetAlpha(a);
        d.label.Draw(chips);
    }

    CadLabel Take(CadLabel.Style style)
    {
        CadLabel label;
        if (poolUsed < pool.Count) label = pool[poolUsed];
        else
        {
            label = new CadLabel(root, "CadLabel", style);
            pool.Add(label);
        }
        poolUsed++;
        label.SetStyle(style);
        label.SetAnchor(0);
        label.SetTint(Color.white);
        label.Hide();
        return label;
    }
}
