using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>Connects module prefabs to Core Workspace at runtime without changing Core-owned scenes.</summary>
    public sealed class MLWorkspaceBridge : MonoBehaviour
    {
        const string ModeId = "ml";
        static readonly string[] Supported = { "ml-datastack", "ml-model3d", "ml-results", "ml-training", "ml-viz-card", "ml-regression-results" };
        static readonly string[] ResourceNames = { "MLPanels/DataStack", "MLPanels/Model3D", "MLPanels/ResultsWall", "MLPanels/TrainingDash", "MLPanels/VizCard", "MLPanels/RegressionResults" };
        Workspace workspace;
        IDisposable subscription;
        readonly Dictionary<string, GameObject> assigned = new Dictionary<string, GameObject>();  // prefabs from Main

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var workspace = FindAnyObjectByType<Workspace>();
            if (workspace == null || workspace.GetComponent<MLWorkspaceBridge>() != null) return;
            workspace.gameObject.AddComponent<MLWorkspaceBridge>();
        }

        void Awake()
        {
            workspace = GetComponent<Workspace>();
            Configure(null);
            subscription = ForgeNet.On("ml.layout", ApplyLayout, true);
        }
        void OnDestroy() { subscription?.Dispose(); }

        void ApplyLayout(JToken payload)
        {
            var panels = payload == null ? null : payload["panels"] as JArray;
            if (panels == null) return;
            var requested = new List<string>();
            foreach (var token in panels)
            {
                string id = token.Value<string>();
                if (id == "ml-dots") id = "ml-results";
                else if (id == "ml-live") id = "ml-training";
                else if (id == "ml-viz") id = "ml-viz-card";
                else if (id == "ml-regression") id = "ml-regression-results";
                if (Array.IndexOf(Supported, id) >= 0 && !requested.Contains(id)) requested.Add(id);
            }
            Configure(requested);
        }

        void Configure(List<string> requested)
        {
            var mode = workspace.modes.Find(m => m.id == ModeId);
            if (mode == null) return;
            // Main's builder already slots the ML prefabs (MainSceneBuilder.ModulePrefabs); keep those and
            // anything that isn't ML (screen-main's VS Code prefab). Resources copies fill in what's missing.
            if (requested == null && mode.items.TrueForAll(m => !m.id.StartsWith("ml-") || m.prefab != null)) return;
            foreach (var item in mode.items)
                if (item.prefab != null && !assigned.ContainsKey(item.id)) assigned[item.id] = item.prefab;
            var items = mode.items.FindAll(m => !m.id.StartsWith("ml-"));
            if (items.Count == 0) items.Add(new WorkspaceItem("screen-main", "VS Code", WorkspaceSlot.Main));
            string[] defaults = { "ml-datastack", "ml-model3d", "ml-results", "ml-training" };
            for (int i = 0; i < Supported.Length; i++)
            {
                string id = Supported[i];
                if (requested != null ? !requested.Contains(id) : Array.IndexOf(defaults, id) < 0) continue;
                if (!assigned.TryGetValue(id, out var prefab)) prefab = Resources.Load<GameObject>(ResourceNames[i]);
                if (prefab == null) continue;
                var slot = Slot(id);
                var item = new WorkspaceItem(id, Title(id), slot) { prefab = prefab, kind = WorkspaceItemKind.Panel };
                items.Add(item);
            }
            mode.items = items;
            if (workspace.ActiveMode == ModeId)
            {
                workspace.Close();
                workspace.Open(ModeId);
            }
        }

        static WorkspaceSlot Slot(string id)
        {
            switch (id)
            {
                case "ml-datastack": return WorkspaceSlot.FarLeft;
                case "ml-model3d": return WorkspaceSlot.Left;
                case "ml-training": return WorkspaceSlot.Above;
                case "ml-results":
                case "ml-regression-results": return WorkspaceSlot.Right;
                default: return WorkspaceSlot.FarRight;
            }
        }
        static string Title(string id)
        {
            switch (id)
            {
                case "ml-datastack": return "Dataset";
                case "ml-model3d": return "Model";
                case "ml-results": return "Results";
                case "ml-training": return "Training";
                case "ml-viz-card": return "Visualizations";
                default: return "Regression";
            }
        }
    }
}
