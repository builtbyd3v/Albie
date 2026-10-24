using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The part on the desk. CadViewer.prefab is a ForgeVolume variant with this component.
/// Shows the CadSession's current version. On a change: the changed parts glow (cad-vision's outline),
/// a ghost of what they were fades out, and the CG (blue) and CP (red) collars slide to their new
/// heights. cad.view explodes the assembly or focuses one part.
/// The first model is fitted to the volume and later versions keep that scale, so "20% longer" looks longer.
/// </summary>
[RequireComponent(typeof(ForgeVolume))]
public class CadViewer : MonoBehaviour
{
    public CadMockSet mocks;

    [Header("Materials")]
    public Material ghostMaterial;  // Forge/CadGhost
    public Material outlineMask;    // CADVision/SelectionMask
    public Material outline;        // CADVision/SelectionOutline
    public Material cgMaterial;
    public Material cpMaterial;

    [Header("Feel")]
    public float ghostSeconds = 1.6f;
    public float glowSeconds = 2.5f;
    public float glowWidth = 0.008f;
    public float markerSeconds = 0.6f;
    [Tooltip("The first model fills this fraction of the volume, leaving room for later versions to grow visibly.")]
    [Range(0.3f, 1f)] public float fitHeadroom = 0.7f;
    [Tooltip("Exploded view: each part moves this many times its vertical distance from the model's centre...")]
    public float explodeAxial = 0.6f;
    [Tooltip("...and this many times its sideways distance (fins fan out).")]
    public float explodeRadial = 1.5f;
    [Range(0f, 1f)] public float focusDim = 0.12f;
    public bool showMarkers = true;

    public CadModel Current => current;
    public bool Exploded => explodeTarget > 0.5f;

    struct ExplodePart
    {
        public Transform transform;
        public Vector3 home;
        public Vector3 offset;
    }

    struct Swapped
    {
        public Renderer renderer;
        public Material[] original;
    }

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int WidthId = Shader.PropertyToID("_OutlineWidth");

    ForgeVolume volume;
    CadSession session;
    Transform holder;
    CadModel current, ghost;
    float ghostT = 1f;
    Color ghostColor;
    readonly List<Renderer> ghostRenderers = new List<Renderer>();
    float glowT = 1f;
    readonly List<GameObject> glowLayers = new List<GameObject>();
    readonly List<Swapped> dimmed = new List<Swapped>();
    Material glowInstance;
    MaterialPropertyBlock block;
    Transform cgRing, cpRing;
    float cgFrom, cgTo, cpFrom, cpTo, markerT = 1f;
    bool markersVisible;
    readonly List<ExplodePart> explodeParts = new List<ExplodePart>();
    float explodeT, explodeTarget;
    string focusId;
    int loadToken;
    bool fitted;

    void Awake()
    {
        volume = GetComponent<ForgeVolume>();
        block = new MaterialPropertyBlock();
        if (outline != null) glowInstance = new Material(outline);
        ghostColor = ghostMaterial != null ? ghostMaterial.GetColor(ColorId) : new Color(0.6f, 0.85f, 1f, 0.35f);
        holder = new GameObject("CadModel").transform;
        volume.SetContent(holder.gameObject, fit: false);
        cgRing = MakeCollar("CG", cgMaterial);
        cpRing = MakeCollar("CP", cpMaterial);
    }

    void OnEnable()
    {
        session = CadSession.Get(mocks);
        session.ModelChanged += OnModelChanged;
        session.ViewChanged += OnViewChanged;
        if (session.Latest != null) OnModelChanged(session.Latest);  // spawned after the first version arrived
        OnViewChanged(session.View);
    }

    void OnDisable()
    {
        if (session == null) return;
        session.ModelChanged -= OnModelChanged;
        session.ViewChanged -= OnViewChanged;
    }

    void OnDestroy()
    {
        ghost?.Dispose();
        current?.Dispose();
        if (glowInstance != null) Destroy(glowInstance);
    }

    /// <summary>Explode or reassemble (goes through the session, so the hub and the pet see it too).</summary>
    public void SetExploded(bool on) => session.SetView(on, focusId);

