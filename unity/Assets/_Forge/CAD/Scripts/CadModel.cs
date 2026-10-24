using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CADVision;
using GLTFast;
using UnityEngine;

/// <summary>
/// One loaded version of the part: the glTFast scene plus CAD object ID -> GameObject, from the
/// GLB + metadata.json pair the hub exports. Owns the glTFast meshes/materials until disposed.
/// </summary>
public sealed class CadModel : IDisposable
{
    public int Version { get; private set; }
    public GameObject Root { get; private set; }
    public readonly Dictionary<string, GameObject> objects = new Dictionary<string, GameObject>();
    public readonly List<string> partIds = new List<string>();  // leaf parts (have meshes)

    GltfImport import;

    public static async Task<CadModel> LoadAsync(int version, byte[] glb, string metadataJson, Transform parent,
        CancellationToken token)
    {
        var metadata = new CadMetadata(metadataJson);  // cad-vision's validation: IDs, node indices, tree
        var root = new GameObject($"CAD v{version}");
        root.transform.SetParent(parent, false);
        root.SetActive(false);
        var import = new GltfImport();
        try
        {
            var settings = new ImportSettings { NodeNameMethod = NameImportMethod.Original };
            if (!await import.Load(glb, importSettings: settings, cancellationToken: token))
                throw new InvalidDataException("glTFast could not load the GLB (see the Console).");
            var instantiator = new NodeIndexInstantiator(import, root.transform);
            if (!await import.InstantiateMainSceneAsync(instantiator, token))
                throw new InvalidDataException("glTFast could not instantiate the GLB scene.");
            token.ThrowIfCancellationRequested();

            var model = new CadModel { Version = version, Root = root, import = import };
            foreach (var pair in metadata.NodeIndices)
            {
                if (!instantiator.TryGetNode(pair.Value, out var node))
                    throw new InvalidDataException($"{pair.Key}: GLB node {pair.Value} was not instantiated.");
                node.AddComponent<CadPart>().id = pair.Key;
                model.objects.Add(pair.Key, node);
                if ((string)metadata.GetObject(pair.Key)["type"] == "part") model.partIds.Add(pair.Key);
            }
            root.SetActive(true);
            return model;
        }
        catch
        {
            import.Dispose();
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }

    /// <summary>Renderers under one CAD object (a part or a whole assembly).</summary>
    public Renderer[] Renderers(string id) =>
        objects.TryGetValue(id, out var go) ? go.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();

    public void Dispose()
    {
        if (Root != null) UnityEngine.Object.Destroy(Root);
        Root = null;
        import?.Dispose();
        import = null;
    }

    /// <summary>glTFast instantiator that exposes GLB node index -> GameObject (idea from cad-vision's loader).</summary>
    sealed class NodeIndexInstantiator : GameObjectInstantiator
    {
        public NodeIndexInstantiator(IGltfReadable gltf, Transform parent)
            : base(gltf, parent, settings: new InstantiationSettings { Mask = ComponentType.Mesh }) { }

        public bool TryGetNode(int index, out GameObject node) => m_Nodes.TryGetValue((uint)index, out node);
    }
}
