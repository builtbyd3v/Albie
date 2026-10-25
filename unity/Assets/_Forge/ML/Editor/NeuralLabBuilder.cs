using Forge.ML;
using UnityEditor;
using UnityEngine;

/// <summary>Builds the Neural Net Lab prefabs (the Lab, its three learning panels, and the 3D network volume for the
/// desk) into ML/Prefabs and Resources/MLPanels, without rebuilding the other ML panels.</summary>
public static class NeuralLabBuilder
{
    const string Prefab3D = "Assets/_Forge/ML/Prefabs/NeuralNet3D.prefab", Resource3D = "Assets/_Forge/ML/Resources/MLPanels/NeuralNet3D.prefab";
    const string NodeMat = "Assets/_Forge/ML/Prefabs/NeuralNode.mat", EdgeMat = "Assets/_Forge/ML/Prefabs/NeuralEdge.mat";

    [MenuItem("Forge/ML/Build Neural Lab Prefab")]
    public static void Build()
    {
        Panel<NeuralLab>("NeuralLab", "ml-neural-lab", "Neural Net Lab", new Vector2(.90f, .62f));
        Panel<NeuralLearn>("NeuralLearn", "ml-neural-learn", "How it learns", new Vector2(.56f, .62f));
        Panel<NeuralNeurons>("NeuralNeurons", "ml-neural-neurons", "What each neuron sees", new Vector2(.60f, .62f));
        Panel<NeuralStory>("NeuralStory", "ml-neural-story", "Training story", new Vector2(.90f, .40f));
        Build3D();
        AssetDatabase.SaveAssets();
        Debug.Log("[ML] Built NeuralLab, NeuralLearn, NeuralNeurons, NeuralStory and NeuralNet3D prefabs (ML/Prefabs and Resources/MLPanels).");
    }

    static void Panel<T>(string name, string id, string title, Vector2 size) where T : Component
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(ForgePrefabBuilder.PanelPath);
        if (source == null) throw new System.InvalidOperationException("Core ForgePanel prefab is required. Ask Core to build its prefabs first.");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(source);
        go.name = name;
        var panel = go.GetComponent<ForgePanel>(); panel.panelId = id; panel.Title = title; panel.SizeMeters = size;
        for (int i = panel.Content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(panel.Content.GetChild(i).gameObject);
        go.AddComponent<T>();
        PrefabUtility.SaveAsPrefabAsset(go, $"Assets/_Forge/ML/Prefabs/{name}.prefab");
        PrefabUtility.SaveAsPrefabAsset(go, $"Assets/_Forge/ML/Resources/MLPanels/{name}.prefab");
        Object.DestroyImmediate(go);
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
