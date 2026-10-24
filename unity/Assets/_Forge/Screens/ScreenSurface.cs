using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;

/// <summary>
/// Where a screen stream is drawn, for <see cref="ScreenReceiver"/> and <see cref="JpegScreenReceiver"/>:
/// a RawImage inside a <see cref="ForgePanel"/> (the Workspace's screen-main slot), or this object's
/// renderer (the quad in the sandboxes). Keeps the height and sets the width from the stream's aspect ratio.
/// Draws a mipmapped copy of the stream: a 1920 px frame lands on roughly 900 headset pixels, and without mipmaps
/// small text sparkles and crawls as the head moves. Trilinear + anisotropic keeps it steady at an angle too.
/// </summary>
public class ScreenSurface : MonoBehaviour
{
    [Tooltip("RawImage under the ForgePanel's Content. Empty = draw on this object's renderer.")]
    public RawImage image;

    [Tooltip("Keep the panel's height and set its width from the stream's aspect ratio.")]
    public bool fitAspect = true;

    [Tooltip("Copies per second of the stream into the mipmapped texture (the stream itself is 15 to 30 fps).")]
    public float copyRate = 30f;

    Texture source;
    RenderTexture mipped;
    float nextCopy;

    void Awake()
    {
        // Resizing the panel by hand keeps the stream's shape too.
        if (image != null && fitAspect && TryGetComponent(out ForgePanel panel)) panel.KeepAspect = true;
    }

    // Called on the first frame and whenever the stream changes size.
    public void Show(Texture texture)
    {
        source = texture;
        if (mipped != null) Destroy(mipped);
        // RGBA8 either way (a JPEG loads as RGB24, which can't be rendered into), sRGB-ness as the source's so colors match.
        var format = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
        mipped = new RenderTexture(texture.width, texture.height, 0, format)
        {
            useMipMap = true,
            autoGenerateMips = true,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
        };
        nextCopy = 0f;
        LateUpdate();

        float aspect = (float)texture.width / texture.height;
        if (image != null)
        {
            image.texture = mipped;
            if (fitAspect && TryGetComponent(out ForgePanel panel))
                panel.SizeMeters = new Vector2(panel.SizeMeters.y * aspect, panel.SizeMeters.y);
        }
        else
        {
            GetComponent<Renderer>().material.mainTexture = mipped;
            if (fitAspect)
            {
                var s = transform.localScale;
                transform.localScale = new Vector3(s.y * aspect, s.y, s.z);
            }
        }
    }

    // Both receivers update `source` in place, so copy it on a clock rather than per frame received.
    void LateUpdate()
    {
        if (source == null || Time.unscaledTime < nextCopy) return;
        nextCopy = Mathf.Max(nextCopy + 1f / copyRate, Time.unscaledTime); // fixed cadence: now + 1/rate skipped every other 30 fps frame at 72 Hz
        Graphics.Blit(source, mipped);
    }

    void OnDestroy()
    {
        if (mipped != null) Destroy(mipped);
    }
}
