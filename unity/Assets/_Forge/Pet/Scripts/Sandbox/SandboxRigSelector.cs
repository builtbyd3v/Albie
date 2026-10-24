using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Forge.Pet
{
    /// <summary>
    /// Enables the XR rig when an XR display is running, otherwise the desktop rig.
    /// Both references are optional. Re-checks in Start in case XR finished initialising late.
    /// </summary>
    public class SandboxRigSelector : MonoBehaviour
    {
        [SerializeField, Tooltip("Enabled when an XR device is active (e.g. the Meta camera rig).")]
        GameObject xrRig;
        [SerializeField, Tooltip("Enabled when no XR device is active (DesktopRig).")]
        GameObject desktopRig;

        static readonly List<XRDisplaySubsystem> s_displays = new List<XRDisplaySubsystem>();

        public bool XRActive { get; private set; }

        void Awake() => Apply();

        void Start()
        {
            if (IsXRRunning() != XRActive) Apply();
        }

        /// <summary>True if XRSettings reports a device or any XR display subsystem is running.</summary>
        public static bool IsXRRunning()
        {
            if (XRSettings.isDeviceActive) return true;
            SubsystemManager.GetSubsystems(s_displays);
            for (int i = 0; i < s_displays.Count; i++)
                if (s_displays[i].running) return true;
            return false;
        }

        void Apply()
        {
            XRActive = IsXRRunning();
            if (xrRig != null) xrRig.SetActive(XRActive);
            if (desktopRig != null) desktopRig.SetActive(!XRActive);
            Debug.Log("[SandboxRigSelector] " + (XRActive ? "XR device active -> XR rig" : "No XR device -> DesktopRig"), this);
        }
    }
}
