using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The parts CadViewer lifts and turns to glass so you can see inside (CAD UX spec 4.3 and 5.3): the gearbox's lid
/// during Run (lifted to 55% of its explode distance, 45% glass) and on the Inspect stage (38% glass). Which parts:
/// <see cref="CadDemoPlayer.RevealIds"/>. Glass is the viewer's ghost material (Forge/CadGhost) at the opacity left
/// over; solid again, the parts get their own materials back. Only touches renderers and transforms when a value
/// changes. Bind before a focus dims the model, and Reset before a focus changes (CadViewer does both).
/// </summary>
public sealed class CadReveal
{
    struct Item
    {
        public Renderer renderer;
        public Material[] original;
    }

    struct Lift
    {
        public Transform transform;
        public Vector3 home, offset;  // parent space: assembled position, full explode
    }

    static readonly int ColorId = Shader.PropertyToID("_Color");

    readonly List<Item> items = new List<Item>();
    readonly List<Lift> lifts = new List<Lift>();
    readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    Material ghost;
    Color ghostColor;
    float lift, glass = -1f;
    bool glassy;

    public bool Any => items.Count > 0;

    /// <summary>The parts of a newly shown version to reveal (null or empty: none).</summary>
    public void Bind(CadModel model, List<string> ids, CadExploder exploder, Material ghostMaterial, Color color)
    {
        Reset();
        items.Clear();
        lifts.Clear();
        ghost = ghostMaterial;
        ghostColor = color;
        if (model == null || ids == null) return;
        foreach (var id in ids)
        {
            if (string.IsNullOrEmpty(id) || !model.objects.ContainsKey(id)) continue;
            int index = exploder != null ? exploder.IndexOf(id) : -1;
            if (index >= 0 && exploder[index].transform != null)
                lifts.Add(new Lift { transform = exploder[index].transform, home = exploder[index].home, offset = exploder[index].offset });
            foreach (var r in model.Renderers(id))
                if (r != null && !r.name.StartsWith("Glow ", StringComparison.Ordinal))
                    items.Add(new Item { renderer = r, original = r.sharedMaterials });
        }
    }

    /// <summary><paramref name="liftShare"/>: share of the explode distance; <paramref name="glassShare"/>: 0 solid, 1 clear.</summary>
    public void Apply(float liftShare, float glassShare)
    {
        if (Mathf.Abs(liftShare - lift) > 1e-4f)
        {
            lift = liftShare;
            for (int i = 0; i < lifts.Count; i++)
                if (lifts[i].transform != null) lifts[i].transform.localPosition = lifts[i].home + lifts[i].offset * lift;
        }
        bool want = glassShare > 0.01f && ghost != null && items.Count > 0;
        if (want != glassy)
        {
            glassy = want;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.renderer == null) continue;
                if (want)
                {
                    var materials = new Material[it.original.Length];
                    for (int m = 0; m < materials.Length; m++) materials[m] = ghost;
                    it.renderer.sharedMaterials = materials;
                }
                else
                {
                    it.renderer.sharedMaterials = it.original;
                    it.renderer.SetPropertyBlock(null);
                }
            }
            glass = -1f;
        }
        if (!want || Mathf.Abs(glassShare - glass) < 0.004f) return;
        glass = glassShare;
        var c = ghostColor;
        c.a = Mathf.Clamp01(1f - glassShare);
        block.Clear();
        block.SetColor(ColorId, c);
        for (int i = 0; i < items.Count; i++)
            if (items[i].renderer != null) items[i].renderer.SetPropertyBlock(block);
    }

    /// <summary>Everything solid and in place again.</summary>
    public void Reset()
    {
        if (lift != 0f)
        {
            lift = 0f;
            for (int i = 0; i < lifts.Count; i++)
                if (lifts[i].transform != null) lifts[i].transform.localPosition = lifts[i].home;
        }
        if (glassy)
        {
            glassy = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].renderer == null) continue;
                items[i].renderer.sharedMaterials = items[i].original;
                items[i].renderer.SetPropertyBlock(null);
            }
        }
        glass = -1f;
    }
}
