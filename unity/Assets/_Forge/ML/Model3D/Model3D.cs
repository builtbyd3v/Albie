using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    [RequireComponent(typeof(ForgePanel))]
    public class Model3D : MonoBehaviour
    {
        public MLSession session;
        public Material blockMaterial;
        Transform[] blocks;
        Vector3[] scales;
        Material[] materials;
        Color[] colors;
        TMPro.TMP_Text detail;
        int highlighted = -1;
        MLLayer[] built;
        void Start()
        {
            session = MLSession.Resolve(session);
            Build();
            session.Metrics += OnMetrics; session.Reloaded += OnReloaded;
        }
        // The hub can send a different model (another repo, or graph_ready): rebuild only when the layers changed.
        void OnReloaded() { if (session.Data.layers != built) { StopAllCoroutines(); Build(); } }
        void Build()
        {
            var root = GetComponent<ForgePanel>().Content;
            for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
            if (materials != null) foreach (var old in materials) Destroy(old);
            built = session.Data.layers; highlighted = -1;
            int count = session.Data.layers.Length; blocks = new Transform[count]; scales = new Vector3[count]; materials = new Material[count]; colors = new Color[count];
            float step = Mathf.Min(62, (root.rect.height - 30) / Mathf.Max(1, count));
            MLUI.Text(root, "Layer blocks · pulses follow metrics ticks", 8, 0, 450, 26, 14);
            detail = MLUI.Text(root, "Hover or select a layer for details", 8, 25, 450, 18, 11);
            for (int i = 0; i < count; i++)
            {
                var layer = session.Data.layers[i];
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube); Destroy(go.GetComponent<Collider>()); go.name = layer.name;
                go.transform.SetParent(root, false); go.transform.localPosition = new Vector3(root.rect.xMin+100,root.rect.yMax-55-i*step,-35); go.transform.localRotation = Quaternion.Euler(12,25,0);
                float size = 25;
                foreach (int dimension in layer.out_shape) size += Mathf.Log(Mathf.Max(1,dimension)+1)*3;
                go.transform.localScale = new Vector3(size,22,30); blocks[i] = go.transform; scales[i] = go.transform.localScale;
                var material = blockMaterial != null ? new Material(blockMaterial) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                colors[i] = TypeColor(layer.type); material.SetColor("_BaseColor",colors[i]); materials[i] = material; go.GetComponent<Renderer>().sharedMaterial = material;
                float y = 30+i*step;
                MLUI.Text(root, string.Join("×",layer.in_shape), 190, y, 260, 17, 13);
                MLUI.Text(root, layer.name+" · "+layer.type, 190, y+17, 260, 17, 12).color = colors[i];
                MLUI.Text(root, string.Join("×",layer.out_shape), 190, y+34, 260, 19, 15);
                var hit = MLUI.Rect("Layer hit " + i, root, 180, y, 280, 56).gameObject;
                var image = hit.AddComponent<Image>(); image.color = new Color(0, 0, 0, .001f);
                var input = hit.AddComponent<MLModelLayerInput>(); input.owner = this; input.layerIndex = i;
            }
        }
        public void HighlightLayer(int index)
        {
            if (index < 0 || index >= session.Data.layers.Length) { ClearHighlight(); return; }
            highlighted = index;
            var layer = session.Data.layers[index];
            detail.text = $"{layer.name} · {layer.type}    {string.Join("×", layer.in_shape)} → {string.Join("×", layer.out_shape)}";
            for (int i = 0; i < blocks.Length; i++) materials[i].SetColor("_BaseColor", i == index ? Color.white : colors[i]);
        }
        public void ClearHighlight()
        {
            highlighted = -1;
            if (detail != null) detail.text = "Hover or select a layer for details";
            for (int i = 0; materials != null && i < materials.Length; i++) materials[i].SetColor("_BaseColor", colors[i]);
        }
        public static Color TypeColor(string type)
        {
            type = type.ToLowerInvariant();
            if (type.Contains("conv")) return new Color(.3f,.65f,1);
            if (type.Contains("pool")) return new Color(.3f,.85f,.65f);
            if (type.Contains("linear")) return new Color(1,.65f,.25f);
            if (type.Contains("norm")) return new Color(.7f,.55f,1);
            if (type.Contains("reshape")) return new Color(.65f,.72f,.8f);
            return new Color(1,.4f,.6f);
        }
        void OnMetrics(MLMetrics tick) { StopAllCoroutines(); for (int i=0;i<blocks.Length;i++) { blocks[i].localScale=scales[i]; materials[i].SetColor("_BaseColor",colors[i]); } highlighted = -1; StartCoroutine(Pulse()); }
        IEnumerator Pulse()
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                float elapsed = 0;
                while (elapsed < .12f) { elapsed += Time.deltaTime; float p = Mathf.Sin(Mathf.Clamp01(elapsed/.12f)*Mathf.PI); blocks[i].localScale = scales[i]*(1+p*.18f); materials[i].SetColor("_BaseColor",Color.Lerp(colors[i],Color.white,p)); yield return null; }
                blocks[i].localScale = scales[i]; materials[i].SetColor("_BaseColor",colors[i]);
            }
        }
        void OnDestroy() { if(session != null) { session.Metrics -= OnMetrics; session.Reloaded -= OnReloaded; } if(materials != null) foreach(var material in materials) Destroy(material); }
    }
}
