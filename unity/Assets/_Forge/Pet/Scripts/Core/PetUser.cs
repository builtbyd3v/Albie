using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Rig-agnostic access to the user's head. Resolves Camera.main, falls back to any enabled
    /// Camera, caches the result and re-resolves if it is destroyed or disabled.
    /// No Meta/XRI types: works with DesktopRig, an XR rig, or anything with a camera.
    /// </summary>
    public static class PetUser
    {
        const float RetryInterval = 0.25f;       // throttle searches when no camera exists

        static Camera s_camera;
        static float s_nextSearch;
        static Camera[] s_buffer = new Camera[8];

        /// <summary>The user's head transform, or null if no usable camera exists.</summary>
        public static Transform Head
        {
            get
            {
                Camera cam = HeadCamera;
                return cam != null ? cam.transform : null;
            }
        }

        /// <summary>The camera behind <see cref="Head"/> (used for mouse raycasts).</summary>
        public static Camera HeadCamera
        {
            get
            {
                if (!IsUsable(s_camera)) Resolve();
                return s_camera;
            }
        }

        static bool IsUsable(Camera c) => c != null && c.isActiveAndEnabled;

        static void Resolve()
        {
            s_camera = null;
            if (Time.unscaledTime < s_nextSearch) return;

            Camera main = Camera.main;
            if (IsUsable(main)) { s_camera = main; return; }

            // Fallback: first enabled camera that renders to the screen (no allocation unless the buffer must grow).
            int count = Camera.allCamerasCount;
            if (count > s_buffer.Length) s_buffer = new Camera[count * 2];
            count = Camera.GetAllCameras(s_buffer);
            for (int i = 0; i < count; i++)
            {
                Camera c = s_buffer[i];
                if (IsUsable(c) && c.targetTexture == null) { s_camera = c; break; }
            }
            System.Array.Clear(s_buffer, 0, count);

            if (s_camera == null) s_nextSearch = Time.unscaledTime + RetryInterval;
        }

        // Support "Enter Play Mode Options" with domain reload disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_camera = null;
            s_nextSearch = 0f;
        }
    }
}
