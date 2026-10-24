using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Parameters (CAD UX spec 2.5): presets and Reset all, then one section per group in the hub's `groups` order. Each
/// parameter is a 44 mm row: its label (an orange dot once changed), − / + steppers (the hub's step; a 0.4 s debounce
/// when tapped repeatedly), a slider whose whole 32 mm track takes the pointer (a tick at the default, an orange ring
/// at the old value after a change; the value shows live while dragging and commits on release), and value / unit /
/// "was 300". Options and small integer ranges (4 choices or fewer) are segmented pills. Fixed inputs
/// (analysis.constants) are info rows. Pointing at a row lights its parts and dimension on the model
/// (CadViewer.HighlightParam) and, after a moment, shows its help under it. Offline: one quiet line at the top; while
/// a demo runs the rows dim to 45% and pause.
/// </summary>
public sealed class CadParamsTab : CadPanelTab
{
    const float RowH = 44f, LabelW = 112f, StepW = 28f, ValueW = 74f, ColGap = 7f, PadX = 4f, TrackH = 32f;
    const float PresetH = 32f, Gap = 8f, HeadingH = 16f, NoteH = 30f, Debounce = 0.4f, HelpDelay = 0.35f;
    // A row stays lit this long after the pointer leaves its last hit area: crossing from its background to a stepper
    // or the track is an exit then an enter, and must not flicker the row, its help or the model's highlight.
    const float HoverGrace = 0.12f;

    sealed class Row
    {
        public CadParam param;
        public string name;
        public RectTransform root;
        public ForgeSurface bg, changedDot;
        public TextMeshProUGUI label;
        public bool isSlider, isSegments, isInfo, integer, changed, hot, cooling;
        public int hovers;
        public float coolTimer;
        public float y, height = RowH;
        // Slider rows.
        public CadButton minus, plus;
        public Slider slider;
        public CadSliderRelease grip;  // pressed / let go: a refresh leaves a held knob alone
        public RectTransform knob;
        public ForgeSurface tick, ghostRing;
        public TextMeshProUGUI value, unit, was;
        public CadLiveText live;
        public double step, shown, committed;
        public float debounce;  // > 0: stepper taps waiting to be sent
        // Segment rows.
        public RectTransform segments;
        public readonly List<CadButton> segs = new List<CadButton>();
        public readonly List<RectTransform> segRects = new List<RectTransform>();
        public readonly List<object> segValues = new List<object>();
        // Info rows.
        public TextMeshProUGUI info, note;
    }

    readonly RectTransform lockable;
    readonly CanvasGroup lockGroup;
    readonly RectTransform mockNote, lockNote;
    readonly TextMeshProUGUI mockText;
    readonly TextMeshProUGUI presetsHeading;
    readonly CadButton resetAll;
    readonly List<CadButton> presets = new List<CadButton>();
    readonly List<RectTransform> presetRects = new List<RectTransform>();
    readonly List<Row> rows = new List<Row>();
    readonly List<TextMeshProUGUI> headings = new List<TextMeshProUGUI>();
    readonly List<string> headingGroups = new List<string>();
    readonly RectTransform help;
    readonly TextMeshProUGUI helpText;
    string schema, presetKey;
    Row helpRow, hotRow;
    float helpTimer, width = 444f;
    bool locked;

    public CadParamsTab(RectTransform parent, CadParamsPanel owner) : base(parent, "Parameters", owner)
    {
        mockNote = Note(Root, "Offline", true, out mockText);
        lockNote = Note(Root, "Paused", false, out var lockText);
        lockText.text = "Editing pauses while the demo runs.";
        lockable = CadUi.Rect(Root, "Rows");
        CadUi.Stretch(lockable, 0f, 0f, 0f, 0f);
        lockGroup = CadUi.Group(lockable.gameObject);
        presetsHeading = Heading(lockable, "Presets");
        resetAll = CadButton.Create(lockable, "ResetAll", CadButton.Ghost, 8f, () => owner.ResetAll());
        resetAll.AddLabel("Reset all", 12f);

        var helpFill = CadUi.Surface(Root, "Help", CadUi.MenuFill, 10f, 0.09f);
        help = helpFill.rectTransform;
        helpText = CadUi.Text(help, "Text", CadUi.Small, CadUi.White(0.75f), wrap: true);
        helpText.alignment = TextAlignmentOptions.TopLeft;
        CadUi.LineHeight(helpText, 1.35f);
        help.gameObject.SetActive(false);
    }

