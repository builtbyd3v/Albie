using UnityEngine;

/// <summary>Tags a GLB node with its CAD object ID (metadata.json `id`, e.g. FIN_2).</summary>
[DisallowMultipleComponent]
public class CadPart : MonoBehaviour
{
    public string id;
}
