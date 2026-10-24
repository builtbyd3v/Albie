using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Shared CAD state for CadViewer and CadParamsPanel, which Workspace spawns as separate prefabs.
/// Hub mode: REST to the hub through ForgeNet, and ForgeNet's /ws events (cad.model_updated, cad.view,
/// cad.regen_failed) for changes made anywhere (sliders, Bit's voice tools, VS Code saves).
/// Mock mode: the hub's mocks, hub off. The hub address is ForgeNet's (one setting for the Quest).
/// The hub has a library of designs (rocket, quadcopter, ...); one is open, and everything acts on it.
/// </summary>
public class CadSession : MonoBehaviour
{
    public enum Mode { Auto, Hub, Mock }

    [Tooltip("Auto: follow ForgeNet (hub live = hub, no hub = mocks; switches to the hub when it appears).")]
    public Mode mode = Mode.Auto;
    public CadMockSet mocks;

    public static CadSession Instance { get; private set; }

    public bool Ready { get; private set; }
    public bool IsMock { get; private set; }
    public CadUpdate Latest { get; private set; }  // last version shown, for subscribers that arrive late
    public CadParams Params { get; private set; }
    public CadPartList Parts { get; private set; } = new CadPartList();
    public CadView View { get; private set; } = new CadView { explode = false };
    public string Status { get; private set; } = "Connecting to the hub...";
    public int MockIndex => mockIndex;
    public int MockCount => IsMock && mockPart >= 0 ? mocks.parts[mockPart].versions.Count : 0;

    public event Action<CadUpdate> ModelChanged;
    /// <summary>A version is on its way (hub: announced and downloading; mock: shown at once). The drawing panel asks
    /// for its drawing straight away instead of waiting for the model to load.</summary>
    public event Action<CadModelUpdated> VersionComing;
    public event Action ParamsChanged;
    public event Action PartsChanged;
    public event Action<CadView> ViewChanged;
    public event Action<string> StatusChanged;
    public event Action<CadFlight> FlightReady;

    /// <summary>The model is up in the big Inspect view (view only: the hub doesn't know about it).</summary>
    public bool Inspecting { get; private set; }
    /// <summary>Parts taken off the model in the Inspect view.</summary>
    public int PartsOff { get; private set; }
    public event Action InspectChanged;  // Inspecting or PartsOff changed
    public event Action ReassembleRequested;

    // View only, like Inspecting (the hub doesn't know): what CadViewer draws over the model and its demo. Any panel sets
    // them here; the viewer, wherever Workspace spawned it, reacts.
    /// <summary>Dimension callouts shown (analysis.dimensions). Off until a panel's Measure turns them on.</summary>
    public bool Measuring { get; private set; }
    /// <summary>Marker beads shown (analysis.markers: CG, CP, thrust, input, output...) and the rocket's CG / CP collars.</summary>
    public bool MarkersShown { get; private set; } = true;
    public event Action OverlaysChanged;  // Measuring or MarkersShown changed
    /// <summary>The design's demo (Fly / Hover / Run) is running, or getting ready to (CadViewer sets it).</summary>
    public bool DemoRunning { get; private set; }
    public event Action<bool> DemoRequested;     // a panel asks the viewer to start (true) or stop (false) the demo
    public event Action DemoChanged;             // DemoRunning changed
    public event Action<CadDemoFrame> DemoFrame; // every frame while a demo runs: phase and live readouts

    int mockPart = -1, mockIndex = -1;
    CadFlight cachedFlight;
    int cachedFlightVersion = -1;
    readonly List<int> mockHistory = new List<int>();
    bool fetching;
    CadModelUpdated pending;  // newest version announced while a fetch was running

    static string HubLabel => "Hub " + ForgeNet.Url("").TrimEnd('/');
    List<CadMockSet.Version> MockVersions => mocks.parts[mockPart].versions;

