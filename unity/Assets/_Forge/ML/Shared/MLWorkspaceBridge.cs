using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>Connects module prefabs to Core Workspace at runtime without changing Core-owned scenes.</summary>
    public sealed class MLWorkspaceBridge : MonoBehaviour
    {
        const string ModeId = "ml", LabId = "ml-neural-lab", LabResource = "MLPanels/NeuralLab", Lab3DResource = "MLPanels/NeuralNet3D";
        static readonly string[] Supported = { "ml-datastack", "ml-model3d", "ml-results", "ml-training", "ml-viz-card", "ml-regression-results" };
        static readonly string[] ResourceNames = { "MLPanels/DataStack", "MLPanels/Model3D", "MLPanels/ResultsWall", "MLPanels/TrainingDash", "MLPanels/VizCard", "MLPanels/RegressionResults" };
        Workspace workspace;
        IDisposable subscription;
        readonly Dictionary<string, GameObject> assigned = new Dictionary<string, GameObject>();  // prefabs from Main
        // Neural Net Lab view: the lab alone in the middle of the ML desk. The project's panels wait in projectItems.
        List<WorkspaceItem> projectItems;
        public static bool LabOpen { get; private set; }

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
            LabOpen = false;  // statics outlive a play session when domain reload is off
            Configure(null);
            subscription = ForgeNet.On("ml.layout", ApplyLayout, true);
            ForgeNet.StatusChanged += OnNetStatus;
            if (ForgeNet.Status == ForgeNetStatus.Live) OnNetStatus(ForgeNetStatus.Live);
        }
        void OnDestroy() { subscription?.Dispose(); ForgeNet.StatusChanged -= OnNetStatus; }

        // ml.layout is sent once, when setup finishes: a headset that connects later (app started after the hub
        // opened the repo, a reconnect) asks for the open project's layout instead.
        void OnNetStatus(ForgeNetStatus status)
        {
            if (status != ForgeNetStatus.Live) return;
            ForgeNet.Get("/ml/project", text =>
            {
                JToken layout;
                try { layout = JObject.Parse(text)["layout"]; }
                catch (Exception) { return; }
                if (layout != null && layout.Type == JTokenType.Object) ApplyLayout(layout);
            }, _ => { });
        }

        void ApplyLayout(JToken payload)
        {
            var panels = payload == null ? null : payload["panels"] as JArray;
            if (panels == null) return;
            var requested = new List<string>();
            bool regression = payload.Value<string>("archetype") == "regression";
            foreach (var token in panels)
            {
                string id = token.Value<string>();
                // Regression: "results" is the predicted-vs-actual scatter. The image stack and the layer blocks
                // have no table-row or scikit-learn view yet, so they'd show the image demo: leave them out.
                if (regression && (id == "ml-datastack" || id == "ml-model3d")) continue;
                if (regression && id == "ml-results") id = "ml-regression-results";
                else if (id == "ml-dots") id = "ml-results";
                else if (id == "ml-live") id = "ml-training";
                else if (id == "ml-viz") id = "ml-viz-card";
                else if (id == "ml-regression") id = "ml-regression-results";
                if (Array.IndexOf(Supported, id) >= 0 && !requested.Contains(id)) requested.Add(id);
            }
            Configure(requested);
            // "open": Albie opened the project (ml_open), so the ML desk unfolds without a tap on the dock.
            if (payload.Value<bool?>("open") == true)
            {
                if (LabOpen) SetLab(false);  // "open my ML project" from the Lab: back to the project's panels
                if (workspace.ActiveMode != ModeId) workspace.Open(ModeId);
            }
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
            if (LabOpen) { projectItems = items; return; }  // a layout that arrives in the lab applies on the way back
            mode.items = items;
            Reopen();
        }

        void Reopen()
        {
            if (workspace.ActiveMode != ModeId) return;
            workspace.Close();
            workspace.Open(ModeId);
        }

        /// <summary>Swap the ML desk between the project's panels and the Neural Net Lab, and open the ML desk when
        /// <paramref name="open"/> (the Lab button, Albie's nn_control). False when the ML desk can't open (away from the desk).</summary>
        public static bool ShowLab(bool on, bool open = true)
        {
            var bridge = FindAnyObjectByType<MLWorkspaceBridge>();
            if (bridge == null) return false;
            bridge.SetLab(on);
            if (open && bridge.workspace.ActiveMode != ModeId) bridge.workspace.Open(ModeId);
            return bridge.workspace.ActiveMode == ModeId;
        }

        void SetLab(bool on)
        {
            var mode = workspace.modes.Find(m => m.id == ModeId);
            if (mode == null || on == LabOpen) return;
            if (on)
            {
                projectItems = mode.items;
                var prefab = Resources.Load<GameObject>(LabResource);
                if (prefab == null) { Debug.LogWarning("[ML] No " + LabResource + " prefab: Forge > ML > Build Neural Lab Prefab"); return; }
                mode.items = new List<WorkspaceItem> { new WorkspaceItem(LabId, "Neural Net Lab", WorkspaceSlot.Main) { prefab = prefab, kind = WorkspaceItemKind.Panel } };
                var net3D = Resources.Load<GameObject>(Lab3DResource);  // the same network in 3D, standing on the desk
                if (net3D != null) mode.items.Add(new WorkspaceItem("ml-neural-3d", "Neural network", WorkspaceSlot.DeskCenter, WorkspaceItemKind.Volume) { prefab = net3D });
            }
            else if (projectItems != null) mode.items = projectItems;
            LabOpen = on;
            Reopen();
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
