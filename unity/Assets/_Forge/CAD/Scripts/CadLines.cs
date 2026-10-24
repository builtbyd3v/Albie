using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Lines and beads of light (Forge/CadLight) batched into one mesh, so one draw call: segments, arcs and dots given in
/// world space each frame, built as quads facing the eye. It lives under the viewer's overlay root, so it folds and
/// scales with the volume; widths are metres in the room. No allocations once its buffers have grown.
/// Per frame: Begin(eye), add, End(). Hide() when there's nothing to draw.
/// </summary>
public sealed class CadLines
{
    const int MaxVertices = 65532;  // 16-bit indices

    static int linearSpace = -1;

    readonly Transform transform;
    readonly Mesh mesh;
    readonly MeshRenderer renderer;
    Vector3[] positions = new Vector3[256];
    Vector2[] uvs = new Vector2[256];
    Color32[] colors = new Color32[256];
    int[] indices = new int[384];
    int vertexCount, indexCount;
    Matrix4x4 toLocal = Matrix4x4.identity;
    float toLocalScale = 1f;
    Vector3 eye;       // in local space
    Vector3 eyeWorld;
    float dashOn = 0.003f, dashOff = 0.005f, dashPhase;

    public CadLines(string name, Transform parent, Material material)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = parent.gameObject.layer;
        transform = go.transform;
        transform.SetParent(parent, false);
        mesh = new Mesh { name = name };
        mesh.MarkDynamic();
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
    }

    /// <summary>Start a frame: what's added until End faces <paramref name="eyeWorld"/>.</summary>
    public void Begin(Vector3 eyeWorld)
    {
        vertexCount = indexCount = 0;
        this.eyeWorld = eyeWorld;
        if (transform == null) return;
        toLocal = transform.worldToLocalMatrix;
        float s = transform.lossyScale.x;
        toLocalScale = s > 1e-6f ? 1f / s : 1f;
        eye = toLocal.MultiplyPoint3x4(eyeWorld);
    }

    /// <summary>Start a dashed run: <paramref name="on"/> m of line, <paramref name="off"/> m of gap, carried across <see cref="Dash"/> calls.</summary>
    public void DashStart(float on, float off, float phase = 0f)
    {
        dashOn = Mathf.Max(on, 0.0005f);
        dashOff = Mathf.Max(off, 0.0005f);
        dashPhase = phase;
    }

    /// <summary>The next piece of a dashed polyline (see <see cref="DashStart"/>).</summary>
    public void Dash(Vector3 a, Vector3 b, float width, Color color)
    {
        float length = Vector3.Distance(a, b);
        if (length < 1e-6f) return;
        Vector3 along = (b - a) / length;
        float period = dashOn + dashOff, s = 0f;
        for (int guard = 0; s < length && guard < 4096; guard++)
        {
            float p = dashPhase % period;
            float e = Mathf.Min(length, s + (p < dashOn ? dashOn - p : period - p));
            if (p < dashOn) Segment(a + along * s, a + along * e, width, color);
            dashPhase += e - s;
            s = e;
        }
    }

    /// <summary>A dashed line on its own.</summary>
    public void Dashed(Vector3 a, Vector3 b, float width, Color color, float on, float off)
    {
        DashStart(on, off);
        Dash(a, b, width, color);
    }

    /// <summary>A ring facing the eye (the end of a leader, a marker).</summary>
    public void Ring(Vector3 centre, float radius, float width, Color color, int segments = 18) =>
        Arc(centre, centre - eyeWorld, Vector3.up, radius, 0f, 360f, width, color, color, segments);

    /// <summary>A flat disc of light round <paramref name="axis"/>: faint inside, brightening to its rim (prop discs, pools).</summary>
    public void Disc(Vector3 centreWorld, Vector3 axisWorld, float radius, Color color)
    {
        if (color.a <= 0.003f || radius <= 0f || axisWorld.sqrMagnitude < 1e-10f) return;
        Vector3 axis = toLocal.MultiplyVector(axisWorld).normalized;
        Vector3 c = toLocal.MultiplyPoint3x4(centreWorld);
        Vector3 u = Vector3.ProjectOnPlane(Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right, axis).normalized;
        Vector3 w = Vector3.Cross(axis, u);
        float r = radius * toLocalScale;
        int v = Reserve();
        if (v < 0) return;
        Color32 col = Linear(color);
        positions[v] = c - u * r - w * r;
        positions[v + 1] = c + u * r - w * r;
        positions[v + 2] = c - u * r + w * r;
        positions[v + 3] = c + u * r + w * r;
        uvs[v] = new Vector2(2f, 2f);  // 2..3: the shader's disc
        uvs[v + 1] = new Vector2(3f, 2f);
        uvs[v + 2] = new Vector2(2f, 3f);
        uvs[v + 3] = new Vector2(3f, 3f);
        colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = col;
    }

    public void Segment(Vector3 a, Vector3 b, float width, Color color) => Segment(a, b, width, color, color);

    /// <summary>A line from <paramref name="aWorld"/> to <paramref name="bWorld"/>, its colour fading from one end to the other.</summary>
    public void Segment(Vector3 aWorld, Vector3 bWorld, float width, Color colorA, Color colorB)
    {
        if (colorA.a <= 0.003f && colorB.a <= 0.003f) return;
        Vector3 a = toLocal.MultiplyPoint3x4(aWorld), b = toLocal.MultiplyPoint3x4(bWorld);
        Vector3 side = Vector3.Cross(b - a, eye - (a + b) * 0.5f);
        float m = side.magnitude;
        if (m < 1e-10f) return;  // no length, or pointing straight at the eye
        side *= width * 0.5f * toLocalScale / m;
        int v = Reserve();
        if (v < 0) return;
        Color32 ca = Linear(colorA), cb = Linear(colorB);
        positions[v] = a - side;
        positions[v + 1] = a + side;
        positions[v + 2] = b - side;
        positions[v + 3] = b + side;
        uvs[v] = uvs[v + 2] = new Vector2(0.5f, 0f);  // across the line only: the shader's profile is radial
        uvs[v + 1] = uvs[v + 3] = new Vector2(0.5f, 1f);
        colors[v] = colors[v + 1] = ca;
        colors[v + 2] = colors[v + 3] = cb;
    }

    /// <summary>A bead of light <paramref name="diameter"/> across (glow included), facing the eye.</summary>
    public void Dot(Vector3 pWorld, float diameter, Color color)
    {
        if (color.a <= 0.003f || diameter <= 0f) return;
        Vector3 p = toLocal.MultiplyPoint3x4(pWorld);
        Vector3 forward = p - eye;
        if (forward.sqrMagnitude < 1e-10f) return;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        if (right.sqrMagnitude < 1e-10f) right = Vector3.Cross(Vector3.forward, forward);
        float half = diameter * 0.5f * toLocalScale;
        right = right.normalized * half;
        Vector3 up = Vector3.Cross(forward, right).normalized * half;
        int v = Reserve();
        if (v < 0) return;
        Color32 c = Linear(color);
        positions[v] = p - right - up;
        positions[v + 1] = p + right - up;
        positions[v + 2] = p - right + up;
        positions[v + 3] = p + right + up;
        uvs[v] = new Vector2(0f, 0f);
        uvs[v + 1] = new Vector2(1f, 0f);
        uvs[v + 2] = new Vector2(0f, 1f);
        uvs[v + 3] = new Vector2(1f, 1f);
        colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = c;
    }

    /// <summary>
    /// An arc round <paramref name="axis"/> through <paramref name="centre"/>, from <paramref name="fromDeg"/> to
    /// <paramref name="toDeg"/>, its colour fading along it. Angles go the way Quaternion.AngleAxis turns, starting at
    /// <paramref name="reference"/> (flattened onto the circle's plane), so an arc can follow a part spun with AngleAxis.
    /// </summary>
    public void Arc(Vector3 centre, Vector3 axis, Vector3 reference, float radius, float fromDeg, float toDeg, float width,
        Color fromColor, Color toColor, int segments)
    {
        if (axis.sqrMagnitude < 1e-10f || radius <= 0f || segments < 1) return;
        axis.Normalize();
        Vector3 u = Vector3.ProjectOnPlane(reference, axis);
        if (u.sqrMagnitude < 1e-10f) u = Vector3.ProjectOnPlane(Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right, axis);
        u.Normalize();
        Vector3 w = Vector3.Cross(axis, u);  // AngleAxis(90, axis) * u
        float a0 = fromDeg * Mathf.Deg2Rad, step = (toDeg - fromDeg) * Mathf.Deg2Rad / segments;
        Vector3 previous = centre + (u * Mathf.Cos(a0) + w * Mathf.Sin(a0)) * radius;
        for (int k = 1; k <= segments; k++)
        {
            float a = a0 + step * k;
            Vector3 next = centre + (u * Mathf.Cos(a) + w * Mathf.Sin(a)) * radius;
            Segment(previous, next, width, Color.LerpUnclamped(fromColor, toColor, (k - 1f) / segments),
                Color.LerpUnclamped(fromColor, toColor, (float)k / segments));
            previous = next;
        }
    }

    public void Circle(Vector3 centre, Vector3 axis, float radius, float width, Color color, int segments = 28) =>
        Arc(centre, axis, Vector3.up, radius, 0f, 360f, width, color, color, segments);

    /// <summary>Upload what was added since Begin (or hide the mesh if nothing was).</summary>
    public void End()
    {
        if (renderer == null || mesh == null) return;  // torn down (leaving Play)
        bool any = indexCount > 0;
        if (any)
        {
            mesh.Clear(true);
            mesh.SetVertices(positions, 0, vertexCount);
            mesh.SetUVs(0, uvs, 0, vertexCount);
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

    // Four vertices and their two triangles; -1 when the mesh is full.
    int Reserve()
    {
        if (vertexCount + 4 > MaxVertices) return -1;
        if (vertexCount + 4 > positions.Length)
        {
            int n = positions.Length * 2;
            System.Array.Resize(ref positions, n);
            System.Array.Resize(ref uvs, n);
            System.Array.Resize(ref colors, n);
        }
        if (indexCount + 6 > indices.Length) System.Array.Resize(ref indices, indices.Length * 2);
        int v = vertexCount;
        vertexCount += 4;
        indices[indexCount++] = v;
        indices[indexCount++] = v + 1;
        indices[indexCount++] = v + 2;
        indices[indexCount++] = v + 2;
        indices[indexCount++] = v + 1;
        indices[indexCount++] = v + 3;
        return v;
    }

    // Vertex colours aren't converted for us: the project renders in linear space.
    static Color32 Linear(Color c)
    {
        if (linearSpace < 0) linearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0;
        if (linearSpace == 0) return c;
        Color l = c.linear;
        l.a = c.a;
        return l;
    }
}
