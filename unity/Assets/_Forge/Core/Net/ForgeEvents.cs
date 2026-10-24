using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Hub event payloads: PROJECT_CONTEXT section 7 + CAD_PLAN section 5. Mirror of hub/events.py.
// Field names are snake_case on purpose so they match the JSON (and the Python models) one to one.
// Change these only together with section 7 and events.py.

[Serializable] public class MlRunState { public string run_id; public string status; public int epoch; public int total_epochs; }
[Serializable] public class MlMetrics { public string run_id; public int epoch; public int step; public float train_loss; public float train_acc; public float val_loss; public float val_acc; public float lr; }
[Serializable] public class MlOverfitAlert { public string run_id; public int epoch; public string reason; }
[Serializable] public class MlGraphReady { }
[Serializable] public class MlResultsReady { public string run_id; }
[Serializable] public class MlSampleUpdated { public string id; public int label_idx; public bool removed; }

[Serializable]
public class CadAnalysis
{
    public float mass_g;
    public float[] cg_gltf;
    public float? length_mm;
    public float? diameter_mm;
    public float? cg_mm;            // from the nose tip
    public float? cp_mm;            // from the nose tip
    public float? margin_cal;
    public bool? stable;
    public string stability;        // unstable | marginal | stable | overstable
    public float? cna_per_rad;
    public string motor;
    public Dictionary<string, float[]> markers_gltf;
    public List<Dictionary<string, object>> breakdown;
    public List<string> assumptions;
}

[Serializable]
public class CadModelUpdated
{
    public int version;
    public Dictionary<string, object> @params;
    public int? previous_version;
    public List<string> changed_ids;
    public List<string> removed_ids;
    public string source;           // api | pet | script | undo | reset | startup
    public CadAnalysis analysis;
}

[Serializable] public class CadRegenFailed { public string source; public string kind; public string error; public int? error_line; public int? kept_version; }
[Serializable] public class CadView { public bool? explode; public string focus_id; }

[Serializable] public class PetState { public string pet_id; public string mode; public string target_panel; }
[Serializable] public class PetCardAction { public string label; public string action_id; }
[Serializable] public class PetCard { public string title; public List<string> bullets; public List<PetCardAction> actions; }
[Serializable] public class PetSay { public string pet_id; public string text; public PetCard card; }
[Serializable] public class UiHighlight { public string panel; public List<string> item_ids; public int? class_idx; }

[Serializable] public class NotifyMessage { public string @from; public string text; public string thread_id; }
[Serializable] public class NotifySent { public string to; public string text; }

/// <summary>
/// One static event per hub event type. Subscribe in OnEnable, unsubscribe in OnDisable:
/// <code>ForgeEvents.OnCadModelUpdated += HandleModel;</code>
/// <see cref="ForgeNet"/> raises these on the main thread for live and mock events alike.
/// For a type not listed here, use <see cref="ForgeNet.On"/>.
/// </summary>
public static class ForgeEvents
{
    public const string MlRunStateType = "ml.run_state";
    public const string MlMetricsType = "ml.metrics";
    public const string MlOverfitAlertType = "ml.overfit_alert";
    public const string MlGraphReadyType = "ml.graph_ready";
    public const string MlResultsReadyType = "ml.results_ready";
    public const string MlSampleUpdatedType = "ml.sample_updated";
    public const string CadModelUpdatedType = "cad.model_updated";
    public const string CadRegenFailedType = "cad.regen_failed";
    public const string CadViewType = "cad.view";
    public const string PetStateType = "pet.state";
    public const string PetSayType = "pet.say";
    public const string UiHighlightType = "ui.highlight";
    public const string NotifyMessageType = "notify.message";
    public const string NotifySentType = "notify.sent";

    public static event Action<MlRunState> OnMlRunState;
    public static event Action<MlMetrics> OnMlMetrics;
    public static event Action<MlOverfitAlert> OnMlOverfitAlert;
    public static event Action<MlGraphReady> OnMlGraphReady;
    public static event Action<MlResultsReady> OnMlResultsReady;
    public static event Action<MlSampleUpdated> OnMlSampleUpdated;
    public static event Action<CadModelUpdated> OnCadModelUpdated;
    public static event Action<CadRegenFailed> OnCadRegenFailed;
    public static event Action<CadView> OnCadView;
    public static event Action<PetState> OnPetState;
    public static event Action<PetSay> OnPetSay;
    public static event Action<UiHighlight> OnUiHighlight;
    public static event Action<NotifyMessage> OnNotifyMessage;
    public static event Action<NotifySent> OnNotifySent;

    /// <summary>Raise the typed event for <paramref name="type"/>. Returns false for unknown types.</summary>
    internal static bool Raise(string type, JToken payload)
    {
        switch (type)
        {
            case MlRunStateType: Invoke(OnMlRunState, payload); return true;
            case MlMetricsType: Invoke(OnMlMetrics, payload); return true;
            case MlOverfitAlertType: Invoke(OnMlOverfitAlert, payload); return true;
            case MlGraphReadyType: Invoke(OnMlGraphReady, payload); return true;
            case MlResultsReadyType: Invoke(OnMlResultsReady, payload); return true;
            case MlSampleUpdatedType: Invoke(OnMlSampleUpdated, payload); return true;
            case CadModelUpdatedType: Invoke(OnCadModelUpdated, payload); return true;
            case CadRegenFailedType: Invoke(OnCadRegenFailed, payload); return true;
            case CadViewType: Invoke(OnCadView, payload); return true;
            case PetStateType: Invoke(OnPetState, payload); return true;
            case PetSayType: Invoke(OnPetSay, payload); return true;
            case UiHighlightType: Invoke(OnUiHighlight, payload); return true;
            case NotifyMessageType: Invoke(OnNotifyMessage, payload); return true;
            case NotifySentType: Invoke(OnNotifySent, payload); return true;
            default: return false;
        }
    }

    static void Invoke<T>(Action<T> handlers, JToken payload)
    {
        if (handlers == null) return;
        T data;
        try
        {
            data = payload == null || payload.Type == JTokenType.Null ? default : payload.ToObject<T>();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ForgeEvents] Payload doesn't match {typeof(T).Name}: {e.Message}\n{payload}");
            return;
        }
        // One failing subscriber must not stop the others.
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(data); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
