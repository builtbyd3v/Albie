using System.Reflection;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// The pet's one window onto Core's <see cref="Workspace"/> (desk modes and the panels / volumes they unfold).
    /// Everything here is null-safe for scenes without a Workspace (the sandboxes): <see cref="HasWorkspace"/> is false,
    /// <see cref="ActiveMode"/> is null, <see cref="Open"/> returns false, <see cref="TryGetItemTransform"/> finds nothing.
    ///
    /// <see cref="TryGetItemTransform"/> calls Core's <c>Workspace.TryGetItemTransform(string, out Transform)</c> through
    /// reflection when that method exists (Core PR C adds it) and otherwise scans the scene for a ForgePanel / ForgeVolume
    /// with that id. Once PR C is in, replace the reflection with a direct call; nothing else in Pet needs to change.
    /// Not for per-frame use: the scans allocate.
    /// </summary>
    public static class PetWorkspace
    {
        const float RetryInterval = 0.5f;

        static Workspace s_workspace;
        static float s_nextSearch;
        static bool s_lookedUpMethod;
        static MethodInfo s_tryGetItem;
        static readonly object[] s_args = new object[2];

        /// <summary>The scene's Workspace, or null (looked up again at most twice a second while missing).</summary>
        public static Workspace Current
        {
            get
            {
                if (s_workspace == null && Time.unscaledTime >= s_nextSearch)
                {
                    s_workspace = Object.FindAnyObjectByType<Workspace>();
                    if (s_workspace == null) s_nextSearch = Time.unscaledTime + RetryInterval;
                }
                return s_workspace;
            }
        }

        public static bool HasWorkspace => Current != null;

        /// <summary>Id of the open desk mode ("code", "cad", ...), or null when none is open or there is no Workspace.</summary>
        public static string ActiveMode
        {
            get
            {
                Workspace w = Current;
                return w != null ? w.ActiveMode : null;
            }
        }

        /// <summary>True if the Workspace lists a mode with this id.</summary>
        public static bool HasMode(string modeId)
        {
            Workspace w = Current;
            return w != null && FindMode(w, modeId) != null;
        }

        /// <summary>
        /// Open <paramref name="modeId"/> (or <paramref name="fallbackModeId"/> when the first id isn't a mode, e.g. "code"
        /// before Core renames "screens"). True if that mode is open afterwards. False without a Workspace, when neither id is
        /// a mode, or when the Workspace refused (the user isn't at a desk).
        /// </summary>
        public static bool Open(string modeId, string fallbackModeId = null)
        {
            Workspace w = Current;
            if (w == null) return false;
            string id = FindMode(w, modeId) != null ? modeId
                      : !string.IsNullOrEmpty(fallbackModeId) && FindMode(w, fallbackModeId) != null ? fallbackModeId : null;
            if (id == null) return false;
            if (w.ActiveMode == id) return true;
            w.Open(id);
            return w.ActiveMode == id;
        }

        /// <summary>
        /// Keep the desk dock hidden (the arrival greeting owns the moment) or let it show. Releasing while at the desk
        /// shows it with the dock's entrance (frame slides in from the right, rings plop on). No-op without a Workspace.
        /// </summary>
        public static void HoldDock(bool hold)
        {
            Workspace w = Current;
            if (w != null) w.HoldDock(hold);
        }

        /// <summary>
        /// Transform of the open panel / volume for a workspace item id ("screen-main", "cad-viewer", "messages").
        /// False when nothing with that id is open right now.
        /// </summary>
        public static bool TryGetItemTransform(string itemId, out Transform t)
        {
            t = null;
            if (string.IsNullOrEmpty(itemId)) return false;
            Workspace w = Current;
            if (w == null) return false;

            // Core PR C: public bool Workspace.TryGetItemTransform(string itemId, out Transform t)
            if (!s_lookedUpMethod)
            {
                s_lookedUpMethod = true;
                s_tryGetItem = typeof(Workspace).GetMethod("TryGetItemTransform", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(string), typeof(Transform).MakeByRefType() }, null);
            }
            if (s_tryGetItem != null)
            {
                s_args[0] = itemId;
                s_args[1] = null;
                bool ok = false;
                try { ok = (bool)s_tryGetItem.Invoke(w, s_args); }
                catch (System.Exception e) { Debug.LogWarning("[PetWorkspace] Workspace.TryGetItemTransform threw: " + e.Message); }
                t = s_args[1] as Transform;
                s_args[1] = null;
                if (ok && t != null) return true;
                t = null;
            }

            // Fallback: whatever is unfolded right now, by id (set by Workspace.Spawn on placeholders, by the module prefabs
            // otherwise), by the placeholder's name, or by the item's title.
            string title = ItemTitle(w, itemId);
            string panelName = "Panel " + itemId, volumeName = "Volume " + itemId;
            ForgePanel[] panels = Object.FindObjectsByType<ForgePanel>(FindObjectsSortMode.None);
            for (int pass = 0; pass < 3 && t == null; pass++)
                for (int i = 0; i < panels.Length; i++)
                {
                    ForgePanel p = panels[i];
                    if (p == null || !p.gameObject.activeInHierarchy) continue;
                    bool hit = pass == 0 ? p.panelId == itemId
                             : pass == 1 ? p.name == panelName
                             : title != null && p.Title == title;
                    if (hit) { t = p.transform; break; }
                }
            if (t != null) return true;
            ForgeVolume[] volumes = Object.FindObjectsByType<ForgeVolume>(FindObjectsSortMode.None);
            for (int pass = 0; pass < 3 && t == null; pass++)
                for (int i = 0; i < volumes.Length; i++)
                {
                    ForgeVolume v = volumes[i];
                    if (v == null || !v.gameObject.activeInHierarchy) continue;
                    bool hit = pass == 0 ? v.volumeId == itemId
                             : pass == 1 ? v.name == volumeName
                             : title != null && v.Title == title;
                    if (hit) { t = v.transform; break; }
                }
            return t != null;
        }

        /// <summary>True if the open item is a volume (sits on the desk) rather than a panel (floats at eye level).</summary>
        public static bool IsVolume(Transform item) => item != null && item.GetComponent<ForgeVolume>() != null;

        static WorkspaceMode FindMode(Workspace w, string modeId)
        {
            if (string.IsNullOrEmpty(modeId) || w.modes == null) return null;
            for (int i = 0; i < w.modes.Count; i++)
                if (w.modes[i] != null && w.modes[i].id == modeId) return w.modes[i];
            return null;
        }

        static string ItemTitle(Workspace w, string itemId)
        {
            if (w.modes == null) return null;
            for (int m = 0; m < w.modes.Count; m++)
            {
                var items = w.modes[m] != null ? w.modes[m].items : null;
                if (items == null) continue;
                for (int i = 0; i < items.Count; i++)
                    if (items[i] != null && items[i].id == itemId && !string.IsNullOrEmpty(items[i].title)) return items[i].title;
            }
            return null;
        }

        // Support "Enter Play Mode Options" with domain reload disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_workspace = null;
            s_nextSearch = 0f;
        }
    }
}
