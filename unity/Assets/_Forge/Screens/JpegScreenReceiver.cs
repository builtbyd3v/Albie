using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Fallback for <see cref="ScreenReceiver"/>: shows JPEG frames from screen-bridge/stream.py
/// (one JPEG per binary WebSocket message, about 10 fps). Plain C#, no native plugin, and it runs
/// entirely over a USB cable with adb reverse. Starts disabled on the ScreenPanel prefab;
/// ScreenReceiver enables it when WebRTC shows no video in time.
/// </summary>
public class JpegScreenReceiver : MonoBehaviour
{
    [Tooltip("screen-bridge/stream.py. localhost means: the laptop in ForgeNet's hubUrl.")]
    public string streamUrl = "ws://localhost:8765";

    ClientWebSocket ws;
    Texture2D texture;
    ScreenSurface surface;
    bool destroyed;

    async void Start()
    {
        surface = GetComponent<ScreenSurface>();
        texture = new Texture2D(2, 2);

        // Reconnect forever, so the panel recovers when stream.py restarts.
        while (!destroyed)
        {
            using (ws = new ClientWebSocket())
            {
                try
                {
                    var url = ScreenReceiver.OnHubHost(streamUrl);
                    await ws.ConnectAsync(new Uri(url), default);
                    Debug.Log("[JpegScreenReceiver] Connected to " + url);
                    await ReceiveLoop();
                }
                catch (Exception e)
                {
                    if (!destroyed) Debug.LogWarning("[JpegScreenReceiver] " + e.Message);
                }
            }
            await Task.Delay(2000);
        }
    }

    // Awaits resume on the main thread (Unity's SynchronizationContext), so touching the texture here is safe.
    async Task ReceiveLoop()
    {
        var buffer = new byte[1 << 20]; // a 1920-wide JPEG is 100 to 300 KB
        int length = 0;
        while (!destroyed && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), default);
            if (result.MessageType == WebSocketMessageType.Close) return;
            length += result.Count;
            if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            if (!result.EndOfMessage) continue;

            // ponytail: one copy per frame (about 10 a second); LoadImage wants an exact-size array
            var jpeg = new byte[length];
            Buffer.BlockCopy(buffer, 0, jpeg, 0, length);
            length = 0;
            if (!destroyed) Show(jpeg);
        }
    }

    void Show(byte[] jpeg)
    {
        int width = texture.width, height = texture.height;
        if (!texture.LoadImage(jpeg)) return; // updates the shown texture in place
        if (texture.width == width && texture.height == height) return;

        // First frame or new size
        surface.Show(texture);
        Debug.Log($"[JpegScreenReceiver] Receiving {texture.width}x{texture.height}");
    }

    void OnDestroy()
    {
        destroyed = true;
        ws?.Abort();
        if (texture != null) Destroy(texture);
    }
}