    // A quiet line in a 4% box; the offline one has the amber hub dot.
    static RectTransform Note(RectTransform parent, string name, bool dot, out TextMeshProUGUI text)
    {
        var fill = CadUi.Surface(parent, name, CadUi.White(0.04f), 10f);
        if (dot)
        {
            var d = CadUi.Pill(fill.transform, "Dot", CadUi.HubOff, glow: 0.45f);
            CadUi.Place(d.rectTransform, 10f, (NoteH - 7f) * 0.5f, 7f, 7f);
        }
        text = CadUi.Text(fill.transform, "Text", 12f, CadUi.White(0.6f));
        CadUi.Band(text.rectTransform, 0f, NoteH, dot ? 25f : 10f, 10f);
        return fill.rectTransform;
    }

    public override void Show(CadPanelState s)
    {
        var p = s.p;
        if (p == null) return;
        string key = SchemaKey(p);
        if (key != schema)
        {
            schema = key;
            Rebuild(p);
        }
        BuildPresets(p);
        mockText.text = "Offline: changes jump to the nearest of " + Mathf.Max(1, s.versions.Count) + " stored versions.";
        CadUi.Show(mockNote, s.mock);
        locked = s.demoRunning;
        CadUi.Show(lockNote, locked);
        lockGroup.alpha = locked ? 0.45f : 1f;
        lockGroup.interactable = !locked;
        lockGroup.blocksRaycasts = !locked;
        if (locked) ClearHot();

        foreach (var r in rows)
        {
            if (r.isInfo) continue;
            var q = p.Find(r.name);
            if (q == null) continue;
            r.param = q;
            bool pending = s.building && s.pendingParam == r.name;
            object parentValue = null;
            r.changed = s.parentParams != null && s.parentParams.TryGetValue(r.name, out parentValue) && !CadOverviewTab.Same(parentValue, q.value);
            CadUi.Show(r.changedDot, r.changed);
            if (r.isSegments)
            {
                object now = pending ? s.pendingValue : q.value;
                for (int i = 0; i < r.segs.Count; i++) r.segs[i].On = CadOverviewTab.Same(r.segValues[i], now);
                continue;
            }
            if (r.debounce > 0f || (r.grip != null && r.grip.Held)) continue;  // the user's taps or drag win until sent
            double v = pending && s.pendingValue != null ? ToDouble(s.pendingValue, q.Number) : q.Number;
            r.committed = q.Number;
            SetShown(r, v);
            r.value.color = pending ? CadUi.Hue : r.changed ? CadUi.PeachLight : Color.white;
            r.unit.text = CadUi.Clean(q.unit ?? "");
            r.was.text = r.changed ? "was " + CadUi.ParamValue(q, parentValue) : "";
            CadUi.Show(r.was, r.changed);
            PlaceMark(r.ghostRing.rectTransform, r.changed ? Norm(r, ToDouble(parentValue, q.Number)) : -1f, 12f);
            PlaceMark(r.tick.rectTransform, q.HasDefault ? Norm(r, q.DefaultNumber) : -1f, 12f, 2f);
        }
        ShowInfo(s);
    }

    static string SchemaKey(CadParams p)
    {
        var b = new System.Text.StringBuilder(p.part ?? "");
        foreach (var q in p.parameters)
            b.Append('|').Append(q.name).Append(q.IsOptions ? "o" : "n").Append(q.min).Append(':').Append(q.max).Append(':').Append(q.step);
        if (p.groups != null) foreach (var g in p.groups) b.Append('#').Append(g);
        return b.ToString();
    }

    // ---- Building ----

    void Rebuild(CadParams p)
    {
        ClearHot();
        foreach (var r in rows) Discard(r.root.gameObject);
        rows.Clear();
        foreach (var h in headings) Discard(h.gameObject);
        headings.Clear();
        headingGroups.Clear();

        // Sections in the hub's group order; any group it didn't list goes after, in parameter order.
        var order = new List<string>(p.groups ?? new List<string>());
        foreach (var q in p.parameters)
        {
            string g = string.IsNullOrEmpty(q.group) ? "General" : q.group;
            if (!order.Contains(g)) order.Add(g);
        }
        foreach (var g in order)
        {
            bool any = false;
            foreach (var q in p.parameters)
            {
                if ((string.IsNullOrEmpty(q.group) ? "General" : q.group) != g) continue;
                if (!any)
                {
                    var h = Heading(lockable, g);
                    headings.Add(h);
                    headingGroups.Add(g);
                    any = true;
                }
                rows.Add(BuildRow(q));
            }
        }
        // Fixed inputs: built on Show (they come with the analysis, not the schema).
        var fixedHeading = Heading(lockable, "Fixed inputs");
        headings.Add(fixedHeading);
        headingGroups.Add(null);
        help.SetAsLastSibling();
    }

