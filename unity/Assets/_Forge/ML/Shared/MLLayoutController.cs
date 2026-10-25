using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    public sealed class MLLayoutController : MonoBehaviour
    {
        IDisposable subscription;
        void OnEnable() { subscription = ForgeNet.On("ml.layout", Apply, true); }
        void OnDisable() { subscription?.Dispose(); subscription = null; }
        void Apply(JToken payload)
        {
            var requested = payload == null ? null : payload["panels"] as JArray;
            if (requested == null) return;
            bool Has(string id)
            {
                foreach (var panel in requested)
                {
                    string value = panel.Value<string>();
                    if (value == id || (value == "ml-dots" && id == "ml-results") || (value == "ml-live" && id == "ml-training") || (value == "ml-viz" && id == "ml-viz-card") || (value == "ml-regression" && id == "ml-regression-results")) return true;
                }
                return false;
            }
            foreach (var panel in FindObjectsByType<ForgePanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (panel.panelId.StartsWith("ml-")) panel.gameObject.SetActive(Has(panel.panelId));
        }
    }
}
