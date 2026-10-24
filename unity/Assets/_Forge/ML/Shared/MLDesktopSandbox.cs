#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;

namespace Forge.ML
{
    // Desktop-only sandbox: replace the in-memory settings instance before XR's
    // AfterAssembliesLoaded initializer. The saved XR settings asset is untouched.
    public static class MLDesktopSandbox
    {
        static XRGeneralSettings original, transient;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void SkipXRForSandbox()
        {
            if (!UnityEditor.SessionState.GetBool("Forge.ML.Smoke", false) && SceneManager.GetActiveScene().path != "Assets/_Forge/ML/MLSandbox.unity") return;
            var settings = XRGeneralSettings.Instance;
            if (settings == null) return;
            original = settings;
            var desktop = Object.Instantiate(settings);
            transient = desktop;
            desktop.name = "ML sandbox transient XR settings";
            desktop.hideFlags = HideFlags.HideAndDontSave;
            desktop.InitManagerOnStart = false;
            XRGeneralSettings.Instance = desktop;
            UnityEditor.EditorApplication.playModeStateChanged -= Restore;
            UnityEditor.EditorApplication.playModeStateChanged += Restore;
        }
        static void Restore(UnityEditor.PlayModeStateChange state)
        {
            if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
            UnityEditor.EditorApplication.playModeStateChanged -= Restore;
            if (original != null) XRGeneralSettings.Instance = original;
            if (transient != null) Object.DestroyImmediate(transient);
            original = transient = null;
        }
    }
}
#endif