    /// <summary>The scene's session; created on first use, falling back to these mocks.</summary>
    public static CadSession Get(CadMockSet fallbackMocks)
    {
        if (Instance == null) Instance = FindAnyObjectByType<CadSession>();
        if (Instance == null) Instance = new GameObject("CadSession").AddComponent<CadSession>();
        if (Instance.mocks == null) Instance.mocks = fallbackMocks;
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void OnEnable()
    {
        ForgeEvents.OnCadModelUpdated += OnModelUpdated;
        ForgeEvents.OnCadView += OnViewEvent;
        ForgeEvents.OnCadRegenFailed += OnRegenFailed;
        ForgeNet.StatusChanged += OnNetStatus;
    }

    void OnDisable()
    {
        ForgeEvents.OnCadModelUpdated -= OnModelUpdated;
        ForgeEvents.OnCadView -= OnViewEvent;
        ForgeEvents.OnCadRegenFailed -= OnRegenFailed;
        ForgeNet.StatusChanged -= OnNetStatus;
    }

    IEnumerator Start()
    {
        IsMock = mode == Mode.Mock;
        if (mode == Mode.Auto)
        {
            // ForgeNet decides within its fallback time whether a hub is there.
            _ = ForgeNet.Instance;  // created on first use if the scene has none (sandboxes)
            while (ForgeNet.Status == ForgeNetStatus.Connecting) yield return null;
            IsMock = ForgeNet.IsMock;
        }
        Ready = true;
        if (IsMock)
        {
            if (mocks == null || mocks.parts.Count == 0) { SetStatus("No hub and no mocks."); yield break; }
            SetStatus("Mock mode (hub off): sliders jump to the nearest stored design.");
            RefreshParts();
            ShowMockPart(0);
        }
        else StartHub();
    }

    void StartHub()
    {
        IsMock = false;
        cachedFlight = null;  // the hub's versions may not be the mocks' copies
        SetStatus(HubLabel);
        RefreshParts();
        // The hub replays the current model to every client that connects, so ForgeNet usually has it already.
        if (ForgeNet.TryGetLast<CadView>(ForgeEvents.CadViewType, out var view) && view != null) ApplyView(view);
        if (ForgeNet.TryGetLast<CadModelUpdated>(ForgeEvents.CadModelUpdatedType, out var info) && info != null) OnModelUpdated(info);
        else StartCoroutine(FetchLatest());
    }

    // Commands (the same ones Bit's tools call on the hub)

    public void SetParams(Dictionary<string, object> changes)
    {
        if (IsMock) { ShowMockVersion(NearestMock(changes)); return; }
        SetStatus("Regenerating...");
        StartCoroutine(Post("/cad/params", JsonConvert.SerializeObject(changes)));
    }

    public void ApplyPreset(CadPreset preset)
    {
        if (preset == null) return;
        if (IsMock) { ShowMockVersion(NearestMock(preset.parameters)); return; }
        SetStatus($"Applying {preset.name}...");
        StartCoroutine(Post("/cad/preset", JsonConvert.SerializeObject(new { name = preset.name })));
    }

    /// <summary>Open another design from the library (its latest version comes back as a model update).</summary>
    public void OpenPart(string part)
    {
        if (string.IsNullOrEmpty(part) || part == Params?.part) return;
        if (IsMock)
        {
            int index = mocks.IndexOf(part);
            if (index < 0) { SetStatus($"No mock of {part}."); return; }
            ShowMockPart(index);
            return;
        }
        SetStatus("Opening...");
        StartCoroutine(Post("/cad/part", JsonConvert.SerializeObject(new { part })));
    }

    /// <summary>Re-read the library (a new part script shows up without a restart).</summary>
    public void RefreshParts()
    {
        if (IsMock)
        {
            if (mocks?.partList == null) return;
            Parts = JsonConvert.DeserializeObject<CadPartList>(mocks.partList.text);
            if (mockPart >= 0) MarkActive(mocks.parts[mockPart].part);
            PartsChanged?.Invoke();
            return;
        }
        StartCoroutine(GetText("/cad/parts", text =>
        {
            if (text == null) return;
            Parts = JsonConvert.DeserializeObject<CadPartList>(text);
            PartsChanged?.Invoke();
        }));
    }

    public void Undo()
    {
        if (!IsMock) { StartCoroutine(Post("/cad/undo", "{}")); return; }
        if (mockHistory.Count < 2) { SetStatus("Nothing to undo."); return; }
        mockHistory.RemoveAt(mockHistory.Count - 1);
        ShowMock(mockHistory[mockHistory.Count - 1], record: false);
    }

    public void ResetDesign()
    {
        if (IsMock) ShowMockVersion(0);
        else StartCoroutine(Post("/cad/reset", "{}"));
    }

    public void SetView(bool explode, string focusId)
    {
        var view = new CadView { explode = explode, focusId = focusId };
        ApplyView(view);
        if (!IsMock) StartCoroutine(Post("/cad/view", JsonConvert.SerializeObject(view), refresh: false));
    }

    /// <summary>Bring the model up big in front of you, or send it back to the desk (CadViewer flies it).</summary>
    public void SetInspecting(bool on)
    {
        if (on == Inspecting) return;
        Inspecting = on;
        InspectChanged?.Invoke();
    }

    /// <summary>Put every part taken off in the Inspect view back (CadTakeApart).</summary>
    public void Reassemble() => ReassembleRequested?.Invoke();

    public void SetPartsOff(int count)
    {
        if (count == PartsOff) return;
        PartsOff = count;
        InspectChanged?.Invoke();
    }

    public void SetMeasuring(bool on)
    {
        if (on == Measuring) return;
        Measuring = on;
        OverlaysChanged?.Invoke();
    }

    public void SetMarkersShown(bool on)
    {
        if (on == MarkersShown) return;
        MarkersShown = on;
        OverlaysChanged?.Invoke();
    }

    /// <summary>Start (true) or stop (false) the design's demo: Fly, Hover or Run (CadViewer runs it).</summary>
    public void RequestDemo(bool run) => DemoRequested?.Invoke(run);

    /// <summary>CadViewer: the demo started or ended.</summary>
    public void SetDemoRunning(bool on)
    {
        if (on == DemoRunning) return;
        DemoRunning = on;
        DemoChanged?.Invoke();
    }

    /// <summary>CadViewer: this frame of the running demo.</summary>
    public void PublishDemoFrame(CadDemoFrame frame) => DemoFrame?.Invoke(frame);

    public void RequestFlight()
    {
        if (Latest == null || Params == null || !Params.Has("flight")) return;
        if (IsMock)
        {
            var file = MockVersions[mockIndex].flight;
            if (file != null) FlightReady?.Invoke(JsonConvert.DeserializeObject<CadFlight>(file.text));
            return;
        }
        SetStatus("Simulating the flight...");
        StartCoroutine(GetText($"/cad/flight?v={Latest.info.version}", text =>
        {
            if (text == null) { SetStatus("Flight sim failed."); return; }
            SetStatus(HubLabel);
            FlightReady?.Invoke(JsonConvert.DeserializeObject<CadFlight>(text));
        }, timeout: 60));
    }

    /// <summary>
    /// A version's flight, for the launch demo: GET <paramref name="source"/>?v=N (the hub runs its sim on the first
    /// ask, a few seconds, then caches it) or the mock's stored flight.json. Null if there's none or it failed. Also
    /// raises FlightReady (the panel's apogee). The last one is kept, so flying the same version again is instant.
    /// </summary>
    public void GetFlight(int version, string source, Action<CadFlight> done)
    {
        if (cachedFlight != null && cachedFlightVersion == version)
        {
            done(cachedFlight);
            return;
        }
        if (IsMock)
        {
            var file = MockVersionOf(version)?.flight;
            CadFlight mock = null;
            if (file != null)
            {
                try { mock = JsonConvert.DeserializeObject<CadFlight>(file.text); }
                catch (JsonException e) { Debug.LogWarning($"[CadSession] Mock flight of v{version}: {e.Message}"); }
            }
            DeliverFlight(version, mock, done);
            return;
        }
        string path = string.IsNullOrEmpty(source) ? "/cad/flight" : source;
        path += (path.IndexOf('?') >= 0 ? "&v=" : "?v=") + version;
        SetStatus("Simulating the flight...");
        StartCoroutine(GetText(path, text =>
        {
            CadFlight flight = null;
            if (text != null)
            {
                try { flight = JsonConvert.DeserializeObject<CadFlight>(text); }
                catch (JsonException e) { Debug.LogWarning($"[CadSession] Flight of v{version}: {e.Message}"); }
            }
            SetStatus(flight != null ? HubLabel : "Flight sim failed.");
            DeliverFlight(version, flight, done);
        }, timeout: 60));
    }

    void DeliverFlight(int version, CadFlight flight, Action<CadFlight> done)
    {
        if (flight != null)
        {
            cachedFlight = flight;
            cachedFlightVersion = version;
            FlightReady?.Invoke(flight);
        }
        done(flight);
    }

    /// <summary>The stored mock version with this version number (mock labels end in /vN), or null.</summary>
    CadMockSet.Version MockVersionOf(int version)
    {
        if (mocks == null) return null;
        string suffix = "/v" + version;
        foreach (var part in mocks.parts)
            foreach (var v in part.versions)
                if (v.label.EndsWith(suffix, StringComparison.Ordinal)) return v;
        return null;
    }

    /// <summary>
    /// A version's engineering drawing as PNG bytes (the drawing panel): GET /cad/drawing.png?v=N, which the hub
    /// makes on the first request (a second or two) and then caches, or the mock's stored one. Null if there's none.
    /// </summary>
    public void GetDrawing(int version, Action<byte[]> done)
    {
        if (!IsMock)
        {
            StartCoroutine(GetBytes($"/cad/drawing.png?v={version}", done, timeout: 30));
            return;
        }
        string suffix = "/v" + version;
        foreach (var part in mocks.parts)
            foreach (var v in part.versions)
                if (v.label.EndsWith(suffix, StringComparison.Ordinal))
                {
                    done(v.drawing != null ? v.drawing.bytes : null);
                    return;
                }
        done(null);
    }

    public void StepMock(int delta)
    {
        if (IsMock) ShowMockVersion(Mathf.Clamp(mockIndex + delta, 0, MockVersions.Count - 1));
    }

    public void ShowMockVersion(int index)
    {
        if (IsMock && mockPart >= 0 && index >= 0 && index < MockVersions.Count && index != mockIndex) ShowMock(index, record: true);
    }

    /// <summary>Open the design after the current one (sandbox key P).</summary>
    public void NextPart()
    {
        if (Parts.parts.Count == 0) return;
        int i = Parts.parts.FindIndex(p => p.part == Params?.part);
        OpenPart(Parts.parts[(i + 1) % Parts.parts.Count].part);
    }

    // Hub

    void OnModelUpdated(CadModelUpdated info)
    {
        if (!Ready || IsMock || info == null) return;
        if (Latest != null && Latest.info.version == info.version) return;
        if (fetching) { pending = info; return; }
        StartCoroutine(Fetch(info));
    }

    void OnViewEvent(CadView view)
    {
        if (IsMock || view == null || (view.explode == View.explode && view.focusId == View.focusId)) return;
        ApplyView(view);
    }

    void OnRegenFailed(CadRegenFailed failure)
    {
        if (IsMock || failure == null) return;
        SetStatus(failure.errorLine.HasValue ? $"{failure.error} (part script line {failure.errorLine})" : failure.error);
        ParamsChanged?.Invoke();  // snap the sliders back to the version that's still up
    }

    void OnNetStatus(ForgeNetStatus status)
    {
        if (!Ready || mode != Mode.Auto) return;
        if (status == ForgeNetStatus.Live && IsMock) StartHub();          // hub appeared: go live
        else if (status != ForgeNetStatus.Live && !IsMock) SetStatus($"{HubLabel} lost; retrying...");
        else if (status == ForgeNetStatus.Live) SetStatus(HubLabel);
    }

    /// <summary>Download one version's model, metadata and parameter list, then show it.</summary>
    IEnumerator Fetch(CadModelUpdated info)
    {
        fetching = true;
        VersionComing?.Invoke(info);
        byte[] glb = null;
        string metadata = null, parameters = null;
        yield return GetBytes($"/cad/model.glb?v={info.version}", b => glb = b);
        yield return GetText($"/cad/metadata.json?v={info.version}", t => metadata = t);
        yield return GetText($"/cad/params?v={info.version}", t => parameters = t);
        if (glb != null && metadata != null)
        {
            bool newPart = Params == null || (info.part != null && info.part != Params.part);
            if (parameters != null) Params = JsonConvert.DeserializeObject<CadParams>(parameters);
            if (Status == "Regenerating..." || Status == "Opening..." || Status.StartsWith("Applying")) SetStatus(HubLabel);
            Publish(new CadUpdate { info = info, glb = glb, metadata = metadata });
            if (newPart)
            {
                MarkActive(Params?.part);
                RefreshParts();  // its version number, and anything added to the library meanwhile
            }
        }
        fetching = false;

        if (pending != null)
        {
            var next = pending;
            pending = null;
            OnModelUpdated(next);
        }
    }

    /// <summary>Fallback when there's no event to go on (first load, or the socket is down): ask the hub.</summary>
    IEnumerator FetchLatest()
    {
        string latestJson = null;
        yield return GetText("/cad/latest", t => latestJson = t);
        if (latestJson == null) yield break;
        var latest = JObject.Parse(latestJson);
        var view = latest["cad.view"]?.ToObject<CadView>();
        if (view != null) OnViewEvent(view);
        var info = latest["cad.model_updated"]?.ToObject<CadModelUpdated>();
        if (info != null) OnModelUpdated(info);
    }

    IEnumerator Post(string path, string json, bool refresh = true)
    {
        string error = null;
        using (var req = new UnityWebRequest(ForgeNet.Url(path), "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 30;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) error = ErrorText(req);
        }
        if (error != null)
        {
            SetStatus(error);
            ParamsChanged?.Invoke();  // snap the sliders back to the version that's still up
            yield break;
        }
        // Live, the hub's cad.model_updated event brings the new version; without the socket, ask for it.
        if (refresh && ForgeNet.Status != ForgeNetStatus.Live) yield return FetchLatest();
    }

    IEnumerator GetText(string path, Action<string> done, int timeout = 10)
    {
        using (var req = UnityWebRequest.Get(ForgeNet.Url(path)))
        {
            req.timeout = timeout;
            yield return req.SendWebRequest();
            done(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null);
        }
    }

    IEnumerator GetBytes(string path, Action<byte[]> done, int timeout = 10)
    {
        using (var req = UnityWebRequest.Get(ForgeNet.Url(path)))
        {
            req.timeout = timeout;
            yield return req.SendWebRequest();
            done(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.data : null);
        }
    }

    static string ErrorText(UnityWebRequest req)
    {
        try
        {
            var detail = JObject.Parse(req.downloadHandler.text)["detail"];
            if (detail is JObject failure)
            {
                var f = failure.ToObject<CadRegenFailed>();
                return f.errorLine.HasValue ? $"{f.error} (part script line {f.errorLine})" : f.error;
            }
            if (detail != null) return detail.ToString();
        }
        catch (JsonException) { }
        return req.error;
    }

    // Mocks

    void ShowMockPart(int part)
    {
        mockPart = part;
        mockIndex = -1;
        mockHistory.Clear();
        ApplyView(new CadView { explode = false });  // part IDs differ per design
        ShowMock(0, record: true);
        MarkActive(mocks.parts[part].part);
    }

    void ShowMock(int index, bool record)
    {
        var info = MockEnvelope(index);
        if (mockIndex >= 0 && index != mockIndex + 1) MockDiff(mockIndex, index, info);
        info.previousVersion = mockIndex >= 0 ? MockEnvelope(mockIndex).version : (int?)null;
        mockIndex = index;
        if (record) mockHistory.Add(index);
        var v = MockVersions[index];
        Params = JsonConvert.DeserializeObject<CadParams>(v.parameters.text);
        VersionComing?.Invoke(info);
        Publish(new CadUpdate { info = info, glb = v.glb.bytes, metadata = v.metadata.text });
    }

    CadModelUpdated MockEnvelope(int index) =>
        JsonConvert.DeserializeObject<CadEnvelope<CadModelUpdated>>(MockVersions[index].modelUpdated.text).payload;

    /// <summary>Each mock's changed_ids is relative to the one before it; recompute them for other jumps.</summary>
    void MockDiff(int from, int to, CadModelUpdated info)
    {
        var fromIds = MockObjectIds(from);
        var toIds = MockObjectIds(to);
        var changed = new HashSet<string>(toIds.Except(fromIds));
        for (int i = Math.Min(from, to) + 1; i <= Math.Max(from, to); i++) changed.UnionWith(MockEnvelope(i).changedIds);
        changed.IntersectWith(toIds);
        info.changedIds = changed.ToList();
        info.removedIds = fromIds.Except(toIds).ToList();
    }

    HashSet<string> MockObjectIds(int index) => new HashSet<string>(
        JObject.Parse(MockVersions[index].metadata.text)["objects"].Select(o => (string)o["id"]));

    /// <summary>
    /// The stored design that best has what was asked for: closest on the changed parameters first,
    /// then closest to the current design on the rest.
    /// </summary>
    int NearestMock(Dictionary<string, object> changes)
    {
        int best = mockIndex;
        double bestScore = double.MaxValue;
        for (int i = 0; i < MockVersions.Count; i++)
        {
            var candidate = MockEnvelope(i).parameters;
            double score = 0;
            foreach (var p in Params.parameters)
            {
                bool changed = changes.TryGetValue(p.name, out var wanted);
                if (!changed) wanted = p.value;
                if (!candidate.TryGetValue(p.name, out var have)) continue;
                double d = p.IsOptions
                    ? (Equals(wanted?.ToString(), have?.ToString()) ? 0 : 1)
                    : Math.Abs(Convert.ToDouble(wanted) - Convert.ToDouble(have)) / Math.Max(1e-6, (p.max ?? 1) - (p.min ?? 0));
                score += changed ? d * 1000 : d;
            }
            if (score < bestScore - 1e-9) { bestScore = score; best = i; }
        }
        if (best == mockIndex) { SetStatus("Mock mode: no stored design is closer; showing the current one."); ParamsChanged?.Invoke(); }
        return best;
    }

    // Shared

    void MarkActive(string part)
    {
        if (string.IsNullOrEmpty(part)) return;
        Parts.active = part;
        foreach (var p in Parts.parts) p.active = p.part == part;
        PartsChanged?.Invoke();
    }

    void Publish(CadUpdate update)
    {
        Latest = update;
        ModelChanged?.Invoke(update);
        ParamsChanged?.Invoke();
    }

    void ApplyView(CadView view)
    {
        View = view;
        ViewChanged?.Invoke(view);
    }

    void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }
}
