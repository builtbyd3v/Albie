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
    public class DataStack : MonoBehaviour, IScrollHandler, IBeginDragHandler, IDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public MLSession session;
        public Material atlasMaterial;
        readonly Matrix4x4[] matrices = new Matrix4x4[64];
        readonly Vector4[] uvs = new Vector4[64];
        Material material;
        MaterialPropertyBlock properties;
        Mesh quad;
        RectTransform root, preview;
        RawImage image;
        TMPro.TMP_Text label, cursor;
        float position, target;
        int focus, selected = -1;
        bool hovered;
        Vector2 lastDrag;
        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content;
            // Transparent graphic ensures the panel receives scroll and drag from all uGUI input modules.
            var hit = root.gameObject.AddComponent<Image>(); hit.color = new Color(0,0,0,.001f);
            var relay = root.gameObject.AddComponent<MLStackInput>(); relay.owner = this;
            material = atlasMaterial != null ? new Material(atlasMaterial) : new Material(Shader.Find("Forge/ML/InstancedAtlas"));
            material.enableInstancing = true; material.mainTexture = session.Atlas;
            properties = new MaterialPropertyBlock();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad); quad = temp.GetComponent<MeshFilter>().sharedMesh; Destroy(temp);
            MLUI.Text(root, "Scroll / swipe / hover + thumbstick", 8, 4, 400, 28, 14);
            cursor = MLUI.Text(root, "", 8, 34, 400, 30);
            MLUI.Button(root, "Previous", 8, 290, 90, 30, () => Scrub(-1));
            MLUI.Button(root, "Inspect", 105, 290, 90, 30, Select);
            MLUI.Button(root, "Next", 202, 290, 90, 30, () => Scrub(1));
            preview = MLUI.Rect("Selected card", root, 70, 75, 240, 205);
            MLUI.Fill(preview, 0, 0, 240, 205, MLUI.Ink);
            image = MLUI.Photo(preview, session, 0, 8, 8, 150, 150);
            label = MLUI.Text(preview, "", 8, 160, 230, 32, 14);
            MLUI.Button(preview, "Put back", 161, 8, 74, 32, PutBack); preview.gameObject.SetActive(false);
            session.SamplesChanged += Refresh; Refresh();
        }
        public void Scrub(float delta) { if (session != null) target = Mathf.Clamp(target + delta, 0, session.Data.samples.Length - 1); }
        void Refresh()
        {
            if (cursor == null) return;
            var sample = session.Data.samples[focus];
            cursor.text = $"{focus+1}/{session.Data.samples.Length}   {sample.id}   {(sample.removed ? "removed" : session.Data.classes[sample.label_idx])}";
            if (selected >= 0) label.text = session.Data.classes[session.Data.samples[selected].label_idx] + " · " + session.Data.samples[selected].id;
        }
        void Update()
        {
            if (material == null) return;
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
                Vector3 p = new Vector3(root.rect.xMin + 190 + d * 12, root.rect.yMax - 165 - Mathf.Abs(d)*2, -5 - (21-Mathf.Abs(d))*2);
                matrices[count] = root.localToWorldMatrix * Matrix4x4.TRS(p, Quaternion.Euler(0,d*3,d*-2), new Vector3(132,132,1));
                Rect uv = session.UV(i); uvs[count] = new Vector4(uv.x,uv.y,uv.width,uv.height); count++;
            }
            properties.SetVectorArray("_UVRect", uvs);
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
        void OnDestroy() { if (session != null) session.SamplesChanged -= Refresh; if (material != null) Destroy(material); }
    }
}
