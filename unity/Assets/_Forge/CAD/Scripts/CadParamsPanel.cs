using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The design panel (panel id cad-params; CadParamsPanel.prefab, a ForgePanel variant, 480 x 600 mm), built at runtime
/// under ForgePanel.Content to the CAD UX spec, section 2 (boards: HANDOFF_UI2/cad/CadPanels.dc.html and
/// CadExperience.dc.html):
/// - header (over Core's): the design's glyph on the mode tile, the title as the Designs menu, "v4 · 4 versions · Live";
/// - tabs: Overview · Parameters · Analysis · History;
/// - the verdict, always on top: the card on Overview, a strip on the other tabs, live telemetry while a demo runs,
///   and a red banner over it when a build fails;
/// - the active tab in a scroll view;
/// - the footer: Undo · Expand · Explode · Fly / Hover / Run.
/// Generic over the hub's designs: everything comes from CadSession (hub or mocks), and the viewer is reached through
/// the session (and CadViewer.HighlightParam for a pointed-at row). The prefab's older UI (CadUI) is switched off.
/// Sections refill only when the data changes (coalesced to once a frame); per frame only timers and live numbers.
/// </summary>
public class CadParamsPanel : MonoBehaviour
{
    public CadMockSet mocks;

    // The body (mm, the boards'): 12 under the 64 mm header, 18 at the sides (Core's Content inset), 16 at the bottom.
    const float BodyTopRaise = 2f, Gap = 10f, TabsH = 34f, HeroH = 218f, StripH = 56f, FadeSeconds = 0.15f;
    const float ToastSeconds = 2.6f;
    static readonly string[] TabNames = { "Overview", "Parameters", "Analysis", "History" };

