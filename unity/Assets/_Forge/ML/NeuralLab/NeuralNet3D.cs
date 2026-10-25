using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// The Neural Net Lab's network in 3D on the desk (volume ml-neural-3d): inputs → hidden layers → output from left
    /// to right, each layer a ring of glowing neurons, the connections blue (positive) or orange (negative) and thicker
    /// for larger weights. Every <see cref="PulseSeconds"/> it feeds one training point through: the neurons light up
    /// with their activation and a wave of light runs along the connections, layer by layer. A switched-off neuron goes
    /// dark with its connections. Touch a hidden neuron with a fingertip, point a ray at it or click it to switch it off
    /// and on (<see cref="NeuronTouch"/>). Drawn from <see cref="NeuralLabSim"/>; it stands on a <see cref="ForgeVolume"/>.
    /// </summary>
    [RequireComponent(typeof(ForgeVolume))]
    public sealed class NeuralNet3D : MonoBehaviour
    {
        const float LayerGap = 1f, RingRadius = 0.5f, SwayDegrees = 22f, PokeRefresh = 3f, PulseSeconds = 0.9f, WaveSeconds = 0.5f;
        static readonly Color Blue = new Color32(0x4C, 0x9B, 0xFF, 0xFF), Orange = new Color32(0xFF, 0x9A, 0x3C, 0xFF);
        static readonly Color Dim = new Color(0.16f, 0.17f, 0.2f), Mid = new Color(0.22f, 0.24f, 0.3f);
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Unlit material for the neurons (URP Unlit); each neuron's color goes in a property block.")]
        public Material nodeMaterial;
        [Tooltip("Vertex-colored transparent material for the connections (Sprites/Default).")]
        public Material edgeMaterial;

        class Neuron { public int layer, index; public Transform t; public Renderer r; public float size; public NeuronTouch touch; }
        class Edge { public LineRenderer line; public int layer, to, from; }

        NeuralLabSim sim;
        ForgeVolume volume;
        Transform pivot, net;  // pivot: the network's center, which sways; net: the neurons and links
        readonly List<Neuron> neurons = new List<Neuron>();
        readonly List<Edge> edges = new List<Edge>();
        MaterialPropertyBlock block;
        Material ownNode, ownEdge;
        float pulseStart = -10f, pokesFound = -100f;
        int sample;
        PokeInteractor[] pokes = new PokeInteractor[0];

        void Start()
        {
            volume = GetComponent<ForgeVolume>();
            block = new MaterialPropertyBlock();
            if (nodeMaterial == null) nodeMaterial = ownNode = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            if (edgeMaterial == null) edgeMaterial = ownEdge = new Material(Shader.Find("Sprites/Default"));
            sim = NeuralLabSim.Instance;
            sim.Rebuilt += Build;
            if (FindAnyObjectByType<NeuronRaycaster>() == null) gameObject.AddComponent<NeuronRaycaster>();  // mouse clicks
            Build();
        }

        void OnDestroy()
        {
            if (sim != null) sim.Rebuilt -= Build;
            if (ownNode != null) Destroy(ownNode);
            if (ownEdge != null) Destroy(ownEdge);
        }

        void Build()
        {
            neurons.Clear(); edges.Clear();
            var content = new GameObject("Neural network");
            pivot = content.transform;
            net = new GameObject("Layers").transform; net.SetParent(pivot, false);
            var layers = new List<List<Neuron>>();
            int inputs = sim.FeatureCount, last = sim.Hidden.Count + 1;
            for (int l = 0; l <= last; l++)
            {
                int count = l == 0 ? inputs : l == last ? 1 : sim.Hidden[l - 1];
                float size = l == 0 ? 0.12f : l == last ? 0.24f : 0.17f;
                var column = new List<Neuron>();
                for (int i = 0; i < count; i++) column.Add(NewNeuron(l, i, Place(l, i, count), size));
                layers.Add(column);
            }
            for (int l = 1; l <= last; l++)
                foreach (var to in layers[l])
                    foreach (var from in layers[l - 1])
                        edges.Add(NewEdge(from.t.localPosition, to.t.localPosition, l, to.index, from.index));
            // Fit to the box from the neurons themselves: ForgeVolume.Fit reads renderer bounds, which aren't up to date
            // for lines made this frame (and still count the old network until it's destroyed at the end of the frame).
            volume.SetContent(content, fit: false);
            var b = new Bounds(neurons[0].t.localPosition, Vector3.zero);
            foreach (var n in neurons) b.Encapsulate(new Bounds(n.t.localPosition, Vector3.one * n.size));
            net.localPosition = -b.center;  // the pivot sits at the network's center, so the sway turns it in place
            var box = volume.size;
            float scale = Mathf.Min(box.x / b.size.x, box.y / b.size.y, box.z / Mathf.Max(b.size.z, 0.3f));
            var root = volume.ContentRoot;
            root.localScale = Vector3.one * scale;
            root.localPosition = new Vector3(0, b.extents.y * scale + 0.02f, 0);  // standing just above the base
            pulseStart = Time.time;
        }

        // Layer l along x; its neurons on a ring across y/z (a line when there are 1-2), so it reads in depth.
        static Vector3 Place(int layer, int index, int count)
        {
            float x = layer * LayerGap;
            if (count == 1) return new Vector3(x, RingRadius, 0);
            if (count == 2) return new Vector3(x, RingRadius + (index == 0 ? 0.18f : -0.18f), 0);
            float r = RingRadius * Mathf.Clamp01(0.55f + count * 0.06f);
            float a = index / (float)count * Mathf.PI * 2f;
            return new Vector3(x, RingRadius + Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }

        Neuron NewNeuron(int layer, int index, Vector3 position, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bool hidden = layer > 0 && layer <= sim.Hidden.Count;  // only hidden neurons switch off
            if (!hidden) Destroy(go.GetComponent<Collider>());
            go.name = layer == 0 ? $"Input {index + 1}" : $"Neuron L{layer}N{index + 1}";
            go.transform.SetParent(net, false);
            go.transform.localPosition = position; go.transform.localScale = Vector3.one * size;
            var r = go.GetComponent<Renderer>(); r.sharedMaterial = nodeMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            var n = new Neuron { layer = layer, index = index, t = go.transform, r = r, size = size };
            if (hidden) { n.touch = go.AddComponent<NeuronTouch>(); n.touch.Setup(layer, index); }
            neurons.Add(n);
            return n;
        }

        Edge NewEdge(Vector3 from, Vector3 to, int layer, int toIndex, int fromIndex)
        {
            var go = new GameObject($"Link L{layer} {fromIndex + 1}→{toIndex + 1}");
            go.transform.SetParent(net, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false; line.positionCount = 2;
            line.SetPosition(0, from); line.SetPosition(1, to);
            line.sharedMaterial = edgeMaterial; line.numCapVertices = 2;
            line.startWidth = line.endWidth = 0.002f;  // the default 1 m width would make ForgeVolume.Fit shrink the net to a speck
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
            return new Edge { line = line, layer = layer, to = toIndex, from = fromIndex };
        }

        void Update()
        {
            if (sim == null || sim.Net == null || net == null) return;
            // A gentle sway instead of a full turntable: the layers stay readable left to right, and it still looks 3D.
            pivot.localRotation = Quaternion.Euler(0, SwayDegrees * Mathf.Sin(Time.time * 0.35f), 0);
            if (Time.time - pulseStart >= PulseSeconds)
            {
                pulseStart = Time.time;
                if (sim.Train.Count > 0) { sample = (sample + 37) % sim.Train.Count; var p = sim.Train[sample]; sim.Predict(p.x, p.y); }
            }
            else if (sim.Train.Count > 0) { var p = sim.Train[sample]; sim.Predict(p.x, p.y); }  // keep the activations of this point
            float wave = (Time.time - pulseStart) / WaveSeconds * (sim.Hidden.Count + 1);  // which layer the light is at
            Paint(wave);
        }

        // Fingertips (the hands' PokeInteractors, looked up every few seconds): touching a hidden neuron switches it.
        void LateUpdate()
        {
            if (neurons.Count == 0) return;
            if (Time.unscaledTime - pokesFound > PokeRefresh || Time.unscaledTime < pokesFound)
            {
                pokesFound = Time.unscaledTime;
                pokes = FindObjectsByType<PokeInteractor>(FindObjectsSortMode.None);
            }
            foreach (var n in neurons)
            {
                if (n.touch == null) continue;
                float radius = n.t.lossyScale.x * 0.5f, nearest = float.MaxValue;
                for (int i = 0; i < pokes.Length; i++)
                {
                    var poke = pokes[i];
                    if (poke == null || !poke.isActiveAndEnabled || poke.State == InteractorState.Disabled) continue;
                    nearest = Mathf.Min(nearest, Vector3.Distance(poke.Origin, n.t.position) - radius);
                }
                n.touch.Poke(nearest);
            }
        }

        void Paint(float wave)
        {
            var nn = sim.Net; int last = nn.Layers - 1;
            float relu = sim.Activation == NNActivation.ReLU || sim.Activation == NNActivation.Linear ? 0.3f : 1f;
            foreach (var n in neurons)
            {
                bool off = n.layer > 0 && n.layer < last && nn.disabled[n.layer][n.index];
                Color c;
                if (off) c = Dim;
                else
                {
                    float a = nn.NeuronOutput(n.layer, n.index);
                    if (n.layer == 0) a = Mathf.Clamp(a / NNData.Extent, -1, 1);
                    else if (n.layer < last) a = sim.Activation == NNActivation.Sigmoid ? a * 2 - 1 : a * relu;
                    a = Mathf.Clamp(a, -1, 1);
                    float lit = Mathf.Clamp01(1f - Mathf.Abs(wave - n.layer));  // the wave passing this layer
                    c = Color.Lerp(Mid, a >= 0 ? Blue : Orange, 0.35f + 0.65f * Mathf.Abs(a));
                    c = Color.Lerp(c, Color.white, 0.35f * lit);
                }
                bool hover = n.touch != null && n.touch.Hovered;
                if (hover) c = Color.Lerp(c, Color.white, 0.45f);  // a finger or pointer on it: it can be switched
                n.r.GetPropertyBlock(block); block.SetColor(BaseColor, c); block.SetColor("_Color", c); n.r.SetPropertyBlock(block);
                n.t.localScale = Vector3.one * ((off ? n.size * 0.6f : n.size) * (hover ? 1.3f : 1f));
            }
            foreach (var e in edges)
            {
                float w = nn.weights[e.layer][e.to][e.from];
                bool off = (e.layer < last && nn.disabled[e.layer][e.to]) || (e.layer > 1 && nn.disabled[e.layer - 1][e.from]);
                float lit = Mathf.Clamp01(1f - Mathf.Abs(wave - (e.layer - 0.5f)));  // light running between the layers
                Color c = w >= 0 ? Blue : Orange;
                c.a = off ? 0.04f : Mathf.Clamp(0.12f + Mathf.Abs(w) * 0.35f + 0.5f * lit, 0.12f, 1f);
                if (!off) c = Color.Lerp(c, Color.white, 0.4f * lit);
                e.line.startColor = e.line.endColor = c;
                float width = off ? 0.001f : Mathf.Clamp(0.0015f + Mathf.Abs(w) * 0.002f, 0.0015f, 0.006f);
                e.line.startWidth = e.line.endWidth = width;  // meters: line widths don't follow the transform's scale
            }
        }
    }
}
