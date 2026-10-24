using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class DataStack : MonoBehaviour, IScrollHandler, IBeginDragHandler, IDragHandler, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public MLSession session;
        public Material atlasMaterial;
        readonly Matrix4x4[] matrices = new Matrix4x4[64];
        readonly Vector4[] uvs = new Vector4[64];
        readonly Vector4[] colors = new Vector4[64];
        Material material;
        MaterialPropertyBlock properties;
        Mesh quad;
        RectTransform root, preview;
        RawImage image;
        TMPro.TMP_Text label, cursor, metadata;
        float position, target;
        int focus, selected = -1;
        bool hovered;
        Vector2 lastDrag;
        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content;
            GetComponent<ForgePanel>().SizeMeters = new Vector2(.6f, .42f);
            // Transparent graphic ensures the panel receives scroll and drag from all uGUI input modules.
            var hit = root.gameObject.AddComponent<Image>(); hit.color = new Color(0,0,0,.001f);
            var relay = root.gameObject.AddComponent<MLStackInput>(); relay.owner = this;
            material = atlasMaterial != null ? new Material(atlasMaterial) : new Material(Shader.Find("Forge/ML/InstancedAtlas"));
            material.enableInstancing = true; material.mainTexture = session.Atlas;
            properties = new MaterialPropertyBlock();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad); quad = temp.GetComponent<MeshFilter>().sharedMesh; Destroy(temp);
            MLUI.Caps(root, "Scroll, swipe or thumbstick to scrub  ·  click a card to pick it", 4, 8, 420, 14, 9.5f);
            cursor = MLUI.Text(root, "", 4, 26, 420, 26, 15); cursor.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            MLUI.Button(root, "Previous", 8, 290, 90, 30, () => Scrub(-1));
            MLUI.Button(root, "Inspect", 105, 290, 90, 30, Select);
            MLUI.Button(root, "Next", 202, 290, 90, 30, () => Scrub(1));
            preview = MLUI.Rect("Picked image metadata", root, 300, 42, 285, 300);
            MLUI.Surface(preview, 0, 0, 285, 300, new Color(0.11f, 0.12f, 0.15f, 0.97f), 16, 0.35f);
            MLUI.Caps(preview, "Picked image", 14, 12, 200);
            image = MLUI.Photo(preview, session, 0, 14, 30, 128, 128);
            label = MLUI.Text(preview, "", 154, 30, 120, 40, 14); label.fontStyle = TMPro.FontStyles.Bold;
            metadata = MLUI.Text(preview, "", 14, 168, 262, 96, 10.5f); metadata.color = MLUI.Sub; metadata.lineSpacing = 8;
            MLUI.Button(preview, "Put back  (X / B)", 8, 270, 145, 26, PutBack); preview.gameObject.SetActive(false);
            session.SamplesChanged += Refresh; session.Reloaded += OnReloaded; Refresh();
        }
        void OnReloaded()
        {
            material.mainTexture = session.Atlas; image.texture = session.Atlas;
            int last = session.Data.samples.Length - 1;
            target = Mathf.Clamp(target, 0, last); position = Mathf.Clamp(position, 0, last); focus = Mathf.Clamp(focus, 0, last);
            if (selected > last) { selected = -1; preview.gameObject.SetActive(false); }
            else if (selected >= 0) image.uvRect = session.UV(selected);
        }
        public void Scrub(float delta) { if (session != null) target = Mathf.Clamp(target + delta, 0, session.Data.samples.Length - 1); }
        void Refresh()
        {
            if (cursor == null) return;
            var sample = session.Data.samples[focus];
            cursor.text = $"<b>{focus+1:N0}</b><color=#FFFFFF60> / {session.Data.samples.Length:N0}</color>   <color=#FFFFFFA0>{sample.id}</color>   <b>{(sample.removed ? "removed" : session.Data.classes[sample.label_idx])}</b>";
            if (selected >= 0 && selected < session.Data.samples.Length)
            {
                var picked = session.Data.samples[selected];
                int pred = Mathf.Clamp(picked.pred_idx, 0, session.Data.classes.Length - 1);
                float confidence = picked.probs != null && pred < picked.probs.Length ? picked.probs[pred] : 0;
                label.text = string.IsNullOrEmpty(picked.name) ? picked.id : picked.name;
                string path = string.IsNullOrEmpty(picked.path) ? "Not available" : picked.path;
                string bytes = picked.file_size > 0 ? $"{picked.file_size:N0} bytes" : "Not available";
                metadata.text = $"Path: {path}\nClass: {session.Data.classes[picked.label_idx]} · {picked.split ?? "val"}\nImage: {picked.width} × {picked.height} · {picked.channels} channels\nFile size: {bytes}\nPrediction: {session.Data.classes[pred]} · {confidence:P1}";
            }
        }
        void Update()
        {
            if (material == null) return;
            // Unity can preserve scene objects when Enter Play Mode skips a domain reload.
            // Start normally creates this, but lazily restore it if the editor kept an older instance.
            if (properties == null) properties = new MaterialPropertyBlock();
#if ENABLE_INPUT_SYSTEM
            if (hovered && Gamepad.current != null) Scrub(Gamepad.current.leftStick.ReadValue().y * Time.deltaTime * 10);
            if (hovered && UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis)) Scrub(axis.y * Time.deltaTime * 10);
            }
