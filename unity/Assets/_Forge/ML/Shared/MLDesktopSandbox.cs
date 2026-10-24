#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEditor;

namespace Forge.ML
{
    // Desktop-only sandbox: replace the in-memory settings instance before XR's
    // AfterAssembliesLoaded initializer. The saved XR settings asset is untouched.
    public static class MLDesktopSandbox
    {
        const string SmokeKey = "Forge.ML.Smoke";
        const string SimulatorKey = "Forge.ML.UseXrSimulator";
        const string SimulatorMenu = "Forge/ML/Use OpenXR Simulator";
        static XRGeneralSettings original, transient;

        [MenuItem(SimulatorMenu)]
        static void ToggleSimulator()
        {
            bool enabled = !SessionState.GetBool(SimulatorKey, false);
            SessionState.SetBool(SimulatorKey, enabled);
            Menu.SetChecked(SimulatorMenu, enabled);
            Debug.Log(enabled
                ? "[ML] OpenXR Simulator enabled for MLSandbox. Restart Play mode to connect."
                : "[ML] Desktop sandbox mode enabled. Restart Play mode to use the local camera.");
        }

        [MenuItem(SimulatorMenu, true)]
        static bool ValidateSimulatorMenu()
        {
            Menu.SetChecked(SimulatorMenu, SessionState.GetBool(SimulatorKey, false));
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void SkipXRForSandbox()
        {
            if (SceneManager.GetActiveScene().path != "Assets/_Forge/ML/MLSandbox.unity") return;
            if (!SessionState.GetBool(SmokeKey, false) && SessionState.GetBool(SimulatorKey, false)) return;
            var settings = XRGeneralSettings.Instance;
            if (settings == null) return;
            original = settings;
            var desktop = Object.Instantiate(settings);
            transient = desktop;
            desktop.name = "ML sandbox transient XR settings";
            desktop.hideFlags = HideFlags.HideAndDontSave;
            desktop.InitManagerOnStart = false;
            XRGeneralSettings.Instance = desktop;
            EditorApplication.playModeStateChanged -= Restore;
            EditorApplication.playModeStateChanged += Restore;
        }
        static void Restore(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode) return;
            EditorApplication.playModeStateChanged -= Restore;
            if (original != null) XRGeneralSettings.Instance = original;
            if (transient != null) Object.DestroyImmediate(transient);
            original = transient = null;
        }
    }
}
#endif
