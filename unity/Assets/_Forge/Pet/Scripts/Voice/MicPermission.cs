using System;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Forge.Pet
{
    /// <summary>
    /// Microphone permission helper. On Android (device builds) asks the OS for
    /// android.permission.RECORD_AUDIO; everywhere else it resolves to true immediately.
    /// Callbacks arrive on the Unity main thread.
    /// </summary>
    public static class MicPermission
    {
        public static bool HasPermission
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
                return true;
#endif
            }
        }

        /// <summary>Requests the permission if needed; invokes <paramref name="done"/> with the result.</summary>
        public static void Request(Action<bool> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (HasPermission) { done?.Invoke(true); return; }
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => done?.Invoke(true);
            callbacks.PermissionDenied += _ =>
            {
                Debug.LogWarning("[MicPermission] Microphone permission denied.");
                done?.Invoke(false);
            };
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
#else
            done?.Invoke(true);
#endif
        }

        /// <summary>Awaitable version of <see cref="Request"/>.</summary>
        public static Task<bool> RequestAsync()
        {
            if (HasPermission) return Task.FromResult(true);
            var tcs = new TaskCompletionSource<bool>();
            Request(ok => tcs.TrySetResult(ok));
            return tcs.Task;
        }
    }
}
