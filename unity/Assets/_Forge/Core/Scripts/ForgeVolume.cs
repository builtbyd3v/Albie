using TMPro;
using UnityEngine;

/// <summary>
/// A 3D container: a box of <see cref="size"/> meters whose base sits at this transform's origin.
/// Put 3D content (a GLB rocket, model layer blocks, a photo stack) under it with <see cref="SetContent"/>;
/// it is scaled to fit the box and stood on the base. The base is a ring of light on the desk (Forge/NeonRing over a
/// LightPool with a contrast wash) tinted with the mode's hue (<see cref="SetAccent"/>); it brightens and runs a comet
/// while pointed at (<see cref="SetHighlight"/>).
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
    [SerializeField] MeshRenderer basePool;
    [SerializeField] TMP_Text label;

    const float BasePad = 0.03f;  // room around the ring for its glow (m)

    static readonly int SizeId = Shader.PropertyToID("_Size"), RadiusId = Shader.PropertyToID("_Radius");
    static readonly int HueId = Shader.PropertyToID("_Hue"), HueTId = Shader.PropertyToID("_HueT");
    static readonly int NearId = Shader.PropertyToID("_Near"), HoverId = Shader.PropertyToID("_Hover");
    static readonly int OpacityId = Shader.PropertyToID("_Opacity"), WashId = Shader.PropertyToID("_Wash");

    MaterialPropertyBlock block;
    MeshRenderer ringRenderer;
    Color accent = ForgeTheme.Tool;
    bool highlighted;
    float highlight;
    TMP_Text hint;
    float hintShown, hintTarget;
    string hintText, caption;

    public Transform ContentRoot => contentRoot;

    /// <summary>The content's turn about its own up axis and its tilt toward the viewer (deg), from <see cref="SetView"/>.</summary>
    public float ViewYaw { get; private set; }
    public float ViewTilt { get; private set; }

    public string Title
    {
        get => label != null ? label.text : "";
        set { if (label != null) label.text = value; }
    }

    void OnValidate() => ApplySize();

    void Awake() => ApplySize();

    void Update()
    {
        if (highlight != (highlighted ? 1f : 0f))
        {
            highlight = Mathf.MoveTowards(highlight, highlighted ? 1f : 0f, Time.deltaTime / ForgeTheme.HoverFade);
            ShowBase();
        }
        if (hint != null && hintShown != hintTarget)
        {
            hintShown = Mathf.MoveTowards(hintShown, hintTarget, Time.deltaTime / ForgeTheme.HoverFade);
            hint.alpha = 0.72f * hintShown;
        }
        if (!turntable) return;
        ViewYaw = (ViewYaw + turntableDegreesPerSecond * Time.deltaTime) % 360f;
        ApplyView();
    }

    /// <summary>The mode color the base light is tinted with (faintly, like the dock rings).</summary>
    public void SetAccent(Color color)
    {
        accent = color;
        ShowBase();
    }

    /// <summary>Pointed at: the base ring brightens and a comet of light runs around it.</summary>
    public void SetHighlight(bool on) => highlighted = on;

    /// <summary>
    /// A small line of help under the label (how to move and turn it), faded in while set; it stands in for the
    /// <see cref="SetCaption"/> line meanwhile. Null or empty takes it away. The label itself (<see cref="Title"/>)
    /// is left alone, so a module can retitle the volume at any time.
    /// </summary>
    public void SetHint(string text)
    {
        hintText = text;
        ShowLine();
    }

    /// <summary>A quiet line that stays under the label (e.g. what a placeholder volume is waiting for).</summary>
    public void SetCaption(string text)
    {
        caption = text;
        ShowLine();
    }

    void ShowLine()
    {
        string text = !string.IsNullOrEmpty(hintText) ? hintText : caption;
        bool show = !string.IsNullOrEmpty(text);
        if (show && hint == null) hint = MakeHint();
        if (hint == null) return;
        if (show && hint.text != text) hint.text = text;
        hintTarget = show ? 1f : 0f;
    }

    // Under the label's canvas, which lies flat in front of the base (its "down" points at the viewer).
    TMP_Text MakeHint()
    {
        if (label == null) return null;
        var rt = (RectTransform)new GameObject("Hint", typeof(RectTransform)).transform;
        rt.SetParent(label.transform.parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -1f);
        rt.sizeDelta = new Vector2(320f, 30f);
        var text = rt.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = ForgeTheme.Font != null ? ForgeTheme.Font : label.font;
        if (ForgeTheme.Font != null) text.fontSharedMaterial = ForgeTheme.Font.material;
        text.fontSize = 9.5f;
        text.lineSpacing = 6f;
        text.alignment = TextAlignmentOptions.Top;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.color = Color.white;
        text.alpha = 0f;
        text.raycastTarget = false;
        return text;
    }

    public void SetSize(Vector3 newSize)
    {
        size = newSize;
        ApplySize();
        Fit();
    }

    /// <summary>
    /// Turn the content about the box's center: <paramref name="yaw"/> spins it about its own up axis, then
    /// <paramref name="tilt"/> tips it toward (+) or away from the front. The base and label stay put. Stops the turntable.
    /// </summary>
    public void SetView(float yaw, float tilt)
    {
        turntable = false;
        ViewYaw = yaw;
        ViewTilt = tilt;
        ApplyView();
    }

    void ApplyView()
    {
        var center = new Vector3(0f, size.y * 0.5f, 0f);
        Quaternion r = Quaternion.Euler(ViewTilt, 0f, 0f) * Quaternion.Euler(0f, ViewYaw, 0f);
        spin.localRotation = r;
        spin.localPosition = center - r * center;
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
        // Base: quads lying flat on the desk (a quad faces -Z; turned 90 deg about X it faces up).
        float diameter = Mathf.Max(size.x, size.z);
        if (baseRing != null)
        {
            baseRing.localRotation = Quaternion.Euler(90f, 0f, 0f);
            baseRing.localScale = new Vector3(diameter + 2f * BasePad, diameter + 2f * BasePad, 1f);
        }
        if (basePool != null)
        {
            basePool.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            basePool.transform.localScale = new Vector3(diameter + 0.06f, diameter + 0.06f, 1f);
        }
        // The label is a small world-space canvas lying flat in front of the base.
        if (label != null) label.transform.parent.localPosition = new Vector3(0f, 0.01f, -size.z * 0.5f - 0.03f);
        ShowBase();
    }

    // Sizes go to the shaders in mm, like the dock's.
    void ShowBase()
    {
        if (baseRing == null) return;
        if (ringRenderer == null) ringRenderer = baseRing.GetComponent<MeshRenderer>();
        if (block == null) block = new MaterialPropertyBlock();
        float radius = Mathf.Max(size.x, size.z) * 500f;
        if (ringRenderer != null)
        {
            block.Clear();
            block.SetFloat(SizeId, baseRing.localScale.x * 1000f);
            block.SetFloat(RadiusId, radius);
            block.SetColor(HueId, accent);
            block.SetFloat(HueTId, ForgeTheme.HueStrength);
            block.SetFloat(NearId, highlight);
            block.SetFloat(HoverId, highlight);
            ringRenderer.SetPropertyBlock(block);
        }
        if (basePool != null)
        {
            block.Clear();
            block.SetFloat(SizeId, basePool.transform.localScale.x * 1000f);
            block.SetFloat(RadiusId, radius);
            block.SetColor(HueId, accent);
            block.SetFloat(HueTId, ForgeTheme.HueStrength);
            block.SetFloat(OpacityId, 0.35f + 0.35f * highlight);
            block.SetFloat(WashId, 1f);
            basePool.SetPropertyBlock(block);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireCube(new Vector3(0f, size.y * 0.5f, 0f), size);
    }
}
