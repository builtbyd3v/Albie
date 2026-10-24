using TMPro;
using UnityEngine;

/// <summary>
/// A 3D container: a box of <see cref="size"/> meters whose base sits at this transform's origin.
/// Put 3D content (a GLB rocket, model layer blocks, a photo stack) under it with <see cref="SetContent"/>;
/// it is scaled to fit the box and stood on the base. The base ring shows where the volume is.
/// </summary>
public class ForgeVolume : MonoBehaviour
{
    [Tooltip("Matches the panel IDs in PROJECT_CONTEXT section 7 (e.g. cad-viewer, ml-model3d).")]
    public string volumeId;
    public Vector3 size = new Vector3(0.3f, 0.3f, 0.3f);

    [Tooltip("Slowly spin the content, like a turntable.")]
    public bool turntable;
    public float turntableDegreesPerSecond = 20f;

    [SerializeField] Transform spin;         // turntable pivot at the base center
    [SerializeField] Transform contentRoot;  // child of spin; scaled and offset by Fit
    [SerializeField] Transform baseRing;
    [SerializeField] TMP_Text label;

    public Transform ContentRoot => contentRoot;

    public string Title
    {
        get => label != null ? label.text : "";
        set { if (label != null) label.text = value; }
    }

    void OnValidate() => ApplySize();

    void Update()
    {
        if (turntable) spin.Rotate(0f, turntableDegreesPerSecond * Time.deltaTime, 0f, Space.Self);
    }

    public void SetSize(Vector3 newSize)
    {
        size = newSize;
        ApplySize();
        Fit();
    }

    /// <summary>Replace the content with <paramref name="content"/> and fit it to the box.</summary>
    public void SetContent(GameObject content, bool fit = true)
    {
        for (int i = contentRoot.childCount - 1; i >= 0; i--) Destroy(contentRoot.GetChild(i).gameObject);
        content.transform.SetParent(contentRoot, false);
        if (fit) Fit();
    }

    /// <summary>Uniformly scale and move the content so it fits inside the box, standing on the base.</summary>
    public void Fit()
    {
        if (contentRoot == null || contentRoot.childCount == 0) return;
        contentRoot.localScale = Vector3.one;
        contentRoot.localPosition = Vector3.zero;
        var renderers = contentRoot.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        // Bounds in the spin pivot's space (same as the volume's, up to the turntable yaw).
        Bounds b = default;
        bool first = true;
        foreach (var r in renderers)
        {
            var wb = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var local = spin.InverseTransformPoint(corner);
                if (first) { b = new Bounds(local, Vector3.zero); first = false; }
                else b.Encapsulate(local);
            }
        }

        float scale = Mathf.Min(size.x / Mathf.Max(b.size.x, 1e-4f), size.y / Mathf.Max(b.size.y, 1e-4f), size.z / Mathf.Max(b.size.z, 1e-4f));
        contentRoot.localScale = Vector3.one * scale;
        contentRoot.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z) * scale;
    }

    void ApplySize()
    {
        if (baseRing != null) baseRing.localScale = new Vector3(size.x, 0.004f, size.z);
        // The label is a small world-space canvas lying flat in front of the base.
        if (label != null) label.transform.parent.localPosition = new Vector3(0f, 0.01f, -size.z * 0.5f - 0.03f);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireCube(new Vector3(0f, size.y * 0.5f, 0f), size);
    }
}