    Row BuildRow(CadParam q)
    {
        var r = new Row { param = q, name = q.name };
        r.root = CadUi.Rect(lockable, "Row " + q.name);
        r.bg = CadUi.Surface(r.root, "Back", CadUi.White(0f), 12f);
        r.bg.raycastTarget = true;
        CadUi.Stretch(r.bg.rectTransform, 0f, 0f, 0f, 0f);
        Hover(r, r.bg.gameObject);
        r.changedDot = CadUi.Pill(r.root, "Changed", CadUi.Hue, glow: 0.5f);
        CadUi.Place(r.changedDot.rectTransform, PadX, (RowH - 6f) * 0.5f, 6f, 6f);
        r.label = CadUi.Text(r.root, "Label", CadUi.Body, CadUi.White(0.8f));
        r.label.text = CadUi.Clean(q.label ?? CadLook.Pretty(q.name));

        int choices = q.IsOptions ? q.options.Count
            : q.step.HasValue && q.step.Value > 0 && q.min.HasValue && q.max.HasValue ? (int)Math.Round((q.max.Value - q.min.Value) / q.step.Value) + 1 : 99;
        bool integral = q.value is long || q.value is int;
        r.integer = integral;
        if (q.IsOptions || (integral && choices <= 4)) BuildSegments(r, q, choices);
        else BuildSlider(r, q);
        return r;
    }

    void BuildSegments(Row r, CadParam q, int choices)
    {
        r.isSegments = true;
        var box = CadUi.Surface(r.root, "Segments", CadUi.White(0.07f), 16f);
        r.segments = box.rectTransform;
        for (int i = 0; i < choices; i++)
        {
            object v = q.IsOptions ? (object)q.options[i] : (long)Math.Round(q.min.Value + i * q.step.Value);
            string text = q.IsOptions ? CadUi.Capital(q.options[i]) : v.ToString();
            var seg = CadButton.Create(box.transform, text, CadButton.Segment, 14f, null);
            seg.AddLabel(text, CadUi.Body);
            object chosen = v;
            seg.Button.onClick.AddListener(() => PickSegment(r, chosen));
            Hover(r, seg.gameObject);
            r.segs.Add(seg);
            r.segRects.Add(seg.Rect);
            r.segValues.Add(v);
        }
    }

    void BuildSlider(Row r, CadParam q)
    {
        r.isSlider = true;
        r.step = q.step.HasValue && q.step.Value > 0 ? q.step.Value : 1.0;
        r.minus = CadButton.Create(r.root, "Minus", CadButton.Stepper, 0f, () => Step(r, -1), pill: true);
        r.minus.AddLabel("−", 16f);
        Hover(r, r.minus.gameObject);
        r.plus = CadButton.Create(r.root, "Plus", CadButton.Stepper, 0f, () => Step(r, 1), pill: true);
        r.plus.AddLabel("+", 16f);
        Hover(r, r.plus.gameObject);

        // The track: the whole 32 mm takes the pointer (an invisible hit area is the slider's graphic).
        var hit = CadUi.Hit(r.root, "Track");
        var track = hit.rectTransform;
        var rail = CadUi.Surface(track, "Rail", CadUi.White(0.14f), 2f);
        CadUi.Band(rail.rectTransform, 14f, 4f);
        var fillArea = CadUi.Rect(track, "Fill Area");
        CadUi.Band(fillArea, 14f, 4f);
        var fill = CadUi.Surface(fillArea, "Fill", CadUi.White(0.85f), 2f);
        CadUi.Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);  // the slider drives its anchors only
        r.tick = CadUi.Surface(track, "Default", CadUi.White(0.3f), 1f);
        r.ghostRing = CadUi.Pill(track, "Was", CadUi.Hue);
        var ghostCore = CadUi.Pill(r.ghostRing.transform, "Core", new Color(0.075f, 0.082f, 0.098f, 0.9f));
        CadUi.Stretch(ghostCore.rectTransform, 2f, 2f, 2f, 2f);
        var slideArea = CadUi.Rect(track, "Handle Slide Area");
        CadUi.Band(slideArea, 8f, 16f);
        r.knob = CadUi.Rect(slideArea, "Knob");
        var halo = CadUi.Pill(r.knob, "Halo", CadUi.White(0.12f));
        halo.rectTransform.anchorMin = halo.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        halo.rectTransform.sizeDelta = new Vector2(22f, 22f);
        var knob = CadUi.Pill(r.knob, "Knob", Color.white);
        CadUi.Stretch(knob.rectTransform, 0f, 0f, 0f, 0f);

