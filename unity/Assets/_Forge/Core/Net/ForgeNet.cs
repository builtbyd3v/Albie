using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public enum ForgeNetMode { Auto, Live, Mock }
public enum ForgeNetStatus { Connecting, Live, Mock }

/// <summary>
/// Unity's connection to the hub (PROJECT_CONTEXT section 7): the <c>/ws</c> event bus plus REST helpers.
/// <list type="bullet">
/// <item>Events: <see cref="ForgeEvents"/> (one static event per type) or <see cref="On"/> for any type. Raised on the main thread.</item>
/// <item>REST: <see cref="Get"/>, <see cref="GetBytes"/>, <see cref="Post"/>, or <see cref="Url"/> to hand a URL to a loader (glTFast).</item>
/// <item>Sending on the socket: <see cref="Send"/> (JSON event) and <see cref="SendBinary"/> (raw bytes, e.g. mic audio);
/// binary frames from the hub arrive as <see cref="BinaryReceived"/>, in order with the events around them.</item>
/// <item>Mocks: in <see cref="ForgeNetMode.Auto"/> it falls back to mock mode when no hub answers. Then REST reads
/// <c>Resources/Mocks/&lt;path&gt;</c> (or a <see cref="MockRest"/> handler) and <see cref="ForgeMockPlayer"/> plays events.</item>
/// </list>
/// One per scene. If none is placed, the first call creates one with default settings, so sandbox scenes just work.
/// </summary>
[DefaultExecutionOrder(-900)]
public class ForgeNet : MonoBehaviour
{
    public const string HubUrlPref = "Forge.HubUrl";

    [Tooltip("Hub base URL. On the Quest use the laptop's LAN IP, e.g. http://192.168.1.20:8000. A PlayerPrefs value under Forge.HubUrl overrides this.")]
    public string hubUrl = "http://localhost:8000";
    public ForgeNetMode mode = ForgeNetMode.Auto;
    [Tooltip("Auto: seconds without a hub before switching to mocks. It keeps retrying and goes live when the hub appears.")]
    public float mockFallbackSeconds = 3f;
    [Tooltip("Seconds before a REST call gives up.")]
    public int requestTimeout = 15;
    public bool logEvents;

    static ForgeNet instance;

    // Socket frames in arrival order: text (JSON event) or binary (pooled buffer, returned after dispatch).
    struct Frame { public bool binary; public string text; public byte[] data; public int length; }

    readonly ConcurrentQueue<Frame> inbox = new ConcurrentQueue<Frame>();
    readonly ConcurrentQueue<Frame> outbox = new ConcurrentQueue<Frame>();
    readonly SemaphoreSlim outboxSignal = new SemaphoreSlim(0);
    readonly Dictionary<string, List<Action<JToken>>> handlers = new Dictionary<string, List<Action<JToken>>>();
    readonly Dictionary<string, JToken> last = new Dictionary<string, JToken>();
    readonly Dictionary<string, Func<string, string>> mockRest = new Dictionary<string, Func<string, string>>();
    CancellationTokenSource cancel;
    volatile bool socketOpen;
    volatile string socketError;
    float startTime;

    public static ForgeNet Instance
    {
        get
        {
            if (instance == null && Application.isPlaying)
            {
                instance = FindAnyObjectByType<ForgeNet>();
                if (instance == null) instance = new GameObject("ForgeNet").AddComponent<ForgeNet>();
            }
            return instance;
        }
    }

    public static ForgeNetStatus Status { get; private set; } = ForgeNetStatus.Connecting;
    public static bool IsMock => Status == ForgeNetStatus.Mock;
    public static event Action<ForgeNetStatus> StatusChanged;
    /// <summary>Binary frame from the hub, on the main thread. The buffer is reused after the call: copy what you need.
    /// Valid bytes are [0, length).</summary>
    public static event Action<byte[], int> BinaryReceived;

    // ---- Events ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Call <paramref name="handler"/> for every event of <paramref name="type"/> ("*" = all types). Dispose the result
    /// to unsubscribe. With <paramref name="replayLast"/>, it's also called right away with the latest one received,
    /// for panels that appear after the hub sent the current state.
    /// </summary>
    public static IDisposable On(string type, Action<JToken> handler, bool replayLast = false)
    {
        var net = Instance;
        if (!net.handlers.TryGetValue(type, out var list)) net.handlers[type] = list = new List<Action<JToken>>();
        list.Add(handler);
        if (replayLast && net.last.TryGetValue(type, out var payload)) SafeCall(handler, payload);
        return new Subscription(() => list.Remove(handler));
    }

    /// <summary>Typed <see cref="On(string, Action{JToken}, bool)"/>: the payload is converted to <typeparamref name="T"/>.</summary>
    public static IDisposable On<T>(string type, Action<T> handler, bool replayLast = false) =>
        On(type, payload => handler(payload.ToObject<T>()), replayLast);