    async void OnModelChanged(CadUpdate update)
    {
        if (current != null && current.Version == update.info.version) return;
        int token = ++loadToken;
        CadModel model;
        try
        {
            model = await CadModel.LoadAsync(update.info.version, update.glb, update.metadata, holder,
                destroyCancellationToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            Debug.LogError($"[CadViewer] Could not load v{update.info.version}: {e.Message}");
            return;
        }
        if (token != loadToken) { model.Dispose(); return; }  // a newer version arrived while this one loaded
        Swap(model, update.info);
    }

    void Swap(CadModel model, CadModelUpdated info)
    {
        var previous = current;
        current = model;
        bool animate = previous != null && info.previousVersion.HasValue;
        EndGlow();
        ClearDim();
        DropGhost();
        if (previous != null)
        {
            if (animate) StartGhost(previous, info);
            else previous.Dispose();
        }
        if (!fitted || Outgrown())
        {
            FitToVolume();
            fitted = true;
        }
        BuildExplode();
        UpdateMarkers(info.analysis, animate);
        ApplyFocus();  // before the glow, so dimming never touches the temporary glow layers
        if (animate) StartGlow(info.changedIds);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (ghost != null)
        {
            ghostT += dt / ghostSeconds;
            var c = ghostColor;
            c.a *= 1f - Smooth(ghostT);
            block.SetColor(ColorId, c);
            for (int i = 0; i < ghostRenderers.Count; i++) ghostRenderers[i].SetPropertyBlock(block);
            if (ghostT >= 1f) DropGhost();
        }
        if (glowLayers.Count > 0)
        {
            glowT += dt / glowSeconds;
            float pulse = 0.65f + 0.35f * Mathf.Sin(glowT * 18f);
            glowInstance.SetFloat(WidthId, glowWidth * pulse * (1f - Smooth(glowT)));
            if (glowT >= 1f) EndGlow();
        }
        if (markerT < 1f)
        {
            markerT = Mathf.Min(1f, markerT + dt / markerSeconds);
            float e = Smooth(markerT);
            SetHeight(cgRing, Mathf.Lerp(cgFrom, cgTo, e));
            SetHeight(cpRing, Mathf.Lerp(cpFrom, cpTo, e));
        }
        if (explodeT != explodeTarget)
        {
            explodeT = Mathf.MoveTowards(explodeT, explodeTarget, dt * 2.5f);
            ApplyExplode();
        }
    }

    // Fit

    void FitToVolume()
    {
        // Fit to the model alone: hide what shouldn't count (collars, the fading ghost).
        bool cg = cgRing.gameObject.activeSelf, cp = cpRing.gameObject.activeSelf;
        cgRing.gameObject.SetActive(false);
        cpRing.gameObject.SetActive(false);
        if (ghost != null) ghost.Root.SetActive(false);
        volume.Fit();
        var root = volume.ContentRoot;  // Fit stood the model on the base; shrink about the base for headroom
        root.localScale *= fitHeadroom;
        root.localPosition *= fitHeadroom;
        cgRing.gameObject.SetActive(cg);
        cpRing.gameObject.SetActive(cp);
        if (ghost != null) ghost.Root.SetActive(true);
    }

    bool Outgrown()
    {
        var b = LocalBounds(current.Root.GetComponentsInChildren<Renderer>(), volume.ContentRoot.parent);
        return b.size.y > volume.size.y * 1.1f || b.size.x > volume.size.x * 1.4f || b.size.z > volume.size.z * 1.4f;
    }

    // Ghost: what the changed parts were

    void StartGhost(CadModel old, CadModelUpdated info)
    {
        ghost = old;
        ghostT = 0f;
        var keep = new HashSet<string>(info.changedIds);
        keep.UnionWith(info.removedIds);
        foreach (var r in old.Root.GetComponentsInChildren<Renderer>(true))
        {
            var part = r.GetComponentInParent<CadPart>();
            if (ghostMaterial == null || part == null || !keep.Contains(part.id)) { r.enabled = false; continue; }
            var materials = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = ghostMaterial;
            r.sharedMaterials = materials;
            ghostRenderers.Add(r);
        }
        old.Root.name += " (ghost)";
    }

    void DropGhost()
    {
        ghost?.Dispose();
        ghost = null;
        ghostRenderers.Clear();
        ghostT = 1f;
    }

    // Glow: cad-vision's silhouette rim on the parts that changed. OCCT's GLBs split a part into several
    // primitives (submeshes), so the mask and rim go on child renderers that cover every submesh; the
    // render queue draws all masks before any rim.

    void StartGlow(List<string> ids)
    {
        if (glowInstance == null || outlineMask == null || ids == null || ids.Count == 0) return;
        var set = new HashSet<string>(ids);
        foreach (var r in current.Root.GetComponentsInChildren<Renderer>(true))
        {
            var part = r.GetComponentInParent<CadPart>();
            if (part == null || !set.Contains(part.id)) continue;
            AddGlowLayer(r, outlineMask);
            AddGlowLayer(r, glowInstance);
        }
        glowT = 0f;
    }

    void AddGlowLayer(Renderer r, Material material)
    {
        var filter = r.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return;
        var layer = new GameObject("Glow " + material.shader.name);
        layer.transform.SetParent(r.transform, false);
        layer.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
        var layerRenderer = layer.AddComponent<MeshRenderer>();
        var materials = new Material[filter.sharedMesh.subMeshCount];
        for (int i = 0; i < materials.Length; i++) materials[i] = material;
        layerRenderer.sharedMaterials = materials;
        layerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        layerRenderer.receiveShadows = false;
        glowLayers.Add(layer);
    }

    void EndGlow()
    {
        foreach (var layer in glowLayers)
            if (layer != null) Destroy(layer);
        glowLayers.Clear();
        glowT = 1f;
    }

    // CG / CP collars

    Transform MakeCollar(string label, Material material)
    {
        var collar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        collar.name = label + " marker";
        Destroy(collar.GetComponent<Collider>());
        if (material != null) collar.GetComponent<MeshRenderer>().sharedMaterial = material;
        collar.transform.SetParent(holder, false);
        collar.SetActive(false);
        return collar.transform;
    }

    void UpdateMarkers(CadAnalysis analysis, bool animate)
    {
        var markers = analysis?.markers;
        bool show = showMarkers && markers?.cg != null && markers.cp != null && analysis.diameterMm.HasValue;
        bool wasShown = markersVisible;
        markersVisible = show;
        cgRing.gameObject.SetActive(show && explodeT < 0.02f);
        cpRing.gameObject.SetActive(show && explodeT < 0.02f);
        if (!show) return;
        // Collars stick out a little past the body tube; the primitive is 2 units tall, so y = half the thickness.
        float d = analysis.diameterMm.Value / 1000f * 1.45f;
        cgRing.localScale = new Vector3(d, 0.0012f, d);
        cpRing.localScale = new Vector3(d * 1.12f, 0.0012f, d * 1.12f);
        cgTo = markers.cg[1];
        cpTo = markers.cp[1];
        cgFrom = animate && wasShown ? cgRing.localPosition.y : cgTo;
        cpFrom = animate && wasShown ? cpRing.localPosition.y : cpTo;
        markerT = 0f;
    }

    static void SetHeight(Transform t, float y) => t.localPosition = new Vector3(0f, y, 0f);

    // Explode / focus

    void OnViewChanged(CadView view)
    {
        explodeTarget = view.explode == true ? 1f : 0f;
        if (view.focusId == focusId) return;
        EndGlow();
        ClearDim();
        focusId = view.focusId;
        ApplyFocus();
    }

    void BuildExplode()
    {
        explodeParts.Clear();
        var all = LocalBounds(current.Root.GetComponentsInChildren<Renderer>(), holder);
        // Spread parts away from the model's centre, along the axis and sideways; then lift everything so
        // nothing sinks below the base.
        var offsets = new Vector3[current.partIds.Count];
        float lowest = 0f;
        for (int i = 0; i < offsets.Length; i++)
        {
            var d = LocalBounds(current.objects[current.partIds[i]].GetComponentsInChildren<Renderer>(), holder).center - all.center;
            offsets[i] = new Vector3(d.x * explodeRadial, d.y * explodeAxial, d.z * explodeRadial);
            lowest = Mathf.Min(lowest, offsets[i].y);
        }
        for (int i = 0; i < offsets.Length; i++)
        {
            var t = current.objects[current.partIds[i]].transform;
            var lifted = offsets[i] + Vector3.up * -lowest;
            var offset = t.parent.InverseTransformVector(holder.TransformVector(lifted));
            explodeParts.Add(new ExplodePart { transform = t, home = t.localPosition, offset = offset });
        }
        ApplyExplode();
    }

    void ApplyExplode()
    {
        float e = Smooth(explodeT);
        for (int i = 0; i < explodeParts.Count; i++)
        {
            var p = explodeParts[i];
            p.transform.localPosition = p.home + p.offset * e;
        }
        // CG/CP belong to the assembled rocket.
        bool collars = markersVisible && explodeT < 0.02f;
        if (cgRing.gameObject.activeSelf != collars)
        {
            cgRing.gameObject.SetActive(collars);
            cpRing.gameObject.SetActive(collars);
        }
    }

    void ApplyFocus()
    {
        if (current == null || string.IsNullOrEmpty(focusId) || ghostMaterial == null) return;
        if (!current.objects.TryGetValue(focusId, out var focus)) return;
        var c = ghostColor;
        c.a = focusDim;
        block.SetColor(ColorId, c);
        foreach (var r in current.Root.GetComponentsInChildren<Renderer>())
        {
            if (r.transform.IsChildOf(focus.transform)) continue;
            dimmed.Add(new Swapped { renderer = r, original = r.sharedMaterials });
            var materials = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = ghostMaterial;
            r.sharedMaterials = materials;
            r.SetPropertyBlock(block);
        }
    }

    void ClearDim()
    {
        foreach (var s in dimmed)
        {
            if (s.renderer == null) continue;
            s.renderer.sharedMaterials = s.original;
            s.renderer.SetPropertyBlock(null);
        }
        dimmed.Clear();
    }

    // Helpers

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Bounds of renderers in <paramref name="space"/>'s local coordinates.</summary>
    static Bounds LocalBounds(Renderer[] renderers, Transform space)
    {
        var b = new Bounds();
        bool first = true;
        foreach (var r in renderers)
        {
            var wb = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = wb.center + Vector3.Scale(wb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var local = space.InverseTransformPoint(corner);
                if (first) { b = new Bounds(local, Vector3.zero); first = false; }
                else b.Encapsulate(local);
            }
        }
        return b;
    }
}