#endif
            position = Mathf.Lerp(position, target, 1 - Mathf.Exp(-10 * Time.deltaTime));
            int next = Mathf.RoundToInt(position); if (next != focus) { focus = next; Refresh(); }
            int count = 0, first = Mathf.Max(0, focus - 20), last = Mathf.Min(session.Data.samples.Length, focus + 21);
            for (int i = first; i < last; i++)
            {
                if (session.Data.samples[i].removed || i == selected) continue;
                float d = i-position;
                Vector3 p = new Vector3(root.rect.xMin + 140 + d * 12, root.rect.yMax - 165 - Mathf.Abs(d)*2, -5 - (21-Mathf.Abs(d))*2);
                matrices[count] = root.localToWorldMatrix * Matrix4x4.TRS(p, Quaternion.Euler(0,d*3,d*-2), new Vector3(132,132,1));
                Rect uv = session.UV(i); uvs[count] = new Vector4(uv.x,uv.y,uv.width,uv.height); count++;
            }
            properties.SetVectorArray("_UVRect", uvs);
            for (int i = 0; i < count; i++) colors[i] = Vector4.one;
            properties.SetVectorArray("_Color", colors);
            if (count > 0 && SystemInfo.supportsInstancing) Graphics.DrawMeshInstanced(quad, 0, material, matrices, count, properties, ShadowCastingMode.Off, false, gameObject.layer);
        }
        public void Select()
        {
            if (session.Data.samples[focus].removed) return;
            StopAllCoroutines(); selected = focus; image.uvRect = session.UV(selected); preview.gameObject.SetActive(true); Refresh();
            StartCoroutine(ForgeTween.Run(.3f, 0, t => { preview.localScale = Vector3.one * Mathf.Lerp(.55f,1,ForgeTween.EaseOutCubic(t)); preview.anchoredPosition3D = new Vector3(70, -75, Mathf.Lerp(-30,-90,t)); }));
        }
        public void PutBack()
        {
            StopAllCoroutines(); StartCoroutine(ForgeTween.Run(.2f, 0, t => preview.localScale = Vector3.one * Mathf.Lerp(1,.55f,t), () => { selected = -1; preview.gameObject.SetActive(false); }));
        }
        public void OnScroll(PointerEventData e) => Scrub(-e.scrollDelta.y);
        public void OnBeginDrag(PointerEventData e) { lastDrag = e.position; }
        public void OnDrag(PointerEventData e) { Scrub((e.position.x-lastDrag.x)/30); lastDrag = e.position; }
        public void OnPointerEnter(PointerEventData e) => hovered = true;
        public void OnPointerExit(PointerEventData e) => hovered = false;
        public void OnPointerClick(PointerEventData e)
        {
            if (selected >= 0) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, e.position, e.pressEventCamera, out Vector2 local)) return;
            float stackCenter = root.rect.xMin + 140;
            int index = Mathf.Clamp(Mathf.RoundToInt(position + (local.x - stackCenter) / 12f), 0, session.Data.samples.Length - 1);
            target = focus = index; position = index; Refresh(); Select();
        }
        void UpdateReturnInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.xKey.wasPressedThisFrame && selected >= 0) PutBack();
            if (UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool pressed) && pressed && selected >= 0) PutBack();
            }
#else
            if (Input.GetKeyDown(KeyCode.X) && selected >= 0) PutBack();
#endif
        }
        void LateUpdate() => UpdateReturnInput();
        void OnDestroy() { if (session != null) session.SamplesChanged -= Refresh; if (material != null) Destroy(material); }
    }
}
