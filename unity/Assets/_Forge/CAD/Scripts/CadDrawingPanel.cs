using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The drawing panel (CadDrawingPanel.prefab, a ForgePanel variant; slot cad-drawing): the open version's engineering
/// drawing (front, top, right and iso views, dimensions, parts list, title block; changed parts in orange). From the hub
/// (GET /cad/drawing.png?v=N, made once per version) or the mocks. Asks as soon as a version is announced (not after
/// the model loads) and keeps the last sheet up while the next one is drawn. One drawing at a time: versions that come
/// in meanwhile (a slider drag) skip to the newest, so drawing jobs don't queue up in front of the hub's regens.
/// Tap the sheet to zoom in on that spot, tap again for the whole sheet.
/// </summary>
public class CadDrawingPanel : MonoBehaviour, IPointerClickHandler
{
    public CadMockSet mocks;
    [Tooltip("Tap to zoom: the zoomed view shows this fraction of the sheet's width and height.")]
    [Range(0.2f, 0.9f)] public float zoomView = 0.5f;

    [SerializeField] RawImage sheet;
    [SerializeField] TMP_Text status;

    CadSession session;
    Texture2D texture;
    int shown = -1, wanted = -1;
    bool loading, zoomed;

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        session.VersionComing += OnVersionComing;
        if (texture == null) sheet.enabled = false;
        if (session.Latest != null) Want(session.Latest.info.version);  // spawned after the first version arrived
        else status.text = "Waiting for the design...";
    }

    void OnDisable()
    {
        if (session != null) session.VersionComing -= OnVersionComing;
    }

    void OnDestroy()
    {
        if (texture != null) Destroy(texture);
    }

    void OnVersionComing(CadModelUpdated info) => Want(info.version);

    void Want(int version)
    {
        if (version == shown || version == wanted) return;
        wanted = version;
        status.text = $"Drawing v{version}...";
        if (!loading) Request(version);
    }

    void Request(int version)
    {
        loading = true;
        session.GetDrawing(version, png => Loaded(version, png));
    }

    void Loaded(int version, byte[] png)
    {
        if (this == null) return;
        loading = false;
        if (version == wanted) wanted = -1;
        Show(version, png);
        if (wanted < 0) return;
        status.text = $"Drawing v{wanted}...";  // a newer version came in while this one was drawn
        Request(wanted);
    }

    void Show(int version, byte[] png)
    {
        if (png == null)
        {
            status.text = shown >= 0 ? $"No drawing for v{version} (showing v{shown})" : $"No drawing for v{version}";
            return;
        }
        // Mipmaps and anisotropic filtering keep thin lines readable when the panel is small in view or seen at an angle.
        var next = new Texture2D(2, 2, TextureFormat.RGBA32, true)
        {
            name = $"Drawing v{version}",
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
            wrapMode = TextureWrapMode.Clamp,
        };
        if (!next.LoadImage(png, markNonReadable: true))
        {
            Destroy(next);
            status.text = $"v{version}: the drawing isn't a readable PNG";
            return;
        }
        if (texture != null) Destroy(texture);
        texture = next;
        sheet.texture = texture;
        sheet.enabled = true;
        var fitter = sheet.GetComponent<AspectRatioFitter>();
        if (fitter != null) fitter.aspectRatio = (float)texture.width / texture.height;
        shown = version;
        ShowStatus();
    }

    // Tap the sheet: zoom in on that spot, or back out to the whole sheet.
    public void OnPointerClick(PointerEventData e)
    {
        if (texture == null || e.pointerPressRaycast.gameObject != sheet.gameObject) return;
        zoomed = !zoomed;
        if (!zoomed) sheet.uvRect = new Rect(0f, 0f, 1f, 1f);
        else
        {
            var rt = sheet.rectTransform;
            Vector2 local = rt.InverseTransformPoint(e.pointerPressRaycast.worldPosition);
            Rect r = sheet.uvRect;  // whole sheet here, so rect position = uv
            float u = r.x + (local.x - rt.rect.xMin) / rt.rect.width * r.width;
            float v = r.y + (local.y - rt.rect.yMin) / rt.rect.height * r.height;
            float half = zoomView * 0.5f;
            sheet.uvRect = new Rect(Mathf.Clamp(u - half, 0f, 1f - zoomView), Mathf.Clamp(v - half, 0f, 1f - zoomView), zoomView, zoomView);
        }
        ShowStatus();
    }

    void ShowStatus() => status.text = $"v{shown}  <alpha=#88>" + (zoomed ? "tap: whole sheet" : "tap to zoom in");
}
