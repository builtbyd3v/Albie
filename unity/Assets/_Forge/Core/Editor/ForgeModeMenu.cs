using UnityEditor;
using UnityEditor.XR.Management;

/// <summary>
/// Forge > Desktop Mode: play with the desktop rig even when the Meta XR Simulator is active.
/// For each Play session it stops XR from starting (shutting XR down after it starts would also unload MRUK),
/// then restores the setting when Play ends, so the project settings never change on disk.
/// </summary>
[InitializeOnLoad]
public static class ForgeModeMenu
{
    const string MenuPath = "Forge/Desktop Mode (skip XR)";
    const string RestoreKey = "Forge.RestoreXRInitOnStart";

    static ForgeModeMenu() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

    [MenuItem(MenuPath, false, 100)]
    static void Toggle() => EditorPrefs.SetBool(RigSelector.ForceDesktopPref, !EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));

    [MenuItem(MenuPath, true)]
    static bool Validate()
    {
        Menu.SetChecked(MenuPath, EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
        return !EditorApplication.isPlaying;
    }

    static void OnPlayModeChanged(PlayModeStateChange change)
    {
        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (settings == null) return;

        if (change == PlayModeStateChange.ExitingEditMode
            && EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false) && settings.InitManagerOnStart)
        {
            settings.InitManagerOnStart = false;
            SessionState.SetBool(RestoreKey, true);  // survives the domain reload into Play mode
        }
        else if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(RestoreKey, false))
        {
            settings.InitManagerOnStart = true;
            SessionState.EraseBool(RestoreKey);
        }
    }
}