    /// <summary>The latest payload received for <paramref name="type"/>, if any.</summary>
    public static bool TryGetLast<T>(string type, out T payload)
    {
        payload = default;
        if (instance == null || !instance.last.TryGetValue(type, out var token)) return false;
        payload = token.ToObject<T>();
        return true;
    }

    /// <summary>Send a JSON event <c>{"type", "payload"}</c> to the hub over <c>/ws</c>. Dropped (returns false) unless Live.</summary>
    public static bool Send(string type, object payload)
    {
        if (Status != ForgeNetStatus.Live || string.IsNullOrEmpty(type)) return false;
        var message = new JObject { ["type"] = type, ["payload"] = payload as JToken ?? (payload == null ? new JObject() : JToken.FromObject(payload)) };
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
        Instance.Enqueue(new Frame { data = bytes, length = bytes.Length });
        return true;
    }

    /// <summary>Send raw bytes as one binary frame (copied, so the caller may reuse the buffer). Dropped unless Live.</summary>
    public static bool SendBinary(byte[] data, int offset, int count)
    {
        if (Status != ForgeNetStatus.Live || data == null || count <= 0) return false;
        byte[] copy = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(data, offset, copy, 0, count);
        Instance.Enqueue(new Frame { binary = true, data = copy, length = count });
        return true;
    }

    /// <summary>Dispatch an event locally, as if the hub had sent it (mocks, tests, editor tools).</summary>
    public static void Emit(string type, object payload) =>
        Instance.Dispatch(type, payload as JToken ?? (payload == null ? JValue.CreateNull() : JToken.FromObject(payload)));

    // ---- REST -----------------------------------------------------------------------------------------------

    public static string Url(string path) => Instance.BaseUrl + (path.StartsWith("/") ? path : "/" + path);

    public static Coroutine Get(string path, Action<string> ok, Action<string> fail = null) =>
        Instance.StartCoroutine(Instance.Request("GET", path, null, false, (text, _) => ok?.Invoke(text), fail));

    public static Coroutine Get<T>(string path, Action<T> ok, Action<string> fail = null) =>
        Get(path, text => ok?.Invoke(JsonConvert.DeserializeObject<T>(text)), fail);

    public static Coroutine GetBytes(string path, Action<byte[]> ok, Action<string> fail = null) =>
        Instance.StartCoroutine(Instance.Request("GET", path, null, true, (_, bytes) => ok?.Invoke(bytes), fail));

    public static Coroutine Post(string path, object body, Action<string> ok = null, Action<string> fail = null) =>
        Instance.StartCoroutine(Instance.Request("POST", path, body == null ? "{}" : JsonConvert.SerializeObject(body), false, (text, _) => ok?.Invoke(text), fail));

    public static Coroutine Post<T>(string path, object body, Action<T> ok, Action<string> fail = null) =>
        Post(path, body, text => ok?.Invoke(JsonConvert.DeserializeObject<T>(text)), fail);

    /// <summary>Mock mode: answer GETs for <paramref name="path"/> (no query string) with <paramref name="json"/>(full path).</summary>
    public static void MockRest(string path, Func<string, string> json) => Instance.mockRest[path] = json;

    // ---- Lifecycle ------------------------------------------------------------------------------------------

