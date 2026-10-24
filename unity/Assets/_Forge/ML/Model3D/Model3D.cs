using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// The model as a 3D stack of edge-lit glass slabs floating in front of the panel. Each slab is a block of layers
    /// (a conv block, the flatten, a dense layer); its footprint follows the block's output (wide = big feature maps,
    /// deep = many channels). Hovering a slab grows it and shows a callout; clicking it (or Open) unfolds it into one
    /// thin slab per layer, each labelled. Drag turns the stack, scroll zooms. A packet of light runs down the spine
    /// on every metrics tick.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public class Model3D : MonoBehaviour
    {
        public MLSession session;
        public Material slabMaterial;
        public Material beamMaterial;

        // mm (canvas units)
        const float GroupThickness = 12f, GroupGap = 10f, LayerThickness = 5f, LayerPitch = 19f, OpenPad = 16f;
        const float TopBar = 40f, BottomBar = 30f, CalloutWidth = 200f, CalloutHeight = 132f, TagWidth = 170f;
        const float DefaultYaw = -34f, DefaultPitch = -26f;

        sealed class Slab
        {
            public Transform t; public Renderer r;
            public Vector3 size; public Color color; public Vector2 grid;
            public int group, layer;   // layer -1: the group's own slab
            public float y, yTarget, vis, visTarget, hover, pulse;
            public float minX, maxX, midY, top, bottom;   // projected on the panel, top-left mm
        }
        sealed class Group
        {
            public int first, count; public string title, range, shapes, kinds; public long parameters; public Color color;
            public bool open; public Slab slab;
            public RectTransform bracket; public Image line, tickTop, tickBottom; public ForgePill pill;
        }
        sealed class Tag { public RectTransform rect; public Image leader; public TMP_Text text; public CanvasGroup fade; }

        RectTransform root;
        Transform stage, tower, spine;
        Renderer spineRenderer;
        Group[] groups;
        Slab[] layers;
        Tag[] tags;
        MaterialPropertyBlock block;
        Material ownSlab, ownBeam;
        MLLayer[] built;

        float yaw = DefaultYaw, pitch = DefaultPitch, zoom = 1f, viewYaw = DefaultYaw, viewPitch = DefaultPitch, viewZoom = 1f;
        float fit = 1f, swayAmp, lastInput = -10f, stackHeight, maxHalfDiag, packet = -1f;
        bool dragging, dragMoved; Vector2 dragLast; Vector3 eye;
        int hoverGroup = -1, hoverLayer = -1, selectedLayer = -1, calloutGroup = -1, shownCallout = -2;
        bool overCallout; float lastHover;

        TMP_Text inputTag, outputTag, calloutTitle, calloutRange, calloutShapes, calloutMeta, calloutKinds;
        RectTransform callout; Image calloutLeader; ForgeSurface calloutDot; CanvasGroup calloutFade;
        ForgePill expandAll;

        public int GroupCount => groups == null ? 0 : groups.Length;
        public bool IsOpen(int group) => groups != null && group >= 0 && group < groups.Length && groups[group].open;

        void Start()
        {
            session = MLSession.Resolve(session);
            block = new MaterialPropertyBlock();
            Build();
            session.Metrics += OnMetrics; session.Reloaded += OnReloaded;
        }
        // The hub can send a different model (another repo, or graph_ready): rebuild only when the layers changed.
        void OnReloaded() { if (session.Data.layers != built) Build(); }
        void OnMetrics(MLMetrics tick) { packet = 0f; }

        // ---- Build ----

        void Build()
        {
            root = GetComponent<ForgePanel>().Content;
            for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
            built = session.Data.layers;
            var data = built ?? new MLLayer[0];
            hoverGroup = hoverLayer = selectedLayer = calloutGroup = -1; shownCallout = -2; overCallout = false; packet = -1f;
            groups = BuildGroups(data);
            if (block == null) block = new MaterialPropertyBlock();
            if (slabMaterial == null && ownSlab == null) ownSlab = new Material(Shader.Find("Forge/ML/Slab"));
            if (beamMaterial == null && ownBeam == null) ownBeam = new Material(Shader.Find("Forge/ML/Beam"));

            // Hit surface first so everything else draws over it.
            var hit = MLUI.Rect("Model hit area", root, 0, 0, 10, 10);
            hit.anchorMin = Vector2.zero; hit.anchorMax = Vector2.one; hit.offsetMin = new Vector2(0, BottomBar); hit.offsetMax = new Vector2(0, -TopBar);
            hit.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            hit.gameObject.AddComponent<MLModelLayerInput>().owner = this;

            stage = new GameObject("Model stage").transform; stage.SetParent(root, false);
            tower = new GameObject("Tower").transform; tower.SetParent(stage, false);
            spine = Cube("Spine", beamMaterial != null ? beamMaterial : ownBeam).transform;
            spineRenderer = spine.GetComponent<Renderer>();

            layers = new Slab[data.Length];
            maxHalfDiag = 40f;
            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                Vector3 size = Vector3.zero; Vector2 grid = Vector2.one;
                for (int i = group.first; i < group.first + group.count; i++)
                {
                    var layer = data[i];
                    Vector3 layerSize = Footprint(layer.out_shape, LayerThickness);
                    if (layerSize.x * layerSize.z > size.x * size.z) { size = layerSize; grid = Grid(layer.out_shape); }
                    layers[i] = NewSlab(layer.name, layerSize, Grid(layer.out_shape), TypeColor(layer.type), g, i);
                }
                size.y = GroupThickness;
                group.slab = NewSlab(group.title, size, grid, group.color, g, -1);
                maxHalfDiag = Mathf.Max(maxHalfDiag, 0.5f * Mathf.Sqrt(size.x * size.x + size.z * size.z));
            }

            BuildChrome(data);
            Layout(true);
        }

        Group[] BuildGroups(MLLayer[] data)
        {
            var list = new List<Group>();
            Group current = null; bool hasAnchor = false;
            for (int i = 0; i < data.Length; i++)
            {
                bool anchor = IsAnchor(data[i].type);
                if (current == null || (anchor && hasAnchor)) { current = new Group { first = i }; list.Add(current); hasAnchor = false; }
                current.count++; hasAnchor |= anchor;
            }
            int convs = 0, denses = 0;
            for (int g = 0; g < list.Count; g++)
            {
                var group = list[g];
                string kind = data[group.first].type;
                for (int i = group.first; i < group.first + group.count; i++) if (IsAnchor(data[i].type)) { kind = data[i].type; break; }
                string k = (kind ?? "").ToLowerInvariant();
                if (k.Contains("conv")) group.title = "Conv block " + ++convs;
                else if (k.Contains("linear") || k.Contains("dense")) group.title = g == list.Count - 1 ? "Output" : "Dense " + ++denses;
                else if (k.Contains("reshape") || k.Contains("flatten")) group.title = "Flatten";
                else group.title = TypeName(kind);
                group.color = TypeColor(kind);
                var first = data[group.first]; var last = data[group.first + group.count - 1];
                group.range = group.count > 1 ? first.name + " – " + last.name : first.name;
                group.shapes = Shape(first.in_shape) + "  →  " + Shape(last.out_shape);
                var kinds = new List<string>();
                for (int i = group.first; i < group.first + group.count; i++) { group.parameters += data[i].@params; kinds.Add(TypeName(data[i].type)); }
                group.kinds = string.Join("  ·  ", kinds);
            }
            return list.ToArray();
        }

        Slab NewSlab(string name, Vector3 size, Vector2 grid, Color color, int group, int layer)
        {
            var go = Cube(name, slabMaterial != null ? slabMaterial : ownSlab);
            return new Slab { t = go.transform, r = go.GetComponent<Renderer>(), size = size, grid = grid, color = color, group = group, layer = layer };
        }

        GameObject Cube(string name, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); Destroy(go.GetComponent<Collider>()); go.name = name;
            go.transform.SetParent(tower, false);
            var r = go.GetComponent<Renderer>(); r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            go.layer = gameObject.layer;
            return go;
        }

        void BuildChrome(MLLayer[] data)
        {
            long total = 0; foreach (var layer in data) total += layer.@params;
            MLUI.Caps(root, $"{groups.Length} blocks  ·  {data.Length} layers  ·  {MLUI.Count(total)} params", 4, 11, 330, 16, 10);
            var reset = MLUI.Pill(root, "Reset view", 0, 4, 92, 28, ResetView); RightAlign(reset.Rect, 4, 4);
            expandAll = MLUI.Pill(root, "Open all", 0, 4, 84, 28, ToggleAll); RightAlign(expandAll.Rect, 102, 4);

            // Legend: one key per layer type in this model, and how to use it.
            var legend = MLUI.Rect("Legend", root, 0, 0, 10, BottomBar);
            legend.anchorMin = Vector2.zero; legend.anchorMax = new Vector2(1, 0); legend.pivot = Vector2.zero;
            legend.anchoredPosition = Vector2.zero; legend.sizeDelta = new Vector2(0, BottomBar);
            float x = 4; var seen = new List<string>();
            foreach (var layer in data)
            {
                string name = TypeName(layer.type); if (seen.Contains(name)) continue; seen.Add(name);
                MLUI.Dot(legend, x, 12, 7, TypeColor(layer.type));
                var label = MLUI.Text(legend, name, x + 11, 7, 80, 16, 10); label.color = MLUI.Sub; label.textWrappingMode = TextWrappingModes.NoWrap;
                x += 19 + label.GetPreferredValues(name).x;
            }
            var hint = MLUI.Text(legend, "Drag to turn  ·  Scroll to zoom  ·  Click a block to open it", 0, 7, 10, 16, 10);
            hint.color = MLUI.Faint; hint.alignment = TextAlignmentOptions.Right; hint.textWrappingMode = TextWrappingModes.NoWrap;
            hint.rectTransform.anchorMin = new Vector2(0, 1); hint.rectTransform.anchorMax = new Vector2(1, 1);
            hint.rectTransform.offsetMin = new Vector2(x + 12, -23); hint.rectTransform.offsetMax = new Vector2(-4, -7);

            // Brackets (left) for open blocks, one tag (right) per layer, the input and output ends.
            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g]; int index = g;
                group.bracket = MLUI.Rect("Bracket " + group.title, root, 0, 0, 1, 1);
                group.line = MLUI.Fill(group.bracket, 0, 0, 1.4f, 10, MLUI.Faint);
                group.tickTop = MLUI.Fill(group.bracket, 0, 0, 7, 1.4f, MLUI.Faint);
                group.tickBottom = MLUI.Fill(group.bracket, 0, 0, 7, 1.4f, MLUI.Faint);
                group.pill = MLUI.Pill(group.bracket, group.title + "   ×", 0, 0, 0, 22, () => SetOpen(index, false));
                group.bracket.gameObject.SetActive(false);
            }
            tags = new Tag[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                var tag = new Tag { rect = MLUI.Rect("Tag " + data[i].name, root, 0, 0, TagWidth, 16) };
                tag.fade = tag.rect.gameObject.AddComponent<CanvasGroup>(); tag.fade.blocksRaycasts = false;
                tag.leader = MLUI.Fill(root, 0, 0, 1, 1, new Color(1, 1, 1, 0.22f)); tag.leader.name = "Leader " + data[i].name;
                tag.text = MLUI.Text(tag.rect, "", 0, 0, TagWidth, 16, 11); tag.text.textWrappingMode = TextWrappingModes.NoWrap;
                tag.text.alignment = TextAlignmentOptions.TopLeft; tag.text.lineSpacing = -8;
                tags[i] = tag; SetTagText(i, false);
                tag.rect.gameObject.SetActive(false); tag.leader.gameObject.SetActive(false);
            }
            if (data.Length > 0)
            {
                inputTag = MLUI.Caps(root, "Input   " + Shape(data[0].in_shape), 0, 0, 200, 14, 9.5f);
                outputTag = MLUI.Caps(root, "Output   " + Shape(data[data.Length - 1].out_shape), 0, 0, 200, 14, 9.5f);
                inputTag.alignment = outputTag.alignment = TextAlignmentOptions.Center;
            }
            else MLUI.Text(root, "No model graph yet", 0, TopBar + 120, 400, 30, 16).alignment = TextAlignmentOptions.Center;

            // Callout for the block under the pointer. It holds while the pointer is on it, so Open can be pressed.
            calloutLeader = MLUI.Fill(root, 0, 0, 1, 1, new Color(1, 1, 1, 0.35f)); calloutLeader.name = "Callout leader";
            callout = MLUI.Rect("Callout", root, 0, 0, CalloutWidth, CalloutHeight);
            calloutFade = callout.gameObject.AddComponent<CanvasGroup>();
            callout.gameObject.AddComponent<MLModelCalloutInput>().owner = this;
            var card = MLUI.Surface(callout, 0, 0, CalloutWidth, CalloutHeight, new Color(0.11f, 0.12f, 0.15f, 0.97f), 14, 0.35f);
            card.raycastTarget = true;
            calloutDot = MLUI.Dot(callout, 14, 15, 8, Color.white);
            calloutTitle = MLUI.Text(callout, "", 28, 10, 160, 18, 13); calloutTitle.fontStyle = FontStyles.Bold; calloutTitle.characterSpacing = 4;
            calloutRange = MLUI.Text(callout, "", 14, 30, 176, 14, 9.5f); calloutRange.color = MLUI.Faint;
            calloutShapes = MLUI.Text(callout, "", 14, 47, 176, 20, 14); calloutShapes.fontStyle = FontStyles.Bold; calloutShapes.textWrappingMode = TextWrappingModes.NoWrap;
            calloutMeta = MLUI.Text(callout, "", 14, 69, 176, 14, 10.5f); calloutMeta.color = MLUI.Sub;
            calloutKinds = MLUI.Text(callout, "", 14, 85, 176, 14, 9.5f); calloutKinds.textWrappingMode = TextWrappingModes.NoWrap;
            calloutKinds.overflowMode = TextOverflowModes.Ellipsis;
            MLUI.Pill(callout, "Open", 14, 102, 70, 22, () => { if (calloutGroup >= 0) SetOpen(calloutGroup, true); });
            var note = MLUI.Text(callout, "or click the block", 92, 106, 100, 14, 9.5f); note.color = MLUI.Faint;
            calloutFade.alpha = 0; callout.gameObject.SetActive(false); calloutLeader.gameObject.SetActive(false);
        }

        static void RightAlign(RectTransform r, float right, float top)
        {
            r.anchorMin = r.anchorMax = new Vector2(1, 1); r.pivot = new Vector2(1, 1); r.anchoredPosition = new Vector2(-right, -top);
        }

        // ---- Layout ----

        void Layout(bool snap)
        {
            float y = 0;
            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                if (g > 0) y -= group.open || groups[g - 1].open ? OpenPad : GroupGap;
                if (group.open)
                {
                    float top = y;
                    for (int i = 0; i < group.count; i++)
                    {
                        var s = layers[group.first + i];
                        if (i > 0) y -= LayerPitch - LayerThickness;
                        s.yTarget = y - LayerThickness * 0.5f; s.visTarget = 1; y -= LayerThickness;
                    }
                    group.slab.yTarget = (top + y) * 0.5f; group.slab.visTarget = 0;
                }
                else
                {
                    group.slab.yTarget = y - GroupThickness * 0.5f; group.slab.visTarget = 1; y -= GroupThickness;
                    for (int i = 0; i < group.count; i++) { layers[group.first + i].yTarget = group.slab.yTarget; layers[group.first + i].visTarget = 0; }
                }
            }
            stackHeight = -y;
            float shift = stackHeight * 0.5f;
            foreach (var group in groups)
            {
                group.slab.yTarget += shift;
                for (int i = 0; i < group.count; i++) layers[group.first + i].yTarget += shift;
            }
            if (!snap) return;
            foreach (var group in groups)
            {
                group.slab.y = group.slab.yTarget; group.slab.vis = group.slab.visTarget;
                for (int i = 0; i < group.count; i++) { var s = layers[group.first + i]; s.y = s.yTarget; s.vis = s.visTarget; }
            }
        }

        public void SetOpen(int group, bool open)
        {
            if (group < 0 || group >= groups.Length || groups[group].open == open) return;
            groups[group].open = open;
            if (!open && selectedLayer >= groups[group].first && selectedLayer < groups[group].first + groups[group].count) selectedLayer = -1;
            if (open) calloutGroup = -1;
            hoverGroup = hoverLayer = -1;
            lastInput = Time.unscaledTime;
            Layout(false); RefreshButtons();
        }
        public void ExpandAll() { foreach (var group in groups) group.open = true; hoverGroup = hoverLayer = calloutGroup = -1; Layout(false); RefreshButtons(); }
        public void CollapseAll() { foreach (var group in groups) group.open = false; selectedLayer = -1; Layout(false); RefreshButtons(); }
        void ToggleAll() { if (AnyOpen()) CollapseAll(); else ExpandAll(); lastInput = Time.unscaledTime; }
        bool AnyOpen() { foreach (var group in groups) if (group.open) return true; return false; }
        void RefreshButtons() { expandAll.GetComponentInChildren<TMP_Text>().text = AnyOpen() ? "Close all" : "Open all"; }
        void ResetView() { yaw = DefaultYaw; pitch = DefaultPitch; zoom = 1f; lastInput = Time.unscaledTime; }

        // ---- Frame ----

        void LateUpdate()
        {
            if (groups == null || root == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f), now = Time.unscaledTime;
            var cam = Camera.main;
            eye = cam != null ? cam.transform.position : root.position - root.forward * 0.8f;

            // View: smoothed turn and zoom, and a slow sway when left alone so it reads as 3D.
            swayAmp = Mathf.MoveTowards(swayAmp, now - lastInput > 3f && !dragging ? 1f : 0f, dt * (dragging ? 4f : 0.4f));
            float k = 1f - Mathf.Exp(-dt * (dragging ? 22f : 9f));
            viewYaw = Mathf.Lerp(viewYaw, yaw, k); viewPitch = Mathf.Lerp(viewPitch, pitch, k); viewZoom = Mathf.Lerp(viewZoom, zoom, k);
            float sway = Mathf.Sin(now * 0.42f) * 11f * swayAmp;
            tower.localRotation = Quaternion.AngleAxis(viewPitch, Vector3.right) * Quaternion.AngleAxis(viewYaw + sway, Vector3.up);

            // Fit the stack between the top bar and the legend (it grows as blocks open), in the left part of the panel.
            Rect r = root.rect;
            float stageTop = r.yMax - TopBar - 22f, stageBottom = r.yMin + BottomBar + 22f;
            float c = Mathf.Cos(viewPitch * Mathf.Deg2Rad), s = Mathf.Abs(Mathf.Sin(viewPitch * Mathf.Deg2Rad));
            float projected = stackHeight * c + 2f * maxHalfDiag * s + 8f;
            float target = Mathf.Clamp(Mathf.Min((stageTop - stageBottom) / projected, r.width * 0.5f / (2f * maxHalfDiag)), 0.3f, 1.25f);
            fit = Mathf.Lerp(fit, target, 1f - Mathf.Exp(-dt * 6f));
            float scale = fit * viewZoom;
            tower.localScale = new Vector3(scale, scale, scale);
            stage.localPosition = new Vector3(r.xMin + r.width * 0.4f, (stageTop + stageBottom) * 0.5f, -(maxHalfDiag * scale + 16f));

            if (packet >= 0f) { packet += dt / 1.3f; if (packet > 1.2f) packet = -1f; }

            if (hoverGroup >= 0) { calloutGroup = hoverGroup; lastHover = now; }
            else if (!overCallout && now - lastHover > 0.35f) calloutGroup = -1;

            float kPos = 1f - Mathf.Exp(-dt * 11f), kVis = 1f - Mathf.Exp(-dt * 14f), kHover = 1f - Mathf.Exp(-dt * 16f);
            float spineTop = stackHeight * 0.5f + 14f, spineLength = stackHeight + 28f;
            foreach (var group in groups)
            {
                Animate(group.slab, dt, kPos, kVis, kHover, scale, spineTop, spineLength);
                for (int i = 0; i < group.count; i++) Animate(layers[group.first + i], dt, kPos, kVis, kHover, scale, spineTop, spineLength);
            }
            spine.localPosition = new Vector3(0, spineTop - spineLength * 0.5f, 0);
            spine.localScale = new Vector3(1.6f, spineLength, 1.6f);
            block.SetColor("_Color", new Color(0.55f, 0.75f, 1f, 1f));
            block.SetFloat("_Length", spineLength * scale);
            block.SetFloat("_Packet", packet);
            block.SetFloat("_Fade", 1f);
            spineRenderer.SetPropertyBlock(block);

            PlaceOverlay(r);
        }

        void Animate(Slab s, float dt, float kPos, float kVis, float kHover, float scale, float spineTop, float spineLength)
        {
            s.y = Mathf.Lerp(s.y, s.yTarget, kPos);
            s.vis = Mathf.Abs(s.vis - s.visTarget) < 0.003f ? s.visTarget : Mathf.Lerp(s.vis, s.visTarget, kVis);
            bool lit = s.layer < 0 ? s.group == calloutGroup : s.layer == hoverLayer || s.layer == selectedLayer;
            s.hover = Mathf.Lerp(s.hover, lit ? 1f : 0f, kHover);
            float along = (spineTop - s.y) / spineLength;
            float pulse = packet < 0f ? 0f : Mathf.Exp(-Mathf.Pow((packet - along) * spineLength / 18f, 2f));
            s.pulse = Mathf.Max(pulse, s.pulse - dt * 2.5f);
            bool show = s.vis > 0.01f;
            if (s.r.enabled != show) s.r.enabled = show;
            if (!show) return;
            float grow = (1f + 0.07f * s.hover + 0.03f * s.pulse) * Mathf.Lerp(0.86f, 1f, s.vis);
            float thick = s.size.y * Mathf.Lerp(0.35f, 1f, s.vis) * (1f + 0.25f * s.hover);
            s.t.localPosition = new Vector3(0, s.y + 2.5f * s.hover, 0);
            var size = new Vector3(s.size.x * grow, thick, s.size.z * grow);
            s.t.localScale = size;
            block.SetColor("_Color", s.color);
            block.SetVector("_Size", size * scale);
            block.SetVector("_Grid", s.grid);
            block.SetFloat("_Hover", s.hover);
            block.SetFloat("_Pulse", s.pulse);
            block.SetFloat("_Fade", s.vis);
            s.r.SetPropertyBlock(block);
        }

        // Labels, brackets and the callout follow where the slabs appear on the panel from the viewer's eye.
        void PlaceOverlay(Rect r)
        {
            float column = 0f, top = float.MaxValue, bottom = float.MinValue, centre = 0f; int counted = 0;
            foreach (var group in groups)
            {
                Project(group.slab, r);
                for (int i = 0; i < group.count; i++) Project(layers[group.first + i], r);
                if (group.open) for (int i = 0; i < group.count; i++) column = Mathf.Max(column, layers[group.first + i].maxX);
                var first = group.open ? layers[group.first] : group.slab;
                var last = group.open ? layers[group.first + group.count - 1] : group.slab;
                top = Mathf.Min(top, first.top); bottom = Mathf.Max(bottom, last.bottom);
                centre += (first.minX + first.maxX) * 0.5f; counted++;
            }
            if (counted > 0) centre /= counted;
            column = Mathf.Min(column + 20f, r.width - TagWidth - 2f);

            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                float minX = float.MaxValue, from = float.MaxValue, to = float.MinValue, fade = 0f;
                for (int i = group.first; i < group.first + group.count; i++)
                {
                    var s = layers[i]; var tag = tags[i];
                    bool show = s.vis > 0.25f && group.open;
                    if (tag.rect.gameObject.activeSelf != show) { tag.rect.gameObject.SetActive(show); tag.leader.gameObject.SetActive(show); }
                    if (!show) continue;
                    float alpha = Mathf.InverseLerp(0.25f, 1f, s.vis);
                    tag.fade.alpha = alpha;
                    tag.rect.anchoredPosition = new Vector2(column, -(s.midY - 8f));
                    bool lit = i == hoverLayer || i == selectedLayer;
                    tag.leader.color = new Color(1, 1, 1, (lit ? 0.55f : 0.2f) * alpha);
                    MLUI.SetLine(tag.leader.rectTransform, new Vector2(s.maxX + 3f, s.midY), new Vector2(column - 5f, s.midY), 1f);
                    minX = Mathf.Min(minX, s.minX); from = Mathf.Min(from, s.midY); to = Mathf.Max(to, s.midY); fade = Mathf.Max(fade, s.vis);
                }
                bool bracket = group.open && fade > 0.25f;
                if (group.bracket.gameObject.activeSelf != bracket) group.bracket.gameObject.SetActive(bracket);
                if (!bracket) continue;
                float x = minX - 14f;
                from -= 7f; to += 7f;
                group.line.rectTransform.anchoredPosition = new Vector2(x, -from); group.line.rectTransform.sizeDelta = new Vector2(1.4f, to - from);
                group.tickTop.rectTransform.anchoredPosition = new Vector2(x, -from); group.tickBottom.rectTransform.anchoredPosition = new Vector2(x, -to + 1.4f);
                var pill = group.pill.Rect; float w = pill.sizeDelta.x, px = x - 8f - w, py = from - 2f;
                if (px < 2f) { px = Mathf.Max(2f, x - w * 0.5f); py = from - 28f; }
                pill.anchoredPosition = new Vector2(px, -py);
            }

            if (inputTag != null)
            {
                inputTag.rectTransform.anchoredPosition = new Vector2(centre - 100f, -(top - 20f));
                outputTag.rectTransform.anchoredPosition = new Vector2(centre - 100f, -(bottom + 6f));
            }

            bool showCallout = calloutGroup >= 0 && !groups[calloutGroup].open;
            if (showCallout && shownCallout != calloutGroup) FillCallout(calloutGroup);
            calloutFade.alpha = Mathf.MoveTowards(calloutFade.alpha, showCallout ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            bool active = calloutFade.alpha > 0.01f;
            if (callout.gameObject.activeSelf != active) { callout.gameObject.SetActive(active); calloutLeader.gameObject.SetActive(active); }
            if (!active) { shownCallout = -2; overCallout = false; }
            if (active && shownCallout >= 0)
            {
                var s = groups[shownCallout].slab;
                float x = Mathf.Clamp(s.maxX + 30f, 0f, r.width - CalloutWidth - 4f);
                float y = Mathf.Clamp(s.midY - CalloutHeight * 0.5f, TopBar + 4f, r.height - BottomBar - CalloutHeight - 2f);
                callout.anchoredPosition = new Vector2(x, -y);
                calloutLeader.color = new Color(1, 1, 1, 0.4f * calloutFade.alpha);
                MLUI.SetLine(calloutLeader.rectTransform, new Vector2(s.maxX + 3f, s.midY), new Vector2(x, Mathf.Clamp(s.midY, y + 16f, y + CalloutHeight - 16f)), 1.2f);
            }
        }

        void FillCallout(int index)
        {
            var group = groups[index]; shownCallout = index;
            calloutDot.color = group.color;
            calloutTitle.text = group.title.ToUpperInvariant();
            calloutRange.text = group.range;
            calloutShapes.text = group.shapes;
            calloutMeta.text = $"{group.count} layer{(group.count == 1 ? "" : "s")}  ·  {group.parameters:N0} params";
            calloutKinds.text = group.kinds;
            calloutKinds.color = Color.Lerp(group.color, Color.white, 0.45f);
        }

        void SetTagText(int index, bool detail)
        {
            var layer = built[index];
            string hex = ColorUtility.ToHtmlStringRGB(Color.Lerp(TypeColor(layer.type), Color.white, 0.25f));
            string head = $"<color=#{hex}><b>{TypeName(layer.type)}</b></color>  {Shape(layer.out_shape)}";
            tags[index].text.text = detail
                ? $"{head}\n<size=9><color=#FFFFFF90>{Shape(layer.in_shape)} → {Shape(layer.out_shape)}  ·  {layer.@params:N0} params</color></size>"
                : $"{head}  <size=9><color=#FFFFFF50>{layer.name}</color></size>";
        }

        // The slab's box seen from the eye, flattened onto the panel (top-left mm).
        static readonly Vector3[] Corners =
        {
            new Vector3(-.5f, -.5f, -.5f), new Vector3(.5f, -.5f, -.5f), new Vector3(-.5f, .5f, -.5f), new Vector3(.5f, .5f, -.5f),
            new Vector3(-.5f, -.5f, .5f), new Vector3(.5f, -.5f, .5f), new Vector3(-.5f, .5f, .5f), new Vector3(.5f, .5f, .5f),
        };
        void Project(Slab s, Rect r)
        {
            s.minX = s.top = float.MaxValue; s.maxX = s.bottom = float.MinValue;
            for (int i = 0; i < Corners.Length; i++)
            {
                Vector2 p = ToPanel(s.t.TransformPoint(Corners[i]), r);
                s.minX = Mathf.Min(s.minX, p.x); s.maxX = Mathf.Max(s.maxX, p.x);
                s.top = Mathf.Min(s.top, p.y); s.bottom = Mathf.Max(s.bottom, p.y);
            }
            s.midY = ToPanel(s.t.position, r).y;
        }
        Vector2 ToPanel(Vector3 world, Rect r)
        {
            Vector3 p = root.InverseTransformPoint(world), e = root.InverseTransformPoint(eye);
            float dz = p.z - e.z;
            if (Mathf.Abs(dz) > 1e-4f) p = e + (p - e) * (-e.z / dz);
            return new Vector2(p.x - r.xMin, r.yMax - p.y);
        }

        // ---- Input (from MLModelLayerInput) ----

        public void PointerMove(PointerEventData e)
        {
            if (dragging || !RayFrom(e, out Vector3 origin, out Vector3 direction)) return;
            Slab best = null; float bestDistance = float.MaxValue;
            foreach (var group in groups)
            {
                Test(group.slab, origin, direction, ref best, ref bestDistance);
                for (int i = 0; i < group.count; i++) Test(layers[group.first + i], origin, direction, ref best, ref bestDistance);
            }
            int g = best != null && best.layer < 0 ? best.group : -1, l = best != null ? best.layer : -1;
            if (l != hoverLayer)
            {
                if (hoverLayer >= 0 && hoverLayer != selectedLayer) SetTagText(hoverLayer, false);
                if (l >= 0) SetTagText(l, true);
            }
            hoverGroup = g; hoverLayer = l;
        }
        public void PointerExit()
        {
            if (hoverLayer >= 0 && hoverLayer != selectedLayer) SetTagText(hoverLayer, false);
            hoverGroup = hoverLayer = -1;
        }
        public void CalloutHover(bool inside) { overCallout = inside; if (inside) lastHover = Time.unscaledTime; }
        public void PointerDown() { dragMoved = false; }
        public void Click(PointerEventData e)
        {
            if (dragMoved) return;
            PointerMove(e);
            lastInput = Time.unscaledTime;
            if (hoverGroup >= 0) { SetOpen(hoverGroup, true); return; }
            int previous = selectedLayer;
            selectedLayer = hoverLayer == selectedLayer ? -1 : hoverLayer;
            if (previous >= 0 && previous != hoverLayer) SetTagText(previous, false);
            if (selectedLayer >= 0) SetTagText(selectedLayer, true);
        }
        public void BeginDrag(PointerEventData e) { dragging = PanelPoint(e, out dragLast); hoverGroup = -1; }
        public void Drag(PointerEventData e)
        {
            if (!dragging || !PanelPoint(e, out Vector2 point)) return;
            Vector2 d = point - dragLast; dragLast = point;
            if (d.sqrMagnitude > 0f) dragMoved = true;
            yaw -= d.x * 0.5f;
            pitch = Mathf.Clamp(pitch + d.y * 0.35f, -75f, 5f);
            lastInput = Time.unscaledTime;
        }
        public void EndDrag() { dragging = false; lastInput = Time.unscaledTime; }
        public void Scroll(PointerEventData e)
        {
            zoom = Mathf.Clamp(zoom * (1f + Mathf.Clamp(e.scrollDelta.y, -3f, 3f) * 0.08f), 0.6f, 1.7f);
            lastInput = Time.unscaledTime;
        }

        // The ray from the eye through the point the pointer hit on the panel, so you pick what you see.
        bool RayFrom(PointerEventData e, out Vector3 origin, out Vector3 direction)
        {
            var cam = Camera.main;
            origin = cam != null ? cam.transform.position : eye; direction = Vector3.forward;
            var hit = e.pointerCurrentRaycast;
            if (hit.gameObject == null) return false;
            direction = hit.worldPosition - origin;
            if (direction.sqrMagnitude < 1e-8f) return false;
            direction.Normalize();
            return true;
        }
        bool PanelPoint(PointerEventData e, out Vector2 point)
        {
            point = Vector2.zero;
            var hit = e.pointerCurrentRaycast;
            if (hit.gameObject == null) return false;
            point = root.InverseTransformPoint(hit.worldPosition);
            return true;
        }
        static void Test(Slab s, Vector3 origin, Vector3 direction, ref Slab best, ref float bestDistance)
        {
            if (s.vis < 0.5f) return;
            var toLocal = s.t.worldToLocalMatrix;
            Vector3 o = toLocal.MultiplyPoint3x4(origin), d = toLocal.MultiplyVector(direction);
            float near = float.MinValue, far = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(d[axis]) < 1e-9f) { if (o[axis] < -0.5f || o[axis] > 0.5f) return; continue; }
                float t1 = (-0.5f - o[axis]) / d[axis], t2 = (0.5f - o[axis]) / d[axis];
                if (t1 > t2) { float swap = t1; t1 = t2; t2 = swap; }
                near = Mathf.Max(near, t1); far = Mathf.Min(far, t2);
                if (near > far) return;
            }
            if (far < 0f) return;
            float distance = (s.t.localToWorldMatrix.MultiplyPoint3x4(o + d * Mathf.Max(near, 0f)) - origin).sqrMagnitude;
            if (distance < bestDistance) { bestDistance = distance; best = s; }
        }

        // ---- Look ----

        static bool IsAnchor(string type)
        {
            string t = (type ?? "").ToLowerInvariant();
            return !(t.Contains("norm") || t.Contains("activation") || t.Contains("relu") || t.Contains("pool") || t.Contains("dropout"));
        }
        public static string TypeName(string type)
        {
            string t = (type ?? "").ToLowerInvariant();
            if (t.Contains("conv")) return "Conv";
            if (t.Contains("pool")) return "Pool";
            if (t.Contains("linear") || t.Contains("dense")) return "Linear";
            if (t.Contains("norm")) return "Norm";
            if (t.Contains("reshape") || t.Contains("flatten")) return "Flatten";
            if (t.Contains("activation") || t.Contains("relu")) return "Activation";
            if (t.Contains("dropout")) return "Dropout";
            return string.IsNullOrEmpty(type) ? "Layer" : char.ToUpperInvariant(type[0]) + type.Substring(1);
        }
        public static Color TypeColor(string type)
        {
            string t = (type ?? "").ToLowerInvariant();
            if (t.Contains("conv")) return new Color32(0x4D, 0x8D, 0xFF, 0xFF);
            if (t.Contains("pool")) return new Color32(0x3D, 0xDC, 0x97, 0xFF);
            if (t.Contains("linear") || t.Contains("dense")) return new Color32(0xFF, 0xB5, 0x47, 0xFF);
            if (t.Contains("norm")) return new Color32(0xA7, 0x7B, 0xFF, 0xFF);
            if (t.Contains("reshape") || t.Contains("flatten")) return new Color32(0x9A, 0xA8, 0xBC, 0xFF);
            if (t.Contains("activation") || t.Contains("relu")) return new Color32(0x2F, 0xD9, 0xD0, 0xFF);
            return new Color32(0xFF, 0x6B, 0x9A, 0xFF);
        }
        static string Shape(int[] shape) => shape == null || shape.Length == 0 ? "?" : string.Join("×", shape);

        // Wide = big feature maps (or long vectors), deep = many channels.
        static Vector3 Footprint(int[] shape, float thickness)
        {
            float w = 90f, d = 40f;
            if (shape != null && shape.Length >= 3)
            {
                float area = Mathf.Max(1f, (float)shape[shape.Length - 2] * shape[shape.Length - 1]);
                w = 70f + 12f * Mathf.Log(area, 2f); d = 40f + 14f * Mathf.Log(Mathf.Max(1, shape[0]), 2f);
            }
            else if (shape != null && shape.Length == 2) { w = 70f + 10f * Mathf.Log(Mathf.Max(1, shape[0]), 2f); d = 40f + 12f * Mathf.Log(Mathf.Max(1, shape[1]), 2f); }
            else if (shape != null && shape.Length == 1) { w = 50f + 11f * Mathf.Log(Mathf.Max(1, shape[0]), 2f); d = 30f; }
            return new Vector3(Mathf.Clamp(w, 50f, 200f), thickness, Mathf.Clamp(d, 26f, 150f));
        }
        // The top face's cell grid: the feature map (columns) by channels (rows), or one row of cells for a vector.
        static Vector2 Grid(int[] shape)
        {
            if (shape == null || shape.Length == 0) return Vector2.one;
            if (shape.Length >= 3) return new Vector2(Mathf.Clamp(shape[shape.Length - 1], 2, 16), Mathf.Clamp(shape[0] / 8, 2, 16));
            int n = shape[shape.Length - 1];
            return new Vector2(n <= 16 ? n : Mathf.Clamp(Mathf.Log(n, 2f) * 2f, 4f, 16f), 1f);
        }

        void OnDestroy()
        {
            if (session != null) { session.Metrics -= OnMetrics; session.Reloaded -= OnReloaded; }
            if (ownSlab != null) Destroy(ownSlab);
            if (ownBeam != null) Destroy(ownBeam);
        }
    }
}