    ForgePanel panel;
    CadSession session;
    CadPanelState state;
    RectTransform body, overlay, viewport, content, toast;
    CadPanelHeader header;
    RectTransform tabsRoot;
    readonly CadButton[] tabButtons = new CadButton[4];
    readonly RectTransform[] tabRects = new RectTransform[4];
    static readonly float[] TabFlex = { 1f, 1f, 1f, 1f };
    CadVerdictCard verdict;
    CadTelemetry telemetry;
    CadBanner banner;
    ScrollRect scroll;
    ForgeSurface thumb;
    CadPanelTab[] tabs;
    CadPanelFooter footer;
    TextMeshProUGUI toastText;
    CanvasGroup contentGroup, toastGroup;
    int tab;
    bool built, dataDirty = true, layoutDirty = true, telemetryOn;
    float fade = 1f, toastTimer;
    string pointed;
    readonly Dictionary<int, CadModelUpdated> seen = new Dictionary<int, CadModelUpdated>();
    string seenPart, versionsKey, flightKey;
    int seenGeneration = -1;  // CadSession.Generation the caches above are for (the hub's v1 isn't the mocks')
    int versionsAsked;

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        state = new CadPanelState();
    }

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        state.session = session;
        if (!built) Build();
        session.ParamsChanged += MarkData;
        session.PartsChanged += OnParts;
        session.ViewChanged += OnView;
        session.InspectChanged += MarkData;
        session.StatusChanged += OnStatus;
        session.FlightReady += OnFlight;
        session.DemoChanged += MarkData;
        session.DemoFrame += OnFrame;
        session.BuildingChanged += MarkData;
        session.FailureChanged += MarkData;
        session.OverlaysChanged += MarkData;
        session.ModelChanged += OnModel;
        session.Notice += ShowToast;
        ForgeNet.StatusChanged += OnNet;
        if (session.Latest != null) Remember(session.Latest.info);
        dataDirty = layoutDirty = true;
    }

    void OnDisable()
    {
        if (session != null)
        {
            session.ParamsChanged -= MarkData;
            session.PartsChanged -= OnParts;
            session.ViewChanged -= OnView;
            session.InspectChanged -= MarkData;
            session.StatusChanged -= OnStatus;
            session.FlightReady -= OnFlight;
            session.DemoChanged -= MarkData;
            session.DemoFrame -= OnFrame;
            session.BuildingChanged -= MarkData;
            session.FailureChanged -= MarkData;
            session.OverlaysChanged -= MarkData;
            session.ModelChanged -= OnModel;
            session.Notice -= ShowToast;
        }
        ForgeNet.StatusChanged -= OnNet;
        if (tabs != null) tabs[tab].Hidden();
        PointAt(null);
        if (header != null && header.MenuOpen) header.ShowMenu(false, state);
    }

    void Start()
    {
        // Workspace sets the mode badge after spawning the panel: hide its text now (the glyph is the tile's).
        header.HideModeText();
        overlay.SetAsLastSibling();
        dataDirty = true;
    }

    void OnRectTransformDimensionsChange() => layoutDirty = true;

    // ---- Building (once) ----

    void Build()
    {
        built = true;
        var root = panel != null ? panel.Content : (RectTransform)transform;
        // The prefab's older UI and the ForgePanel placeholder make way.
        var old = root.Find("CadUI");
        if (old != null) old.gameObject.SetActive(false);
        var placeholder = root.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        body = CadUi.Rect(root, "CadPanel");
        CadUi.Stretch(body, 0f, -BodyTopRaise, 0f, 0f);
        overlay = CadUi.Rect(transform, "CadHeader");
        CadUi.Stretch(overlay, 0f, 0f, 0f, 0f);

        header = new CadPanelHeader(this, panel, overlay);

        // Tabs: a segmented control, four equal segments.
        var tabsFill = CadUi.Surface(body, "Tabs", CadUi.White(0.07f), 17f);
        tabsRoot = tabsFill.rectTransform;
        var segments = CadUi.Rect(tabsRoot, "Segments");
        CadUi.Stretch(segments, 3f, 3f, 3f, 3f);
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            tabButtons[i] = CadButton.Create(segments, TabNames[i], CadButton.Segment, 14f, () => SetTab(index));
            tabButtons[i].AddLabel(TabNames[i], 13f);
            tabRects[i] = tabButtons[i].Rect;
        }

        banner = new CadBanner(body, () => session.Undo(), () => session.DismissFailure());
        verdict = new CadVerdictCard(body, () => SetTab(0));
        telemetry = new CadTelemetry(body);

        // The active tab scrolls (drag it; desktop mode sends no wheel to uGUI); a slim thumb shows where you are.
        var view = CadUi.Hit(body, "Viewport");
        viewport = view.rectTransform;
        viewport.gameObject.AddComponent<RectMask2D>();
        content = CadUi.Rect(viewport, "Content");
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = new Vector2(0f, -100f);
        content.offsetMax = Vector2.zero;
        contentGroup = CadUi.Group(content.gameObject);
        scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Elastic;
        scroll.elasticity = 0.08f;
        scroll.inertia = true;
        scroll.decelerationRate = 0.12f;
        scroll.scrollSensitivity = 18f;
        scroll.onValueChanged.AddListener(_ => PlaceThumb());
        thumb = CadUi.Pill(body, "ScrollThumb", CadUi.White(0.22f));

        tabs = new CadPanelTab[]
        {
            new CadOverviewTab(content, this), new CadParamsTab(content, this), new CadAnalysisTab(content, this), new CadHistoryTab(content, this),
        };
        for (int i = 0; i < tabs.Length; i++) tabs[i].Root.gameObject.SetActive(i == tab);
        tabButtons[tab].On = true;

        footer = new CadPanelFooter(body, this);

        var toastFill = CadUi.Pill(body, "Toast", CadUi.ToastFill);
        toast = toastFill.rectTransform;
        toastGroup = CadUi.Group(toast.gameObject);
        toastGroup.blocksRaycasts = false;
        toastText = CadUi.Text(toast, "Text", 13f, CadUi.White(0.9f), align: TextAlignmentOptions.Center);
        CadUi.Stretch(toastText.rectTransform, 16f, 0f, 16f, 0f);
        toast.gameObject.SetActive(false);
    }

    // ---- Events in: marked, then handled once in LateUpdate ----

    void MarkData() => dataDirty = true;
    void OnView(CadView v) => dataDirty = true;
    void OnStatus(string s) => dataDirty = true;
    void OnNet(ForgeNetStatus s) => dataDirty = true;
    void OnFlight(CadFlight f) => dataDirty = true;

    void OnModel(CadUpdate update)
    {
        if (update != null) NewSource(update.generation);
        Remember(update?.info);
        dataDirty = true;
    }

    // Another source (the hub after the mocks): versions with the same numbers are other models; forget what was seen.
    void NewSource(int generation)
    {
        if (generation == seenGeneration) return;
        seenGeneration = generation;
        seen.Clear();
        seenPart = null;
        versionsKey = flightKey = null;
        state?.versions.Clear();
    }

    void OnParts()
    {
        if (header != null && header.MenuOpen) header.RebuildMenu(state);
        dataDirty = true;
    }

    void OnFrame(CadDemoFrame frame)
    {
        state.frame = frame;
        state.hasFrame = true;
        if (telemetryOn) telemetry.Frame(frame, Time.deltaTime);
    }

    // The versions seen this session (their params and analysis), for "was" and Last change when the hub's list lags.
    void Remember(CadModelUpdated info)
    {
        if (info == null) return;
        if (info.part != seenPart)
        {
            seen.Clear();
            seenPart = info.part;
        }
        if (seen.Count > 64) seen.Clear();
        seen[info.version] = info;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        CadButton.TickAll(dt);
        if (!built) return;
        header.Tick(dt);
        verdict.Tick(dt);
        tabs[tab].Tick(dt);
        if (fade < 1f)
        {
            fade = Mathf.Min(1f, fade + dt / FadeSeconds);
            contentGroup.alpha = fade;
        }
        if (toastTimer > 0f)
        {
            toastTimer -= dt;
            float a = Mathf.Clamp01(toastTimer / 0.3f);
            if (!Mathf.Approximately(toastGroup.alpha, a)) toastGroup.alpha = a;  // set only while it fades: no re-batch otherwise
            if (toastTimer <= 0f) toast.gameObject.SetActive(false);
        }
    }

    void LateUpdate()
    {
        if (!built) return;
        if (dataDirty) Refresh();
        if (layoutDirty) Layout();
    }

    // ---- State: worked out from the session, then shown ----

    void Refresh()
    {
        dataDirty = false;
        NewSource(session.Generation);
        var s = state;
        var latest = session.Latest;
        s.p = session.Params;
        s.info = latest?.info;
        s.a = s.info?.analysis;
        s.version = s.p != null ? s.p.version : s.info != null ? s.info.version : 0;
        s.title = s.p != null && !string.IsNullOrEmpty(s.p.title) ? s.p.title : s.info?.title ?? (s.p != null ? CadLook.Pretty(s.p.part) : null);
        s.glyph = s.p?.glyph;
        if (string.IsNullOrEmpty(s.glyph) && session.Parts?.parts != null)
            foreach (var part in session.Parts.parts)
                if (part.part == s.p?.part) s.glyph = part.glyph;
        s.verdict = s.a != null ? CadPanelState.Verdict(s.a) : null;
        s.primary = CadPanelState.Primary(s.a, s.verdict);
        s.demo = CadDemoPlayer.Resolve(s.a, s.p);
        s.question = CadPanelState.Question(s.demo);
        s.ready = session.Ready;
        s.mock = session.Ready && session.IsMock;
        s.live = !s.mock && ForgeNet.Status == ForgeNetStatus.Live;
        s.building = session.Building;
        s.buildingWhat = session.BuildingWhat;
        s.failure = session.Failure;
        s.demoRunning = session.DemoRunning;
        s.exploded = session.View.explode == true;
        s.expanded = session.Inspecting;
        s.partsOff = session.PartsOff;
        if (!s.building)
        {
            s.pendingParam = null;
            s.pendingValue = null;
        }
        if (!s.demoRunning) s.hasFrame = false;

        AskVersions();
        AskFlight();
        s.flight = null;
        if (s.version > 0 && session.TryGetFlight(s.version, out var flight)) s.flight = flight;

        // Where this version came from: the hub's history, else what this panel saw arrive.
        CadVersion row = Find(s.version);
        s.parent = row != null ? row.parent : s.info != null && s.info.source != "undo" && !s.mock ? s.info.previousVersion : null;
        CadVersion parentRow = s.parent.HasValue ? Find(s.parent.Value) : null;
        seen.TryGetValue(s.parent ?? -1, out var parentInfo);
        s.parentParams = parentRow?.parameters ?? parentInfo?.parameters;
        s.parentAnalysis = parentInfo?.analysis ?? parentRow?.analysis;
        s.parentFlight = null;
        if (s.parent.HasValue && session.TryGetFlight(s.parent.Value, out var before)) s.parentFlight = before;
        s.label = !string.IsNullOrEmpty(s.info?.label) ? s.info.label : row?.label;
        s.source = row?.source ?? s.info?.source;
        s.createdAt = row?.createdAt;
        s.canUndo = s.mock ? session.MockCanUndo : s.parent.HasValue;
        int newest = s.version;
        foreach (var v in s.versions) newest = Mathf.Max(newest, v.version);
        if (session.Parts?.parts != null)
            foreach (var part in session.Parts.parts)
                if (part.version.HasValue) newest = Mathf.Max(newest, part.version.Value);
        s.buildingVersion = newest + 1;

        header.Show(s);
        verdict.Show(s);
        if (s.demoRunning && !telemetryOn)
        {
            telemetryOn = true;
            telemetry.Begin(s);
        }
        else if (s.demoRunning) telemetry.Refresh(s);
        else telemetryOn = false;
        if (s.failure != null) banner.Show(s.failure, s, BodyWidth);
        footer.Show(s);
        tabs[tab].Show(s);
        layoutDirty = true;
    }

    CadVersion Find(int version)
    {
        foreach (var v in state.versions)
            if (v.version == version) return v;
        return null;
    }

    // The design's versions: once per version (hub: GET /cad/versions; mock: the stored ones, at once).
    void AskVersions()
    {
        string key = (state.p?.part ?? "") + "/" + state.version + (state.mock ? "m" : "h");
        if (key == versionsKey || state.p == null) return;
        versionsKey = key;
        int ask = ++versionsAsked;
        session.GetVersions(list =>
        {
            if (ask != versionsAsked || list == null) return;
            state.versions.Clear();
            state.versions.AddRange(list);
            dataDirty = true;
        });
    }

    // The rocket's flight for its tiles (the hub runs its sim on the first ask; the demo reuses it).
    void AskFlight()
    {
        var demo = state.demo;
        bool flies = demo?.kind == CadDemoPlayer.Launch || (state.p != null && state.p.Has("flight"));
        if (!flies || state.version <= 0) return;
        string key = state.p?.part + "/" + state.version + "/" + session.Generation;
        if (key == flightKey) return;
        flightKey = key;
        if (session.TryGetFlight(state.version, out _)) return;
        session.GetFlight(state.version, demo?.source, f => dataDirty = true);
    }

    float BodyWidth => body.rect.width > 1f ? body.rect.width : 444f;

    // ---- Layout: top to bottom, tabs, banner, verdict / strip / telemetry, the tab, the footer ----

    void Layout()
    {
        layoutDirty = false;
        float w = BodyWidth, h = body.rect.height > 1f ? body.rect.height : 510f;
        float y = 0f;
        CadUi.Band(tabsRoot, y, TabsH);
        CadUi.Share(tabRects, TabFlex, 2f, 0f, TabsH - 6f);
        y += TabsH + Gap;

        bool failed = state.failure != null;
        CadUi.Show(banner.Root, failed);
        if (failed)
        {
            float bh = banner.Show(state.failure, state, w);
            CadUi.Band(banner.Root, y, bh);
            y += bh + Gap;
        }

        bool running = telemetryOn;
        bool card = !running && tab == 0;
        CadUi.Show(telemetry.Root, running);
        CadUi.Show(verdict.Card, card);
        CadUi.Show(verdict.Strip, !running && !card);
        float slot = running || card ? HeroH : StripH;
        if (running)
        {
            CadUi.Band(telemetry.Root, y, HeroH);
            telemetry.Layout();
        }
        else if (card) CadUi.Band(verdict.Card, y, HeroH);
        else CadUi.Band(verdict.Strip.Rect, y, StripH);
        verdict.Layout();
        y += slot + Gap;

        // Footer at the bottom; the demo's blocked reason just above it.
        float bottom = h;
        var foot = footer.Root;
        foot.anchorMin = new Vector2(0f, 0f);
        foot.anchorMax = new Vector2(1f, 0f);
        foot.pivot = new Vector2(0.5f, 0f);
        foot.offsetMin = Vector2.zero;
        foot.offsetMax = new Vector2(0f, CadPanelFooter.Height);
        footer.Layout();
        bottom -= CadPanelFooter.Height + Gap;
        if (footer.HintShown || footer.ReassembleShown)
        {
            // The boards' fly hint (and Reassemble while parts are off on the stage): tucked between the content and
            // the footer (margins -4 above, -2 below).
            float lineH = footer.ReassembleShown ? CadPanelFooter.ReassembleH : CadPanelFooter.HintH;
            float hy = h - CadPanelFooter.Height - (Gap - 2f) - lineH;
            CadUi.Band(footer.Hint.rectTransform, hy, lineH, 4f, 4f);
            footer.PlaceReassemble(hy, lineH);
            bottom = hy - (Gap - 4f);
        }

        // The scroll view takes what's left.
        viewport.anchorMin = new Vector2(0f, 1f);
        viewport.anchorMax = new Vector2(1f, 1f);
        viewport.pivot = new Vector2(0.5f, 1f);
        viewport.offsetMin = new Vector2(0f, -Mathf.Max(y + 40f, bottom));
        viewport.offsetMax = new Vector2(0f, -y);
        float tabHeight = tabs[tab].Layout(w);
        content.sizeDelta = new Vector2(0f, tabHeight + 4f);
        var active = tabs[tab].Root;
        active.offsetMin = new Vector2(0f, -tabHeight);
        active.offsetMax = Vector2.zero;
        PlaceThumb();

        CadUi.Band(toast, bottom - 44f, 36f, Mathf.Max(0f, w * 0.5f - ToastWidth() * 0.5f), Mathf.Max(0f, w * 0.5f - ToastWidth() * 0.5f));
        overlay.SetAsLastSibling();
    }

    float ToastWidth() => Mathf.Min(BodyWidth, CadUi.Width(toastText, toastText.text) + 34f);

    void PlaceThumb()
    {
        if (thumb == null || viewport == null) return;
        float view = viewport.rect.height, total = content.rect.height;
        bool show = total > view + 1f && view > 1f;
        CadUi.Show(thumb, show);
        if (!show) return;
        float size = Mathf.Max(24f, view * view / total);
        float k = 1f - Mathf.Clamp01(scroll.verticalNormalizedPosition);
        float top = -viewport.offsetMax.y + k * (view - size);
        CadUi.PlaceRight(thumb.rectTransform, -9f, top, 3f, size);
    }

    // ---- Actions (the sections call these) ----

    public void SetTab(int index)
    {
        if (index < 0 || index >= tabs.Length) return;
        if (index == tab)
        {
            dataDirty = true;
            return;
        }
        tabs[tab].Hidden();
        tabs[tab].Root.gameObject.SetActive(false);
        tabButtons[tab].On = false;
        tab = index;
        tabs[tab].Root.gameObject.SetActive(true);
        tabButtons[tab].On = true;
        scroll.verticalNormalizedPosition = 1f;
        scroll.velocity = Vector2.zero;
        fade = 0f;
        contentGroup.alpha = 0f;
        dataDirty = layoutDirty = true;
    }

    public void ToggleMenu() => ShowMenu(!header.MenuOpen);

    public void ShowMenu(bool open)
    {
        header.ShowMenu(open, state);
        if (open) session.RefreshParts();  // a new part script shows up without a restart
    }

    public void OpenDesign(int index)
    {
        ShowMenu(false);
        var parts = session.Parts?.parts;
        if (parts == null || index < 0 || index >= parts.Count) return;
        session.OpenPart(parts[index].part);
    }

    /// <summary>A parameter row's change: the row shows it pending (orange) until the version lands.</summary>
    public void Commit(CadParam param, object value, string what)
    {
        if (param == null) return;
        state.pendingParam = param.name;
        state.pendingValue = value;
        session.SetParams(new Dictionary<string, object> { { param.name, value } }, what);
        dataDirty = true;
    }

    public void ApplyPreset(CadPreset preset) => session.ApplyPreset(preset);

    public void ResetAll() => session.ResetDesign();

    public void GoToVersion(int version)
    {
        var v = Find(version);
        if (v != null && version != state.version) session.GoToVersion(v);
    }

    /// <summary>Analysis's "On the model" toggles: 0 markers, 1 dimensions, 2 part labels (view state on the session).</summary>
    public void ToggleOverlay(int which)
    {
        if (which == 0) session.SetMarkersShown(!session.MarkersShown);
        else if (which == 1) session.SetMeasuring(!session.Measuring);
        else session.SetLabelsShown(!session.LabelsShown);
    }

    /// <summary>A parameter row is pointed at (null: none): its parts and dimension light on the model.</summary>
    public void PointAt(string name)
    {
        if (name == pointed) return;
        pointed = name;
        if (CadViewer.Active != null) CadViewer.Active.HighlightParam(name);
    }

    public void Undo() => session.Undo();

    public void Reassemble() => session.Reassemble();

    public void ToggleExpand() => session.SetInspecting(!session.Inspecting);

    public void ToggleExplode() => session.SetView(session.View.explode != true, session.View.focusId);

    // The demo button says what this design does (Fly / Hover / Run) and Stop while it runs; CadViewer runs it.
    public void ToggleDemo() => session.RequestDemo(!session.DemoRunning);

    void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text) || toast == null) return;
        toastText.text = CadUi.Clean(text);
        toast.gameObject.SetActive(true);
        toast.SetAsLastSibling();
        toastGroup.alpha = 1f;
        toastTimer = ToastSeconds;
        layoutDirty = true;
    }
}
