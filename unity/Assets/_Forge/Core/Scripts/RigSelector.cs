using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.XR.Management;

/// <summary>
/// Picks the player when Play starts. An XR runtime is running (headset or Meta XR Simulator): the Meta camera rig.
/// Otherwise: <see cref="DesktopRig"/> (WASD + mouse), with the room loaded from a JSON copy of the simulator's
/// living room and drawn as a mesh, since there's no passthrough. Same scene, same desk flow, same panels.
/// Editor: Forge > Desktop Mode gives the desktop rig even with the simulator on (XR isn't started that session).
/// </summary>
[DefaultExecutionOrder(-1000)]
public class RigSelector : MonoBehaviour
{
    public const string ForceDesktopPref = "Forge.ForceDesktop";

    public GameObject xrRig;
    public DesktopRig desktopRig;
    [Tooltip("Draws the room (and wall colliders) when there's no passthrough.")]
    public GameObject desktopRoom;
    [Tooltip("Room loaded when there's no device. Captured from the simulator with MRUK.SaveSceneToJsonString.")]
    public TextAsset desktopRoomJson;
    public DeskLocator locator;
    public DeskPresence presence;

    public static bool IsDesktop { get; private set; }

    void Awake()
    {
        // Forge > Desktop Mode keeps XR from starting (ForgeModeMenu), so this check covers it too.
        bool force = false;
#if UNITY_EDITOR
        force = UnityEditor.EditorPrefs.GetBool(ForceDesktopPref, false);
#endif
        var xrManager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        bool xr = xrManager != null && xrManager.activeLoader != null;
        IsDesktop = !xr;

        desktopRig.gameObject.SetActive(!xr);
        desktopRoom.SetActive(!xr);

        if (!xr)
        {
            // MRUK needs an active OVRCameraRig, so keep the object and switch off its cameras,
            // hands/controllers and passthrough instead (cameras again in Start, after the rig's own Awake).
            DisableXrCameras();
            var passthrough = xrRig.GetComponent<OVRPassthroughLayer>();
            if (passthrough != null) passthrough.enabled = false;
            var interaction = xrRig.transform.Find("OVRComprehensiveInteractionRig");
            if (interaction != null) interaction.gameObject.SetActive(false);

            var mruk = FindAnyObjectByType<MRUK>();
            if (mruk != null && desktopRoomJson != null)
            {
                mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Json;
                mruk.SceneSettings.SceneJsons = new[] { desktopRoomJson };
                mruk.SceneSettings.RoomIndex = 0;
            }
            locator.head = desktopRig.Head;
            presence.head = desktopRig.Head;
        }
        Debug.Log("[RigSelector] " + (xr ? "XR rig (headset or simulator)" : "Desktop rig: WASD + mouse, E to sit") + (force && !xr ? " (Forge > Desktop Mode is on)" : ""));
    }

    void Start()
    {
        if (IsDesktop) DisableXrCameras();
    }

    // OVRCameraRig re-enables its eye cameras, so stop the rig component too, and untag the cameras so
    // Camera.main (used by UI raycasts) is the desktop camera.
    void DisableXrCameras()
    {
        var rig = xrRig.GetComponent<OVRCameraRig>();
        if (rig != null) rig.enabled = false;
        foreach (var c in xrRig.GetComponentsInChildren<Camera>(true))
        {
            c.enabled = false;
            c.gameObject.tag = "Untagged";
        }
        foreach (var a in xrRig.GetComponentsInChildren<AudioListener>(true)) a.enabled = false;
    }
}
