using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Live hub connection over System.Net.WebSockets.ClientWebSocket (works on Android/IL2CPP).
    /// Socket I/O runs on a background Task; parsed events are queued and raised on the main
    /// thread in Update. Reconnects with 1,2,4..16 s backoff while enabled. Sends hello on connect.
    /// Binary frames from the hub (voice-mode speech, PCM16) are raised as BinaryReceived on the main thread,
    /// in the same order as the text events around them (tts.start → frames → tts.end).
    /// Android builds need Player Settings → Internet Access = Require.
    /// </summary>
    public class AgentClient : MonoBehaviour, IPetEventSource
    {
        struct Outgoing { public byte[] data; public int length; public bool binary; }
        struct Incoming { public PetEvent evt; public byte[] data; public int length; }   // evt == null → binary

        [SerializeField, Tooltip("Receive buffer size in bytes (frames larger than this are assembled from fragments).")]
        int receiveBufferSize = 16 * 1024;
        [SerializeField, Tooltip("Max backoff between reconnect attempts, seconds.")]
        int maxBackoffSeconds = 16;

        public event Action<PetEvent> EventReceived;
        /// <summary>Raised on the main thread when IsConnected flips.</summary>
        public event Action<bool> ConnectionChanged;
        /// <summary>Binary frame from the hub (main thread). The buffer is pooled and reused after the call:
        /// handlers must copy what they need before returning. Valid bytes = [0, length).</summary>
        public event Action<byte[], int> BinaryReceived;

        public bool IsConnected => _connected;
        /// <summary>The hub announced a speech-to-speech session for this socket (voice.ready); replies come as tts.* + audio.</summary>
        public bool VoiceActive { get; private set; }
        /// <summary>Sample rate from voice.ready (0 until then).</summary>
        public int VoiceSampleRate { get; private set; }
        /// <summary>Human-readable connection status for UI ("connected", "connecting…", "offline: …").</summary>
        public string Status => _status;

        readonly ConcurrentQueue<Incoming> _inbox = new ConcurrentQueue<Incoming>();
        readonly ConcurrentQueue<Outgoing> _outbox = new ConcurrentQueue<Outgoing>();
        readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0);

        PetConfig _cfg;
        CancellationTokenSource _cts;
        volatile bool _connected;
        volatile string _status = "idle";
        bool _lastReportedConnected;
        bool _warnedDrop;
        int _audioOutRate;      // AudioSettings.outputSampleRate, read on the main thread for hello

        // ---------------- public API ----------------

        public void Connect(PetConfig cfg)
        {
            _cfg = cfg ?? new PetConfig();
            _audioOutRate = AudioSettings.outputSampleRate;
            Disconnect();
            if (!isActiveAndEnabled) return;              // OnEnable will start it
            if (!Uri.TryCreate(_cfg.hubUrl, UriKind.Absolute, out var uri)) { _status = "bad hubUrl: " + _cfg.hubUrl; Debug.LogError("[AgentClient] " + _status); return; }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunAsync(uri, token));
        }

        public void Disconnect()
        {
            if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; }
            _connected = false;
            _status = "disconnected";
            VoiceActive = false; VoiceSampleRate = 0;
        }

        /// <summary>Queues a JSON text frame. Dropped (with one warning) while offline.</summary>
        public void Send(string type, object payload)
        {
            if (!_connected) { WarnDrop(type); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(PetEvent.Build(type, payload));
            Enqueue(new Outgoing { data = bytes, length = bytes.Length, binary = false });
        }

        /// <summary>Queues a binary frame (audio). Copies into a pooled buffer, so the caller may reuse <paramref name="buf"/>.</summary>
        public void SendAudio(byte[] buf, int length)
        {
            if (!_connected || buf == null || length <= 0) return;
            byte[] copy = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(buf, 0, copy, 0, length);
            Enqueue(new Outgoing { data = copy, length = length, binary = true });
        }

        // ---------------- Unity lifecycle ----------------

        void OnEnable() { if (_cfg != null && _cts == null) Connect(_cfg); }
        void OnDisable() => Disconnect();
        void OnDestroy() => Disconnect();
        void OnApplicationQuit() => Disconnect();

        void Update()
        {
            if (_lastReportedConnected != _connected)
            {
                _lastReportedConnected = _connected;
                if (!_lastReportedConnected) { VoiceActive = false; VoiceSampleRate = 0; }
                ConnectionChanged?.Invoke(_lastReportedConnected);
            }
            while (_inbox.TryDequeue(out var item))
            {
                if (item.evt == null)
                {
                    try { BinaryReceived?.Invoke(item.data, item.length); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    finally { ArrayPool<byte>.Shared.Return(item.data); }
                    continue;
                }
                if (item.evt.type == "voice.ready") { VoiceActive = true; VoiceSampleRate = item.evt.GetInt("sample_rate"); }
                try { EventReceived?.Invoke(item.evt); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        // ---------------- background ----------------

        void Enqueue(Outgoing o) { _outbox.Enqueue(o); _sendSignal.Release(); }

        void WarnDrop(string type)
        {
            if (_warnedDrop) return;
            _warnedDrop = true;
            Debug.LogWarning("[AgentClient] Not connected; dropping outbound '" + type + "' (further drops silent until connected).");
        }

        async Task RunAsync(Uri uri, CancellationToken ct)
        {
            int backoff = 1;
            bool loggedFailure = false;
            while (!ct.IsCancellationRequested)
            {
                _status = "connecting to " + uri + "…";
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                        DrainOutbox();
                        // hello goes first, before Send() is allowed.
                        var hello = Encoding.UTF8.GetBytes(PetEvent.Build("hello", new JObject
                        { ["client"] = "unity", ["pet_id"] = _cfg.petId, ["project_id"] = _cfg.projectId, ["audio_out_rate"] = _audioOutRate }));
                        Enqueue(new Outgoing { data = hello, length = hello.Length, binary = false });
                        _connected = true; _warnedDrop = false; loggedFailure = false; backoff = 1;
                        _status = "connected";
                        Debug.Log("[AgentClient] Connected to " + uri);

                        using (var session = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            var recv = ReceiveLoop(ws, session.Token);
                            var send = SendLoop(ws, session.Token);
                            var first = await Task.WhenAny(recv, send).ConfigureAwait(false);
                            session.Cancel();
                            try { await Task.WhenAll(recv, send).ConfigureAwait(false); } catch { /* ending session */ }
                            if (first.IsFaulted) throw first.Exception.GetBaseException();
                        }
                        if (!ct.IsCancellationRequested) Debug.Log("[AgentClient] Disconnected from hub; reconnecting with backoff.");
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        if (_connected) Debug.LogWarning("[AgentClient] Connection lost: " + e.Message + " — reconnecting.");
                        else if (!loggedFailure)
                        {
                            loggedFailure = true;
                            Debug.LogWarning("[AgentClient] Can't reach " + uri + " (" + e.Message + "); retrying quietly with backoff (1.." + maxBackoffSeconds + " s).");
                        }
                        _status = "offline: " + e.Message;
                    }
                    catch { /* cancelled */ }
                    finally
                    {
                        // A cancelled (stale) task must not clobber the state of a newer Connect().
                        if (!ct.IsCancellationRequested) { _connected = false; DrainOutbox(); }
                    }
                }
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(backoff * 1000, ct).ConfigureAwait(false); } catch { break; }
                backoff = Math.Min(backoff * 2, Math.Max(1, maxBackoffSeconds));
            }
        }

        async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[Math.Max(1024, receiveBufferSize)];
            var segment = new ArraySegment<byte>(buf);
            using (var ms = new MemoryStream())
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;                 // assemble fragments
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                        var e = PetEvent.Parse(json);
                        if (e != null) _inbox.Enqueue(new Incoming { evt = e });
                    }
                    else if (r.MessageType == WebSocketMessageType.Binary && ms.Length > 0)
                    {
                        int len = (int)ms.Length;
                        byte[] copy = ArrayPool<byte>.Shared.Rent(len);   // returned after dispatch in Update
                        Buffer.BlockCopy(ms.GetBuffer(), 0, copy, 0, len);
                        _inbox.Enqueue(new Incoming { data = copy, length = len });
                    }
                    ms.SetLength(0);
                }
            }
        }

        async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _sendSignal.WaitAsync(ct).ConfigureAwait(false);
                if (!_outbox.TryDequeue(out var o)) continue;
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(o.data, 0, o.length),
                        o.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                finally { if (o.binary) ArrayPool<byte>.Shared.Return(o.data); }
            }
        }

        void DrainOutbox()
        {
            while (_outbox.TryDequeue(out var o)) if (o.binary) ArrayPool<byte>.Shared.Return(o.data);
        }
    }
}
