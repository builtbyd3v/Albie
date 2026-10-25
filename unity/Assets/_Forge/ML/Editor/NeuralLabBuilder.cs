using Forge.ML;
using UnityEditor;
using UnityEngine;

/// <summary>Builds the Neural Net Lab prefabs (the panel, and the 3D network volume for the desk) into ML/Prefabs and
/// Resources/MLPanels, without rebuilding the other ML panels.</summary>
public static class NeuralLabBuilder
{
    const string Prefab = "Assets/_Forge/ML/Prefabs/NeuralLab.prefab", Resource = "Assets/_Forge/ML/Resources/MLPanels/NeuralLab.prefab";
    const string Prefab3D = "Assets/_Forge/ML/Prefabs/NeuralNet3D.prefab", Resource3D = "Assets/_Forge/ML/Resources/MLPanels/NeuralNet3D.prefab";
    const string NodeMat = "Assets/_Forge/ML/Prefabs/NeuralNode.mat", EdgeMat = "Assets/_Forge/ML/Prefabs/NeuralEdge.mat";

    [MenuItem("Forge/ML/Build Neural Lab Prefab")]
    public static void Build()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath);
        if (source == null) throw new System.InvalidOperationException("Core ForgePanel prefab is required. Ask Core to build its prefabs first.");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
        go.name = "NeuralLab";
        var panel = go.GetComponent<ForgePanel>(); panel.panelId = "ml-neural-lab"; panel.Title = "Neural Net Lab"; panel.SizeMeters = new Vector2(.90f, .62f);
        for (int i = panel.Content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(panel.Content.GetChild(i).gameObject);
        go.AddComponent<NeuralLab>();
        PrefabUtility.SaveAsPrefabAsset(go, Prefab);
        PrefabUtility.SaveAsPrefabAsset(go, Resource);
        Object.DestroyImmediate(go);
        Build3D();
        AssetDatabase.SaveAssets();
        Debug.Log("[ML] Built NeuralLab.prefab and NeuralNet3D.prefab (ML/Prefabs and Resources/MLPanels).");
    }

    // The network in 3D on the desk: a ForgeVolume variant with NeuralNet3D and its two materials (saved as assets,
    // so the shaders ship in the Quest build).
    static void Build3D()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.VolumePath);
        if (source == null) throw new System.InvalidOperationException("Core ForgeVolume prefab is required.");
        var node = Material(NodeMat, "Universal Render Pipeline/Unlit");
        var edge = Material(EdgeMat, "Sprites/Default");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
        go.name = "NeuralNet3D";
        var volume = go.GetComponent<ForgeVolume>();
        volume.volumeId = "ml-neural-3d"; volume.size = new Vector3(0.6f, 0.4f, 0.4f);
        volume.turntable = false;  // NeuralNet3D sways it instead, so the layers stay left to right
        var net = go.AddComponent<NeuralNet3D>(); net.nodeMaterial = node; net.edgeMaterial = edge;
        PrefabUtility.SaveAsPrefabAsset(go, Prefab3D);
        PrefabUtility.SaveAsPrefabAsset(go, Resource3D);
        Object.DestroyImmediate(go);
    }

    static Material Material(string path, string shader)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        var material = new Material(Shader.Find(shader));
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
