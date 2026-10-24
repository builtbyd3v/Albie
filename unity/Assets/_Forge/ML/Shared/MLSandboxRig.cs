using UnityEngine;
using UnityEngine.XR.Management;

namespace Forge.ML
{
    // Sandbox version of Core's RigSelector: with an XR runtime (headset or Meta XR Simulator) the Meta camera rig
    // with Interaction SDK hands/controllers is used; otherwise the desktop camera. Main uses RigSelector instead.
    [DefaultExecutionOrder(-1000)]
    public sealed class MLSandboxRig : MonoBehaviour
    {
        public GameObject xrRig;
        public Camera desktopCamera;

        public static bool IsXR { get; private set; }

        void Awake()
        {
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            IsXR = manager != null && manager.activeLoader != null;
            desktopCamera.gameObject.SetActive(!IsXR);
            if (!IsXR) DisableXrCameras();
            Debug.Log("[ML] " + (IsXR ? "XR rig (headset or simulator)" : "Desktop camera"));
        }

        void Start()
        {
            if (!IsXR) DisableXrCameras();
        }

        // OVRCameraRig re-enables its eye cameras, so stop the rig component too and untag the cameras so
        // Camera.main stays the desktop camera.
        void DisableXrCameras()
        {
            var rig = xrRig.GetComponent<OVRCameraRig>();
            if (rig != null) rig.enabled = false;
            var passthrough = xrRig.GetComponent<OVRPassthroughLayer>();
            if (passthrough != null) passthrough.enabled = false;
            var interaction = xrRig.transform.Find("OVRComprehensiveInteractionRig");
            if (interaction != null) interaction.gameObject.SetActive(false);
            foreach (var c in xrRig.GetComponentsInChildren<Camera>(true))
            {
                c.enabled = false;
                c.gameObject.tag = "Untagged";
            }
            foreach (var a in xrRig.GetComponentsInChildren<AudioListener>(true)) a.enabled = false;
        }
    }
}
