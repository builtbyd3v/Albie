using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Puts the desk extras into any scene with a <see cref="Workspace"/> that doesn't have them yet: a Main built by a
/// builder without them (another branch's), a teammate's scene. Adds <see cref="WorkspaceExtras"/> to the Workspace
/// and Resources/DeskExtras under it. Main's builder adds them too; this keeps them working whoever built the scene,
/// the way ForgeXRStereoFix installs itself.
/// </summary>
public static class DeskBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Ensure();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

    static void Ensure()
    {
        if (Object.FindFirstObjectByType<DeskExtras>(FindObjectsInactive.Include) != null) return;
        var workspace = Object.FindFirstObjectByType<Workspace>();
        if (workspace == null) return;
        var prefab = Resources.Load<GameObject>("DeskExtras");
        if (prefab == null)
        {
            Debug.LogWarning("[DeskBootstrap] Resources/DeskExtras missing (Forge > Desk > Build Desk Prefabs); no desk extras.");
            return;
        }

        // Both components hook up in OnEnable, so they're wired before they're enabled.
        var host = workspace.GetComponent<WorkspaceExtras>();
        if (host == null)
        {
            host = workspace.gameObject.AddComponent<WorkspaceExtras>();
            host.enabled = false;
            host.workspace = workspace;
            host.enabled = true;
        }
        var holder = new GameObject("DeskExtras (setup)");
        holder.SetActive(false);
        var extras = Object.Instantiate(prefab, holder.transform);
        extras.name = prefab.name;
        extras.GetComponent<DeskExtras>().host = host;
        extras.transform.SetParent(workspace.transform, false);
        Object.Destroy(holder);
        Debug.Log("[DeskBootstrap] Desk extras added to " + workspace.gameObject.scene.name);
    }
}
