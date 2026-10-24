using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Where a screen stream is drawn, for <see cref="ScreenReceiver"/> and <see cref="JpegScreenReceiver"/>:
/// a RawImage inside a <see cref="ForgePanel"/> (the Workspace's screen-main slot), or this object's
/// renderer (the quad in the sandboxes). Keeps the height and sets the width from the stream's aspect ratio.
/// </summary>
public class ScreenSurface : MonoBehaviour
{
    [Tooltip("RawImage under the ForgePanel's Content. Empty = draw on this object's renderer.")]
    public RawImage image;

    [Tooltip("Keep the panel's height and set its width from the stream's aspect ratio.")]
    public bool fitAspect = true;

    void Awake()
    {
        // Resizing the panel by hand keeps the stream's shape too.
        if (image != null && fitAspect && TryGetComponent(out ForgePanel panel)) panel.KeepAspect = true;
    }

    // Called on the first frame and whenever the stream changes size.
    public void Show(Texture texture)
    {
        float aspect = (float)texture.width / texture.height;
        if (image != null)
        {
            image.texture = texture;
            if (fitAspect && TryGetComponent(out ForgePanel panel))
                panel.SizeMeters = new Vector2(panel.SizeMeters.y * aspect, panel.SizeMeters.y);
        }
        else
        {
            GetComponent<Renderer>().material.mainTexture = texture;
            if (fitAspect)
            {
                var s = transform.localScale;
                transform.localScale = new Vector3(s.y * aspect, s.y, s.z);
            }
        }
    }
}