    string BaseUrl
    {
        get
        {
            string url = PlayerPrefs.GetString(HubUrlPref, "");
            return (string.IsNullOrEmpty(url) ? hubUrl : url).TrimEnd('/');
        }
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[ForgeNet] More than one ForgeNet in the scene; keeping the first.");
            Destroy(this);
            return;
        }
        instance = this;
        Status = ForgeNetStatus.Connecting;
    }

    void OnEnable()
    {
        startTime = Time.realtimeSinceStartup;
        if (mode == ForgeNetMode.Mock) return;
        cancel = new CancellationTokenSource();
        string ws = BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/ws";
        _ = RunSocket(new Uri(ws), cancel.Token);
    }

    void OnDisable()
    {
        cancel?.Cancel();  // also ends the socket task when leaving Play mode in the editor
        cancel = null;
        socketOpen = false;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    void Update()
    {
        UpdateStatus();
        // Drain a bounded number per frame so a burst can't stall a frame.
        for (int i = 0; i < 64 && inbox.TryDequeue(out Frame frame); i++)
        {
            if (frame.binary)
            {
                try { BinaryReceived?.Invoke(frame.data, frame.length); }
                catch (Exception e) { Debug.LogException(e); }
                finally { ArrayPool<byte>.Shared.Return(frame.data); }
                continue;
            }
            string message = frame.text;
            JObject json;
            try { json = JObject.Parse(message); }
            catch (Exception e)
            {
                Debug.LogWarning($"[ForgeNet] Bad message from hub: {e.Message}\n{message}");
                continue;
            }
            Dispatch((string)json["type"], json["payload"]);
        }
    }

    void UpdateStatus()
    {
        ForgeNetStatus next;
        if (mode == ForgeNetMode.Mock) next = ForgeNetStatus.Mock;
        else if (socketOpen) next = ForgeNetStatus.Live;
        else if (mode == ForgeNetMode.Auto && Time.realtimeSinceStartup - startTime > mockFallbackSeconds) next = ForgeNetStatus.Mock;
        else next = ForgeNetStatus.Connecting;
        if (next == Status) return;

        Status = next;
        string why = next == ForgeNetStatus.Mock && mode == ForgeNetMode.Auto
            ? $" (no hub at {BaseUrl}{(socketError != null ? ": " + socketError : "")}; still retrying)" : "";
        Debug.Log($"[ForgeNet] {next}{(next == ForgeNetStatus.Live ? " at " + BaseUrl : "")}{why}");
        StatusChanged?.Invoke(next);
    }

    void Dispatch(string type, JToken payload)
    {
        if (string.IsNullOrEmpty(type)) return;
        last[type] = payload;
        if (logEvents) Debug.Log($"[ForgeNet] {type} {payload?.ToString(Formatting.None)}");
        ForgeEvents.Raise(type, payload);
        CallAll(type, payload);
        CallAll("*", payload);
    }

    void CallAll(string type, JToken payload)
    {
        if (!handlers.TryGetValue(type, out var list) || list.Count == 0) return;
        foreach (var handler in list.ToArray()) SafeCall(handler, payload);
    }

    static void SafeCall(Action<JToken> handler, JToken payload)
    {
        try { handler(payload); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void Enqueue(Frame frame)
    {
        outbox.Enqueue(frame);
        outboxSignal.Release();
    }

    // Outgoing frames queued while disconnected are stale (old audio, old asks): drop them.
    void DrainOutbox()
    {
        while (outbox.TryDequeue(out Frame frame))
            if (frame.binary) ArrayPool<byte>.Shared.Return(frame.data);
    }

    // Writes queued frames while the socket is open (one sender at a time, as ClientWebSocket requires).
    async Task SendLoop(ClientWebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await outboxSignal.WaitAsync(token);
            if (!outbox.TryDequeue(out Frame frame)) continue;
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(frame.data, 0, frame.length),
                    frame.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, token);
            }
            finally { if (frame.binary) ArrayPool<byte>.Shared.Return(frame.data); }
        }
    }

    // Background task: connect, read messages into the inbox, reconnect every 2 s after a drop.
    async Task RunSocket(Uri uri, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested)
        {
            using (var socket = new ClientWebSocket())
            using (var sending = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task sender = null;
                try
                {
                    using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connectTimeout.CancelAfter(3000);
                        await socket.ConnectAsync(uri, connectTimeout.Token);
                    }
                    socketError = null;
                    DrainOutbox();
                    socketOpen = true;
                    sender = SendLoop(socket, sending.Token);
                    var message = new MemoryStream();
                    while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        message.SetLength(0);
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            int length = (int)message.Length;
                            byte[] data = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                            Buffer.BlockCopy(message.GetBuffer(), 0, data, 0, length);
                            inbox.Enqueue(new Frame { binary = true, data = data, length = length });
                        }
                        else inbox.Enqueue(new Frame { text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) });
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception e) { socketError = e.InnerException?.Message ?? e.Message; }
                finally
                {
                    socketOpen = false;
                    sending.Cancel();  // the sender must not outlive this socket
                    if (sender != null)
                    {
                        try { await sender; }
                        catch (Exception) { }  // cancelled, or the socket died mid-send
                    }
                    DrainOutbox();
                }
            }
            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) { }
        }
    }

    IEnumerator Request(string method, string path, string jsonBody, bool binary, Action<string, byte[]> ok, Action<string> fail)
    {
        fail = fail ?? (error => Debug.LogWarning($"[ForgeNet] {method} {path} failed: {error}"));

        if (IsMock)
        {
            yield return null;  // keep callbacks asynchronous, like a real request
            if (method != "GET")
            {
                ok("{}", null);
                yield break;
            }
            string key = path.Split('?')[0];
            if (mockRest.TryGetValue(key, out var handler)) ok(handler(path), null);
            else
            {
                var asset = Resources.Load<TextAsset>("Mocks/" + key.TrimStart('/'));
                if (asset != null) ok(asset.text, asset.bytes);
                else fail($"no mock for {key} (add Resources/Mocks{key}{(binary ? ".bytes" : ".json")} or ForgeNet.MockRest)");
            }
            yield break;
        }

        using (var request = new UnityWebRequest(Url(path), method))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            if (jsonBody != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.timeout = requestTimeout;
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success) ok(request.downloadHandler.text, request.downloadHandler.data);
            else fail($"{request.responseCode} {request.error} {request.downloadHandler?.text}");
        }
    }

    sealed class Subscription : IDisposable
    {
        Action unsubscribe;
        public Subscription(Action unsubscribe) => this.unsubscribe = unsubscribe;
        public void Dispose()
        {
            unsubscribe?.Invoke();
            unsubscribe = null;
        }
    }
}