        r.slider = track.gameObject.AddComponent<Slider>();
        r.slider.transition = Selectable.Transition.None;
        r.slider.navigation = new Navigation { mode = Navigation.Mode.None };
        r.slider.targetGraphic = hit;
        r.slider.fillRect = (RectTransform)fill.transform;
        r.slider.handleRect = r.knob;
        r.slider.direction = Slider.Direction.LeftToRight;
        r.slider.wholeNumbers = true;
        r.slider.minValue = (float)Math.Round((q.min ?? 0) / r.step);
        r.slider.maxValue = (float)Math.Round((q.max ?? Math.Max(1, q.Number * 2)) / r.step);
        r.knob.sizeDelta = new Vector2(16f, 0f);
        r.slider.onValueChanged.AddListener(v => Dragged(r, v));
        r.grip = track.gameObject.AddComponent<CadSliderRelease>();
        r.grip.released += () => Released(r);
        Hover(r, track.gameObject);

        r.value = CadUi.Text(r.root, "Value", CadUi.Value, Color.white, bold: true, align: TextAlignmentOptions.Right);
        r.live = new CadLiveText(r.value);
        r.unit = CadUi.Text(r.root, "Unit", CadUi.Unit, CadUi.White(0.5f), align: TextAlignmentOptions.Right);
        r.was = CadUi.Text(r.root, "Was", CadUi.Tick, CadUi.Peach, align: TextAlignmentOptions.Right);
    }

    void Hover(Row r, GameObject go) => CadHover.Add(go, d => Hovered(r, d));

    void BuildPresets(CadParams p)
    {
        var b = new System.Text.StringBuilder(p.part ?? "");
        if (p.presets != null) foreach (var x in p.presets) b.Append('|').Append(x.name);
        string key = b.ToString();
        if (key == presetKey) return;
        presetKey = key;
        foreach (var x in presets) Discard(x.gameObject);
        presets.Clear();
        presetRects.Clear();
        var list = p.presets ?? new List<CadPreset>();
        foreach (var preset in list)
        {
            var chosen = preset;
            var pill = CadButton.Create(lockable, "Preset " + preset.name, CadButton.Pill, 0f, () => owner.ApplyPreset(chosen), pill: true);
            pill.AddLabel(CadUi.Clean(preset.name), CadUi.Body);
            pill.Rect.sizeDelta = new Vector2(CadUi.Width(pill.Label, pill.Label.text) + 26f, PresetH);
            presets.Add(pill);
            presetRects.Add(pill.Rect);
        }
        CadUi.Show(presetsHeading, presets.Count > 0);
    }

    // Fixed inputs (analysis.constants): label, value, note; rebuilt when they change.
    readonly List<Row> infoRows = new List<Row>();
    string infoKey;

    void ShowInfo(CadPanelState s)
    {
        var list = s.a?.constants;
        var b = new System.Text.StringBuilder();
        if (list != null) foreach (var c in list) b.Append(c.label).Append('|').Append(c.value).Append('|').Append(c.note).Append('#');
        string key = b.ToString();
        if (key == infoKey) return;
        infoKey = key;
        foreach (var r in infoRows) Discard(r.root.gameObject);
        infoRows.Clear();
        if (list == null) return;
        foreach (var c in list)
        {
            var r = new Row { isInfo = true, name = c.label };
            r.root = CadUi.Rect(lockable, "Info " + c.label);
            r.label = CadUi.Text(r.root, "Label", CadUi.Body, CadUi.White(0.8f));
            r.label.text = CadUi.Clean(c.label);
            r.info = CadUi.Text(r.root, "Value", CadUi.Body, CadUi.White(0.75f), wrap: true);
            r.info.alignment = TextAlignmentOptions.TopLeft;
            r.info.text = CadUi.Clean(c.value);
            r.note = CadUi.Text(r.root, "Note", CadUi.Unit, CadUi.White(0.45f), wrap: true);
            r.note.alignment = TextAlignmentOptions.TopLeft;
            r.note.text = CadUi.Clean(c.note ?? "");
            infoRows.Add(r);
        }
        help.SetAsLastSibling();
    }

    // ---- Layout ----

    public override float Layout(float w)
    {
        width = w;
        float y = 0f;
        if (mockNote.gameObject.activeSelf)
        {
            CadUi.Band(mockNote, y, NoteH);
            y += NoteH + Gap;
        }
        if (lockNote.gameObject.activeSelf)
        {
            CadUi.Band(lockNote, y, NoteH);
            y += NoteH + Gap;
        }
        if (presets.Count > 0)
        {
            CadUi.Band(presetsHeading.rectTransform, y + 4f, HeadingH, 0f, 90f);
            float rw = CadUi.Width(resetAll.Label, "Reset all") + 16f;
            CadUi.PlaceRight(resetAll.Rect, 0f, y - 2f, rw, 28f);  // a 28 mm target round the 12 mm text
            y += 4f + HeadingH + Gap;
            y += CadUi.Flow(presetRects, w, 6f, 6f, y) + Gap;
        }
        else
        {
            float rw = CadUi.Width(resetAll.Label, "Reset all") + 16f;
            CadUi.PlaceRight(resetAll.Rect, 0f, y, rw, 28f);
            y += 28f + Gap;
        }

        int row = 0;
        for (int h = 0; h < headings.Count; h++)
        {
            string group = headingGroups[h];
            if (group == null)
            {
                // Fixed inputs.
                bool any = infoRows.Count > 0;
                CadUi.Show(headings[h], any);
                if (!any) continue;
                CadUi.Band(headings[h].rectTransform, y + 4f, HeadingH);
                y += 4f + HeadingH + 2f;
                foreach (var r in infoRows)
                {
                    float iw = w - PadX - LabelW - ColGap - PadX;
                    float vh = CadUi.Height(r.info, r.info.text, iw), nh = CadUi.Height(r.note, r.note.text, iw);
                    float hgt = Mathf.Max(RowH, 8f + vh + nh + 8f);
                    CadUi.Band(r.root, y, hgt);
                    CadUi.Place(r.label.rectTransform, PadX, 0f, LabelW, Mathf.Min(hgt, RowH));
                    CadUi.Band(r.info.rectTransform, 8f, vh + 1f, PadX + LabelW + ColGap, PadX);
                    CadUi.Band(r.note.rectTransform, 8f + vh, nh + 1f, PadX + LabelW + ColGap, PadX);
                    y += hgt + 2f;
                }
                y += Gap - 2f;
                continue;
            }
            CadUi.Band(headings[h].rectTransform, y + 4f, HeadingH);
            y += 4f + HeadingH + 2f;
            while (row < rows.Count && Group(rows[row].param) == group)
            {
                var r = rows[row++];
                r.y = y;
                CadUi.Band(r.root, y, RowH);
                LayoutRow(r, w);
                y += RowH + 2f;
            }
            y += Gap - 2f;
        }
        if (helpRow != null && help.gameObject.activeSelf) PlaceHelp();
        return Mathf.Max(0f, y - Gap);
    }

    static string Group(CadParam q) => string.IsNullOrEmpty(q.group) ? "General" : q.group;

    void LayoutRow(Row r, float w)
    {
        float labelX = PadX + (r.changed ? 12f : 0f);
        CadUi.Place(r.label.rectTransform, labelX, 0f, LabelW - (labelX - PadX), RowH);
        float controls = PadX + LabelW + ColGap;
        if (r.isSegments)
        {
            // The boards' 30 mm box with 26 mm segments, grown 2 mm so each segment is a 28 mm target.
            CadUi.Band(r.segments, (RowH - 32f) * 0.5f, 32f, controls, PadX);
            // Equal segments inside the box's 2 mm padding, 2 mm apart.
            for (int i = 0; i < r.segRects.Count; i++)
            {
                float x0 = i / (float)r.segRects.Count, x1 = (i + 1) / (float)r.segRects.Count;
                CadUi.Column(r.segRects[i], x0, x1, i == 0 ? 2f : 1f, i == r.segRects.Count - 1 ? 2f : 1f, 2f, 28f);
            }
            return;
        }
        if (!r.isSlider) return;
        CadUi.Place(r.minus.Rect, controls, (RowH - StepW) * 0.5f, StepW, StepW);
        float right = PadX + ValueW + ColGap;
        CadUi.PlaceRight(r.plus.Rect, right, (RowH - StepW) * 0.5f, StepW, StepW);
        CadUi.Band((RectTransform)r.slider.transform, (RowH - TrackH) * 0.5f, TrackH, controls + StepW + ColGap, right + StepW + ColGap);
        // Value, unit, "was": a right-aligned column, centred in the row.
        bool was = r.was.gameObject.activeSelf, unit = !string.IsNullOrEmpty(r.unit.text);
        float column = 16f + (unit ? 12f : 0f) + (was ? 11f : 0f), top = (RowH - column) * 0.5f;
        CadUi.PlaceRight(r.value.rectTransform, PadX, top, ValueW, 16f);
        CadUi.PlaceRight(r.unit.rectTransform, PadX, top + 15f, ValueW, 13f);
        CadUi.PlaceRight(r.was.rectTransform, PadX, top + (unit ? 27f : 15f), ValueW, 12f);
    }

    // A mark on the track (the default tick, the "was" ring) at a share of its width; hidden for a negative share.
    static void PlaceMark(RectTransform rt, float at, float size, float width = 0f)
    {
        bool show = at >= 0f;
        if (rt.gameObject.activeSelf != show) rt.gameObject.SetActive(show);
        if (!show) return;
        rt.anchorMin = rt.anchorMax = new Vector2(at, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width > 0f ? width : size, size);
        rt.anchoredPosition = new Vector2(0f, -TrackH * 0.5f);
    }

    float Norm(Row r, double v)
    {
        double min = r.param.min ?? 0, max = r.param.max ?? 1;
        return max > min ? Mathf.Clamp01((float)((v - min) / (max - min))) : 0f;
    }

    // ---- Values ----

    void SetShown(Row r, double v)
    {
        r.shown = v;
        r.slider.SetValueWithoutNotify((float)Math.Round(v / r.step));
        WriteValue(r, v);
    }

    void WriteValue(Row r, double v)
    {
        int dec = Decimals(r.step);
        r.live.Begin();
        if (dec == 0) r.live.Int((long)Math.Round(v), commas: true);
        else
        {
            if (dec == 2 && Math.Abs(Math.Round(v * 10) - v * 10) < 1e-6) dec = 1;  // "1.5", not "1.50"
            r.live.Fixed((float)v, dec);
        }
        r.live.Apply();
    }

    static int Decimals(double step)
    {
        if (step >= 1 || step <= 0) return 0;
        if (Math.Abs(step * 10 - Math.Round(step * 10)) < 1e-9) return 1;
        return 2;
    }

    static double ToDouble(object v, double fallback)
    {
        if (v == null || v is string) return fallback;
        try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception) { return fallback; }
    }

    // Dragging: the value follows live; nothing is sent until release.
    void Dragged(Row r, float steps)
    {
        r.shown = steps * r.step;
        WriteValue(r, r.shown);
        r.value.color = CadUi.PeachLight;
    }

    void Released(Row r)
    {
        double v = Math.Round(r.slider.value) * r.step;
        if (locked || Math.Abs(v - r.committed) < r.step * 0.5)
        {
            if (locked) SetShown(r, r.committed);
            r.value.color = r.changed ? CadUi.PeachLight : Color.white;
            return;
        }
        Send(r, v);
    }

    // − / +: the value moves at once; it's sent 0.4 s after the last tap.
    void Step(Row r, int direction)
    {
        if (locked || r.param == null) return;
        double min = r.param.min ?? double.MinValue, max = r.param.max ?? double.MaxValue;
        double v = Math.Max(min, Math.Min(max, Math.Round(r.shown / r.step) * r.step + direction * r.step));
        if (Math.Abs(v - r.shown) < 1e-9) return;
        r.shown = v;
        r.slider.SetValueWithoutNotify((float)Math.Round(v / r.step));
        WriteValue(r, v);
        r.value.color = CadUi.PeachLight;
        r.debounce = Debounce;
    }

    void PickSegment(Row r, object v)
    {
        if (locked || r.param == null || CadOverviewTab.Same(v, r.param.value)) return;
        for (int i = 0; i < r.segs.Count; i++) r.segs[i].On = CadOverviewTab.Same(r.segValues[i], v);
        string text = v is string str ? CadUi.Capital(str) : v.ToString();
        owner.Commit(r.param, v, CadUi.Clean(r.param.label ?? r.name) + " " + text);
    }

    void Send(Row r, double v)
    {
        if (locked) return;
        object value = r.integer ? (object)(long)Math.Round(v) : v;
        string unit = string.IsNullOrEmpty(r.param.unit) ? "" : " " + CadUi.Clean(r.param.unit);
        r.value.color = CadUi.Hue;
        owner.Commit(r.param, value, CadUi.Clean(r.param.label ?? r.name) + " " + CadUi.ParamValue(r.param, value) + unit);
    }

    public override void Tick(float dt)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.debounce <= 0f) continue;
            r.debounce -= dt;
            if (r.debounce > 0f) continue;
            r.debounce = 0f;
            if (!locked && Math.Abs(r.shown - r.committed) >= r.step * 0.5) Send(r, r.shown);
            else
            {
                if (locked) SetShown(r, r.committed);  // a demo started meanwhile: nothing is sent
                r.value.color = r.changed ? CadUi.PeachLight : Color.white;  // (or tapped back to where it was)
            }
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!r.cooling) continue;
            if (r.hovers > 0 && !locked) r.cooling = false;
            else if ((r.coolTimer -= dt) <= 0f) Cool(r, tell: true);
        }
        if (helpRow != null && !help.gameObject.activeSelf)
        {
            helpTimer -= dt;
            if (helpTimer <= 0f && !string.IsNullOrEmpty(helpRow.param?.help))
            {
                helpText.text = CadUi.Clean(helpRow.param.help);
                help.gameObject.SetActive(true);
                help.SetAsLastSibling();
                PlaceHelp();
            }
        }
    }

    // ---- Pointing: the row lights, its parts and dimension light on the model, its help follows ----

    void Hovered(Row r, int delta)
    {
        r.hovers = Mathf.Max(0, r.hovers + delta);
        if (r.hovers > 0 && !locked)
        {
            r.cooling = false;  // back before it went cold: nothing changes
            if (r.hot) return;
            if (hotRow != null && hotRow != r) Cool(hotRow, tell: false);  // the new row takes the highlight straight over
            r.hot = true;
            r.bg.color = CadUi.White(0.05f);
            hotRow = r;
            helpRow = r;
            helpTimer = HelpDelay;
            help.gameObject.SetActive(false);
            owner.PointAt(r.name);
        }
        else if (r.hot && !r.cooling)
        {
            r.cooling = true;  // gone cold unless the pointer turns up on another of its hit areas within the grace
            r.coolTimer = HoverGrace;
        }
    }

    void Cool(Row r, bool tell)
    {
        r.cooling = false;
        if (!r.hot) return;
        r.hot = false;
        r.bg.color = CadUi.White(0f);
        if (hotRow != r) return;
        hotRow = null;
        helpRow = null;
        help.gameObject.SetActive(false);
        if (tell) owner.PointAt(null);
    }

    void PlaceHelp()
    {
        if (helpRow == null) return;
        float inner = width - 2f * PadX - 20f;
        float h = CadUi.Height(helpText, helpText.text, inner) + 14f;
        CadUi.Band(help, helpRow.y + RowH + 2f, h, PadX, PadX);
        CadUi.Stretch(helpText.rectTransform, 10f, 7f, 10f, 5f);
    }

    void ClearHot()
    {
        foreach (var r in rows)
        {
            r.hovers = 0;
            r.cooling = false;
            if (r.hot)
            {
                r.hot = false;
                r.bg.color = CadUi.White(0f);
            }
        }
        if (hotRow != null) owner.PointAt(null);
        hotRow = helpRow = null;
        help.gameObject.SetActive(false);
    }

    public override void Hidden() => ClearHot();

    static void Discard(GameObject go)
    {
        go.SetActive(false);
        UnityEngine.Object.Destroy(go);
    }
}
