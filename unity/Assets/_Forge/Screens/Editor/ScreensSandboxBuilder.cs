using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Forge > Build Screens Sandbox: (re)builds Assets/_Forge/Screens/ScreensSandbox.unity and the
/// ScreenPanel prefab (quad + ScreenReceiver) that Main uses. A plain camera looks at the panel,
/// for testing the stream without XR.
/// Safe to run again; it overwrites the scene.
/// </summary>
public static class ScreensSandboxBuilder
{
    const string ScenePath = "Assets/_Forge/Screens/ScreensSandbox.unity";
    const string MaterialPath = "Assets/_Forge/Screens/ScreenPanel.mat";
    const string PrefabPath = "Assets/_Forge/Screens/ScreenPanel.prefab";

    [MenuItem("Forge/Build Screens Sandbox")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cam = new GameObject("Main Camera").AddComponent<Camera>();
        cam.tag = "MainCamera";
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f);

        // Unlit, so the screen shows its true colors regardless of lighting
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(material, MaterialPath);
        }

        // About a 24" monitor, 1 m away (section 8.2: VS Code readable at 1 m)
        var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
        panel.name = "ScreenPanel";
        Object.DestroyImmediate(panel.GetComponent<Collider>());
        panel.transform.position = new Vector3(0f, 0f, 1f);
        panel.transform.localScale = new Vector3(0.53f, 0.33f, 1f);
        panel.GetComponent<Renderer>().sharedMaterial = material;
        panel.AddComponent<ScreenReceiver>();
        PrefabUtility.SaveAsPrefabAssetAndConnect(panel, PrefabPath, InteractionMode.AutomatedAction);

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[ScreensSandboxBuilder] Built " + ScenePath + ". Run signaling.py, share a window from index.html, press Play.");
    }
}
