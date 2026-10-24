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
    public CadView View { get; private set; } = new CadView { explode = false };
    public string Status { get; private set; } = "Connecting to the hub...";
    public int MockIndex => mockIndex;

    public event Action<CadUpdate> ModelChanged;
    public event Action ParamsChanged;
    public event Action<CadView> ViewChanged;
    public event Action<string> StatusChanged;
    public event Action<CadFlight> FlightReady;

    int mockIndex = -1;
    readonly List<int> mockHistory = new List<int>();
    bool fetching;
    CadModelUpdated pending;  // newest version announced while a fetch was running

    static string HubLabel => "Hub " + ForgeNet.Url("").TrimEnd('/');

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
            if (mocks == null || mocks.versions.Count == 0) { SetStatus("No hub and no mocks."); yield break; }
            SetStatus("Mock mode (hub off): sliders jump to the nearest stored design.");
            ShowMockVersion(0);
        }
        else StartHub();
    }

    void StartHub()
    {
        IsMock = false;
        SetStatus(HubLabel);
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

    public void RequestFlight()
    {
        if (Latest == null) return;
        if (IsMock)
        {
            var file = mocks.versions[mockIndex].flight;
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

    public void StepMock(int delta)
    {
        if (IsMock) ShowMockVersion(Mathf.Clamp(mockIndex + delta, 0, mocks.versions.Count - 1));
    }

    public void ShowMockVersion(int index)
    {
        if (IsMock && index >= 0 && index < mocks.versions.Count && index != mockIndex) ShowMock(index, record: true);
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

    /// <summary>Download one version's model, metadata and the current parameter list, then show it.</summary>
    IEnumerator Fetch(CadModelUpdated info)
    {
        fetching = true;
        byte[] glb = null;
        string metadata = null, parameters = null;
        yield return GetBytes($"/cad/model.glb?v={info.version}", b => glb = b);
        yield return GetText($"/cad/metadata.json?v={info.version}", t => metadata = t);
        yield return GetText("/cad/params", t => parameters = t);
        if (glb != null && metadata != null)
        {
            if (parameters != null) Params = JsonConvert.DeserializeObject<CadParams>(parameters);
            if (Status == "Regenerating...") SetStatus(HubLabel);
            Publish(new CadUpdate { info = info, glb = glb, metadata = metadata });
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

    IEnumerator GetBytes(string path, Action<byte[]> done)
    {
        using (var req = UnityWebRequest.Get(ForgeNet.Url(path)))
        {
            req.timeout = 10;
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

    void ShowMock(int index, bool record)
    {
        var info = MockEnvelope(index);
        if (mockIndex >= 0 && index != mockIndex + 1) MockDiff(mockIndex, index, info);
        info.previousVersion = mockIndex >= 0 ? MockEnvelope(mockIndex).version : (int?)null;
        mockIndex = index;
        if (record) mockHistory.Add(index);
        var v = mocks.versions[index];
        Params = JsonConvert.DeserializeObject<CadParams>(v.parameters.text);
        Publish(new CadUpdate { info = info, glb = v.glb.bytes, metadata = v.metadata.text });
    }

    CadModelUpdated MockEnvelope(int index) =>
        JsonConvert.DeserializeObject<CadEnvelope<CadModelUpdated>>(mocks.versions[index].modelUpdated.text).payload;

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
        JObject.Parse(mocks.versions[index].metadata.text)["objects"].Select(o => (string)o["id"]));

    /// <summary>
    /// The stored design that best has what was asked for: closest on the changed parameters first,
    /// then closest to the current design on the rest.
    /// </summary>
    int NearestMock(Dictionary<string, object> changes)
    {
        int best = mockIndex;
        double bestScore = double.MaxValue;
        for (int i = 0; i < mocks.versions.Count; i++)
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
