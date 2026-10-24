using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The Versions panel (panel id cad-timeline, left of the desk; CadVersionsPanel.prefab, a ForgePanel variant): the open
/// design's history, the same body as the design panel's History tab (<see cref="CadHistoryTab"/>): the main metric
/// across versions over its bands, then every version newest first; tap one to go back to it. Core's header keeps the
/// title ("Versions") and gets "Rocket · v4 · 4 versions" (or "Building v5…") as its subtitle.
/// Reads everything from CadSession (hub or mocks); refills only when the data changes (coalesced to once a frame).
/// </summary>
public class CadVersionsPanel : MonoBehaviour
{
    public CadMockSet mocks;

    ForgePanel panel;
    CadSession session;
    readonly CadPanelState state = new CadPanelState();
    RectTransform viewport, content;
    ScrollRect scroll;
    ForgeSurface thumb;
    CadHistoryTab history;
    bool built, dataDirty = true, layoutDirty = true;
    string versionsKey, subtitle;
    int versionsAsked, seenGeneration = -1;

    void Awake() => panel = GetComponent<ForgePanel>();

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        state.session = session;
        if (!built) Build();
        session.ParamsChanged += MarkData;
        session.ModelChanged += OnModel;
        session.BuildingChanged += MarkData;
        session.DemoChanged += MarkData;
        session.StatusChanged += OnStatus;
        ForgeNet.StatusChanged += OnNet;
        dataDirty = layoutDirty = true;
    }

    void OnDisable()
    {
        if (session != null)
        {
            session.ParamsChanged -= MarkData;
            session.ModelChanged -= OnModel;
            session.BuildingChanged -= MarkData;
            session.DemoChanged -= MarkData;
            session.StatusChanged -= OnStatus;
        }
        ForgeNet.StatusChanged -= OnNet;
    }

    void OnRectTransformDimensionsChange() => layoutDirty = true;

    void MarkData() => dataDirty = true;
    void OnModel(CadUpdate u) => dataDirty = true;
    void OnStatus(string s) => dataDirty = true;
    void OnNet(ForgeNetStatus s) => dataDirty = true;

    // The list scrolls (drag it; desktop mode sends no wheel to uGUI), with a slim thumb, as on the design panel.
    void Build()
    {
        built = true;
        var root = panel != null ? panel.Content : (RectTransform)transform;
        var placeholder = root.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        var view = CadUi.Hit(root, "Viewport");
        viewport = view.rectTransform;
        CadUi.Stretch(viewport, 0f, 0f, 0f, 0f);
        viewport.gameObject.AddComponent<RectMask2D>();
        content = CadUi.Rect(viewport, "Content");
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = new Vector2(0f, -100f);
        content.offsetMax = Vector2.zero;
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
        thumb = CadUi.Pill(root, "ScrollThumb", CadUi.White(0.22f));

        history = new CadHistoryTab(content, null, GoToVersion);
    }

    void Update() => CadButton.TickAll(Time.deltaTime);

    void LateUpdate()
    {
        if (!built) return;
        if (dataDirty) Refresh();
        if (layoutDirty) Layout();
    }

    void Refresh()
    {
        dataDirty = false;
        var s = state;
        if (session.Generation != seenGeneration)
        {
            // Another source (the hub after the mocks): its version numbers are other models.
            seenGeneration = session.Generation;
            versionsKey = null;
            s.versions.Clear();
        }
        s.p = session.Params;
        s.info = session.Latest?.info;
        s.a = s.info?.analysis;
        s.version = s.p != null ? s.p.version : s.info != null ? s.info.version : 0;
        s.verdict = s.a != null ? CadPanelState.Verdict(s.a) : null;
        s.primary = CadPanelState.Primary(s.a, s.verdict);
        s.ready = session.Ready;
        s.mock = session.Ready && session.IsMock;
        s.live = !s.mock && ForgeNet.Status == ForgeNetStatus.Live;
        s.building = session.Building;
        s.demoRunning = session.DemoRunning;
        AskVersions();

        string title = s.p != null && !string.IsNullOrEmpty(s.p.title) ? s.p.title : s.info?.title ?? (s.p != null ? CadLook.Pretty(s.p.part) : null);
        int count = s.versions.Count;
        string line = s.building ? "Building v" + (Newest() + 1) + "…"
            : s.version > 0 ? "v" + s.version + (count > 0 ? " · " + count + (count == 1 ? " version" : " versions") : "") : "Waiting for the design…";
        if (!string.IsNullOrEmpty(title)) line = title + " · " + line;
        if (line != subtitle && panel != null) panel.Subtitle = subtitle = line;

        history.Show(s);
        layoutDirty = true;
    }

    int Newest()
    {
        int newest = state.version;
        foreach (var v in state.versions) newest = Mathf.Max(newest, v.version);
        return newest;
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
            if (this == null || ask != versionsAsked || list == null) return;
            state.versions.Clear();
            state.versions.AddRange(list);
            dataDirty = true;
        });
    }

    void GoToVersion(int version)
    {
        if (version == state.version) return;
        foreach (var v in state.versions)
            if (v.version == version)
            {
                session.GoToVersion(v);
                return;
            }
    }

    void Layout()
    {
        layoutDirty = false;
        float w = viewport.rect.width > 1f ? viewport.rect.width : 444f;
        float h = history.Layout(w);
        content.sizeDelta = new Vector2(0f, h + 4f);
        history.Root.offsetMin = new Vector2(0f, -h);
        history.Root.offsetMax = Vector2.zero;
        PlaceThumb();
    }

    void PlaceThumb()
    {
        if (thumb == null || viewport == null) return;
        float view = viewport.rect.height, total = content.rect.height;
        bool show = total > view + 1f && view > 1f;
        CadUi.Show(thumb, show);
        if (!show) return;
        float size = Mathf.Max(24f, view * view / total);
        float k = 1f - Mathf.Clamp01(scroll.verticalNormalizedPosition);
        CadUi.PlaceRight(thumb.rectTransform, -9f, k * (view - size), 3f, size);
    }
}
