using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Hub event payloads: PROJECT_CONTEXT section 7 (+ 8.8 for notify.*) + CAD_PLAN section 5. Mirror of hub/events.py.
// Newtonsoft DTOs: JSON names are the hub's snake_case. Change these only together with section 7 and events.py.
// CAD REST shapes that aren't events (CadParams, CadMarkers, CadFlight, ...) live in CAD/Scripts/CadData.cs.

public class MlRunState
{
    [JsonProperty("run_id")] public string runId;
    [JsonProperty("status")] public string status;  // idle | training | evaluating | done
    [JsonProperty("epoch")] public int epoch;
    [JsonProperty("total_epochs")] public int totalEpochs;
}

public class MlMetrics
{
    [JsonProperty("run_id")] public string runId;
    [JsonProperty("epoch")] public int epoch;
    [JsonProperty("step")] public int step;
    [JsonProperty("train_loss")] public float trainLoss;
    [JsonProperty("train_acc")] public float trainAcc;
    [JsonProperty("val_loss")] public float valLoss;
    [JsonProperty("val_acc")] public float valAcc;
    [JsonProperty("lr")] public float lr;
}

public class MlOverfitAlert
{
    [JsonProperty("run_id")] public string runId;
    [JsonProperty("epoch")] public int epoch;
    [JsonProperty("reason")] public string reason;
}

public class MlGraphReady { }

public class MlResultsReady
{
    [JsonProperty("run_id")] public string runId;
}

public class MlSampleUpdated
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label_idx")] public int labelIdx;
    [JsonProperty("removed")] public bool removed;
}

public class CadAnalysis
{
    [JsonProperty("mass_g")] public float massG;
    [JsonProperty("length_mm")] public float? lengthMm;
    [JsonProperty("diameter_mm")] public float? diameterMm;
    [JsonProperty("cg_mm")] public float? cgMm;               // from the nose tip
    [JsonProperty("cp_mm")] public float? cpMm;               // from the nose tip
    [JsonProperty("margin_cal")] public float? marginCal;
    [JsonProperty("stable")] public bool? stable;
    [JsonProperty("stability")] public string stability;      // unstable | marginal | stable | overstable
    [JsonProperty("motor")] public string motor;
    [JsonProperty("markers_gltf")] public CadMarkers markers; // GLB scene space: metres, Y-up, on the axis
    [JsonProperty("size_mm")] public float[] sizeMm;
    [JsonProperty("part_count")] public int? partCount;
    [JsonProperty("metrics")] public List<CadMetric> metrics = new List<CadMetric>();  // any design; CadData.cs
}

public class CadModelUpdated
{
    [JsonProperty("version")] public int version;
    [JsonProperty("part")] public string part;                // design id: hub/cad/parts/<part>.py
    [JsonProperty("title")] public string title;
    [JsonProperty("params")] public Dictionary<string, object> parameters;
    [JsonProperty("previous_version")] public int? previousVersion;
    [JsonProperty("changed_ids")] public List<string> changedIds = new List<string>();
    [JsonProperty("removed_ids")] public List<string> removedIds = new List<string>();
    [JsonProperty("source")] public string source;            // api | pet | preset | script | undo | reset | open | startup
    [JsonProperty("analysis")] public CadAnalysis analysis;
}

public class CadRegenFailed
{
    [JsonProperty("source")] public string source;
    [JsonProperty("kind")] public string kind;  // params | build | timeout | crash
    [JsonProperty("error")] public string error;
    [JsonProperty("error_line")] public int? errorLine;
    [JsonProperty("kept_version")] public int? keptVersion;
}

public class CadView
{
    [JsonProperty("explode")] public bool? explode;
    [JsonProperty("focus_id")] public string focusId;
}

public class PetState
{
    [JsonProperty("pet_id")] public string petId;
    [JsonProperty("mode")] public string mode;  // follow | idle | listening | thinking | working | speaking
    [JsonProperty("target_panel")] public string targetPanel;
}

public class PetCardAction
{
    [JsonProperty("label")] public string label;
    [JsonProperty("action_id")] public string actionId;
}

public class PetCard
{
    [JsonProperty("title")] public string title;
    [JsonProperty("bullets")] public List<string> bullets;
    [JsonProperty("actions")] public List<PetCardAction> actions;
}

public class PetSay
{
    [JsonProperty("pet_id")] public string petId;
    [JsonProperty("text")] public string text;
    [JsonProperty("card")] public PetCard card;
}

public class UiHighlight
{
    [JsonProperty("panel")] public string panel;
    [JsonProperty("item_ids")] public List<string> itemIds;
    [JsonProperty("class_idx")] public int? classIdx;
}

public class NotifyMessage
{
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;
    [JsonProperty("user")] public string user;
    [JsonProperty("user_name")] public string userName;
    [JsonProperty("text")] public string text;
    [JsonProperty("ts")] public string ts;                    // Slack message ts; reply thread key
    [JsonProperty("thread_ts")] public string threadTs;
    [JsonProperty("mentions_me")] public bool mentionsMe;
}

public class NotifySent
{
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;
    [JsonProperty("text")] public string text;
    [JsonProperty("ts")] public string ts;
}

public class NotifyDigest
{
    [JsonProperty("count")] public int count;
    [JsonProperty("mentions")] public int mentions;
    [JsonProperty("line")] public string line;                // what Bit says; empty when count is 0
    [JsonProperty("messages")] public List<NotifyMessage> messages = new List<NotifyMessage>();  // newest first, at most 4
}

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
    public const string NotifyDigestType = "notify.digest";

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
    public static event Action<NotifyDigest> OnNotifyDigest;

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
            case NotifyDigestType: Invoke(OnNotifyDigest, payload); return true;
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
