using System;
using System.Collections;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;

/// <summary>
/// Shows a laptop window, streamed over WebRTC by screen-bridge/index.html, on this object's <see cref="ScreenSurface"/>.
/// Signaling goes through screen-bridge/signaling.py: we send "ready", the page sends an offer,
/// we reply with an answer. ICE candidates ride inside the SDP, so there are no candidate messages.
/// </summary>
public class ScreenReceiver : MonoBehaviour
{
    [Tooltip("screen-bridge/signaling.py. On the Quest, use the laptop's IP instead of localhost.")]
    public string signalingUrl = "ws://localhost:8766";

    [Tooltip("No video after this many seconds: switch to a JpegScreenReceiver on this object, if there is one.")]
    public float jpegFallbackSeconds = 10f;

    [Serializable]
    struct Message
    {
        public string type;
        public string sdp;
    }

    ClientWebSocket ws;
    RTCPeerConnection pc;
    ScreenSurface surface;
    bool destroyed;

    async void Start()
    {
        surface = GetComponent<ScreenSurface>();
        Invoke(nameof(FallBackToJpeg), jpegFallbackSeconds);
        StartCoroutine(WebRTC.Update());

        // Reconnect forever, so the panel recovers when signaling.py restarts.
        while (!destroyed)
        {
            using (ws = new ClientWebSocket())
            {
                try
                {
                    await ws.ConnectAsync(new Uri(signalingUrl), default);
                    Debug.Log("[ScreenReceiver] Connected to " + signalingUrl);
                    Send("ready", null);
                    await ReceiveLoop();
                }
                catch (Exception e)
                {
                    if (!destroyed) Debug.LogWarning("[ScreenReceiver] Signaling: " + e.Message);
                }
            }
            await Task.Delay(2000);
        }
    }

    // Awaits resume on the main thread (Unity's SynchronizationContext), so starting coroutines here is safe.
    async Task ReceiveLoop()
    {
        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();
        while (!destroyed && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), default);
            if (result.MessageType == WebSocketMessageType.Close) return;
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;
            var msg = JsonUtility.FromJson<Message>(text.ToString());
            text.Clear();
            if (msg.type == "offer" && !destroyed) StartCoroutine(Answer(msg.sdp));
        }
    }

    void Send(string type, string sdp)
    {
        var json = JsonUtility.ToJson(new Message { type = type, sdp = sdp });
        _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, default);
    }

    IEnumerator Answer(string sdp)
    {
        // One fresh connection per offer, so a reloaded page just sends a new offer.
        pc?.Dispose();
        var conn = pc = new RTCPeerConnection();
        conn.OnTrack = e =>
        {
            if (e.Track is VideoStreamTrack video) video.OnVideoReceived += Show;
        };
        conn.OnConnectionStateChange = state => Debug.Log("[ScreenReceiver] Video " + state);

        var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdp };
        var setRemote = conn.SetRemoteDescription(ref offer);
        yield return setRemote;
        if (setRemote.IsError) { Debug.LogError("[ScreenReceiver] Offer rejected: " + setRemote.Error.message); yield break; }

        var create = conn.CreateAnswer();
        yield return create;
        var answer = create.Desc;
        yield return conn.SetLocalDescription(ref answer);

        // Wait for all network candidates so they ship inside the answer.
        while (conn.GatheringState != RTCIceGatheringState.Complete) yield return null;
        if (conn == pc) Send("answer", conn.LocalDescription.sdp);
    }

    // Called on the first frame and again whenever the stream changes size.
    void Show(Texture texture)
    {
        CancelInvoke(nameof(FallBackToJpeg));
        surface.Show(texture);
        Debug.Log($"[ScreenReceiver] Receiving {texture.width}x{texture.height}");
    }

    // For the Quest: if WebRTC video fails (for example on Vulkan), the JPEG path still shows the screen.
    void FallBackToJpeg()
    {
        if (!TryGetComponent(out JpegScreenReceiver jpeg)) return;
        Debug.LogWarning($"[ScreenReceiver] No WebRTC video after {jpegFallbackSeconds} s, switching to JPEG frames");
        jpeg.enabled = true;
        Destroy(this);
    }

    void OnDestroy()
    {
        destroyed = true;
        ws?.Abort();
        pc?.Dispose();
    }
}
