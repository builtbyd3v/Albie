using System.Collections;
using UnityEngine;

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
        void Start()
        {
            session = MLSession.Resolve(session); var root = GetComponent<ForgePanel>().Content;
            int count = session.Data.layers.Length; blocks = new Transform[count]; scales = new Vector3[count]; materials = new Material[count]; colors = new Color[count];
            float step = Mathf.Min(62, (root.rect.height - 30) / Mathf.Max(1, count));
            MLUI.Text(root, "Layer blocks · pulses follow metrics ticks", 8, 0, 450, 26, 14);
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
                MLUI.Text(root, string.Join("×",layer.in_shape), 190, y, 260, 12, 9);
                MLUI.Text(root, layer.name+" · "+layer.type, 190, y+12, 260, 15, 12).color = colors[i];
                MLUI.Text(root, string.Join("×",layer.out_shape), 190, y+27, 260, 12, 9);
            }
            session.Metrics += OnMetrics;
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
        void OnMetrics(MLMetrics tick) { StopAllCoroutines(); for (int i=0;i<blocks.Length;i++) { blocks[i].localScale=scales[i]; materials[i].SetColor("_BaseColor",colors[i]); } StartCoroutine(Pulse()); }
        IEnumerator Pulse()
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                float elapsed = 0;
                while (elapsed < .12f) { elapsed += Time.deltaTime; float p = Mathf.Sin(Mathf.Clamp01(elapsed/.12f)*Mathf.PI); blocks[i].localScale = scales[i]*(1+p*.18f); materials[i].SetColor("_BaseColor",Color.Lerp(colors[i],Color.white,p)); yield return null; }
                blocks[i].localScale = scales[i]; materials[i].SetColor("_BaseColor",colors[i]);
            }
        }
        void OnDestroy() { if(session != null) session.Metrics -= OnMetrics; if(materials != null) foreach(var material in materials) Destroy(material); }
    }
}
