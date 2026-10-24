using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The graphite chips behind the CAD viewer's labels (Forge/CadChip), batched into one mesh, so one draw call however
/// many labels show. Each chip is a rounded rectangle with a soft shadow, given each frame by its label
/// (<see cref="CadLabel.Draw"/>) in world space. Lives under the viewer's overlay root. No allocations once its buffers
/// have grown. Per frame: Begin(), labels Draw into it, End().
/// </summary>
public sealed class CadChips
{
    const float ShadowMm = 7f;  // room round each chip for its shadow (matches the shader's _Shadow)
    const int MaxVertices = 65532;

    static int linearSpace = -1;

    readonly Transform transform;
    readonly Mesh mesh;
    readonly MeshRenderer renderer;
    Vector3[] positions = new Vector3[64];
    Vector2[] uvs = new Vector2[64];
    Vector4[] shapes = new Vector4[64];
    Color32[] colors = new Color32[64];
    int[] indices = new int[96];
    int vertexCount, indexCount;
    Matrix4x4 toLocal = Matrix4x4.identity;

    public CadChips(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = parent.gameObject.layer;
        transform = go.transform;
        transform.SetParent(parent, false);
        mesh = new Mesh { name = name };
        mesh.MarkDynamic();
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = CadLook.ChipMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
    }

    public void Begin()
    {
        vertexCount = indexCount = 0;
        if (transform != null) toLocal = transform.worldToLocalMatrix;
    }

    /// <summary>
    /// One chip centred on <paramref name="centre"/>, in the plane of <paramref name="right"/> and <paramref name="up"/>
    /// (unit, world), <paramref name="halfSize"/> and <paramref name="radius"/> in mm of <paramref name="metresPerMm"/>.
    /// <paramref name="color"/>: the chip's colour with its opacity in alpha; <paramref name="shadow"/>: the shadow's alpha.
    /// </summary>
    public void Chip(Vector3 centre, Vector3 right, Vector3 up, Vector2 halfSize, float radius, float metresPerMm, Color color, float shadow)
    {
        if (color.a <= 0.003f && shadow <= 0.003f) return;
        if (vertexCount + 4 > MaxVertices) return;
        Grow();
        float hx = halfSize.x + ShadowMm, hy = halfSize.y + ShadowMm;
        Vector3 rx = right * (hx * metresPerMm), uy = up * (hy * metresPerMm);
        int v = vertexCount;
        positions[v] = toLocal.MultiplyPoint3x4(centre - rx - uy);
        positions[v + 1] = toLocal.MultiplyPoint3x4(centre + rx - uy);
        positions[v + 2] = toLocal.MultiplyPoint3x4(centre - rx + uy);
        positions[v + 3] = toLocal.MultiplyPoint3x4(centre + rx + uy);
        uvs[v] = new Vector2(-hx, -hy);
        uvs[v + 1] = new Vector2(hx, -hy);
        uvs[v + 2] = new Vector2(-hx, hy);
        uvs[v + 3] = new Vector2(hx, hy);
        var shape = new Vector4(halfSize.x, halfSize.y, Mathf.Min(radius, Mathf.Min(halfSize.x, halfSize.y)), Mathf.Clamp01(shadow));
        shapes[v] = shapes[v + 1] = shapes[v + 2] = shapes[v + 3] = shape;
        Color32 c = Linear(color);
        colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = c;
        vertexCount += 4;
        indices[indexCount++] = v;
        indices[indexCount++] = v + 2;
        indices[indexCount++] = v + 1;
        indices[indexCount++] = v + 2;
        indices[indexCount++] = v + 3;
        indices[indexCount++] = v + 1;
    }

    public void End()
    {
        if (renderer == null || mesh == null) return;  // torn down (leaving Play)
        bool any = indexCount > 0;
        if (any)
        {
            mesh.Clear(true);
            mesh.SetVertices(positions, 0, vertexCount);
            mesh.SetUVs(0, uvs, 0, vertexCount);
            mesh.SetUVs(1, shapes, 0, vertexCount);
            mesh.SetColors(colors, 0, vertexCount);
            mesh.SetIndices(indices, 0, indexCount, MeshTopology.Triangles, 0, true);
        }
        if (renderer.enabled != any) renderer.enabled = any;
    }

    public void Hide()
    {
        vertexCount = indexCount = 0;
        if (renderer != null && renderer.enabled) renderer.enabled = false;
    }

    public void Destroy()
    {
        if (mesh != null) Object.Destroy(mesh);
        if (transform != null) Object.Destroy(transform.gameObject);
    }

    void Grow()
    {
        if (vertexCount + 4 > positions.Length)
        {
            int n = positions.Length * 2;
            System.Array.Resize(ref positions, n);
            System.Array.Resize(ref uvs, n);
            System.Array.Resize(ref shapes, n);
            System.Array.Resize(ref colors, n);
        }
        if (indexCount + 6 > indices.Length) System.Array.Resize(ref indices, indices.Length * 2);
    }

    static Color32 Linear(Color c)
    {
        if (linearSpace < 0) linearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0;
        if (linearSpace == 0) return c;
        Color l = c.linear;
        l.a = c.a;
        return l;
    }
}
