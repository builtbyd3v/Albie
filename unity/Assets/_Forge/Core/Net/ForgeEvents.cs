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

    // Round 4 (CAD_PLAN section 5). Null when the version predates them ("not provided"). Positions and directions are
    // in the model frame (glTF scene space: metres, Y up), which is new Vector3(-x, y, z) under CadModel.Root in Unity.
    [JsonProperty("markers")] public List<CadMarker> markerList;  // C# name: markers is markers_gltf's
    [JsonProperty("dimensions")] public List<CadDimension> dimensions;
    [JsonProperty("explode")] public CadExplode explode;
    [JsonProperty("mass_breakdown")] public List<CadMassItem> massBreakdown;
    [JsonProperty("demo")] public CadDemo demo;

    // Round 4, part 2: the design panel's verdict and its fixed inputs (null / empty before then).
    [JsonProperty("verdict")] public CadVerdict verdict;
    [JsonProperty("constants")] public List<CadConstant> constants;
    [JsonProperty("assumptions")] public List<string> assumptions;  // what the analysis assumes (rocket: Barrowman, ...)
}

/// <summary>The panel's answer for this version, from its metrics ("Unstable", "Likely to tumble. 0.84 cal ...").</summary>
public class CadVerdict
{
    [JsonProperty("status")] public string status;            // ok | warn | bad
    [JsonProperty("headline")] public string headline;
    [JsonProperty("sentence")] public string sentence;
    [JsonProperty("metric")] public string metric;            // the metric id it's about (the dial)
}

/// <summary>A fixed input that isn't a parameter (the rocket's motor, the gearbox's input motor): an info row.</summary>
public class CadConstant
{
    [JsonProperty("label")] public string label;
    [JsonProperty("value")] public string value;
    [JsonProperty("note")] public string note;
}

/// <summary>A point to mark on the model: "cg" on every design, plus the design's own (cp, thrust, input, output...).</summary>
public class CadMarker
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label")] public string label;
    [JsonProperty("kind")] public string kind;                // cg | cp | thrust | input | output | ...: the style
    [JsonProperty("pos")] public float[] pos;                 // model frame
}

/// <summary>A dimension callout: value_mm from `from` to `to`, drawn offset_mm (design mm) along offset_dir.</summary>
public class CadDimension
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label")] public string label;
    [JsonProperty("value_mm")] public float valueMm;
    [JsonProperty("from")] public float[] fromPos;            // model frame, on the model
    [JsonProperty("to")] public float[] toPos;
    [JsonProperty("offset_dir")] public float[] offsetDir;    // unit, perpendicular to the span
    [JsonProperty("offset_mm")] public float? offsetMm;
}

/// <summary>The exploded view: one entry per leaf part (CadPart.id).</summary>
public class CadExplode
{
    [JsonProperty("axis")] public string axis;                // stack | radial | shaft | auto
    [JsonProperty("parts")] public List<CadExplodePart> parts = new List<CadExplodePart>();
}

public class CadExplodePart
{
    [JsonProperty("id")] public string id;
    [JsonProperty("dir")] public float[] dir;                 // model frame, unit
    [JsonProperty("dist_mm")] public float distMm;            // design mm; 0 = stays put
    [JsonProperty("order")] public int order;                 // stagger: 0 moves first
}

/// <summary>One row of the mass breakdown; part_ids is empty for a point mass (motor, parachute).</summary>
public class CadMassItem
{
    [JsonProperty("id")] public string id;
    [JsonProperty("label")] public string label;
    [JsonProperty("mass_g")] public float massG;
    [JsonProperty("share")] public float share;               // of analysis.mass_g
    [JsonProperty("part_ids")] public List<string> partIds = new List<string>();
}

public class CadAxis
{
    [JsonProperty("origin")] public float[] origin;           // model frame
    [JsonProperty("dir")] public float[] dir;                 // model frame, unit
}

/// <summary>A part the demo turns about origin + dir.</summary>
public class CadSpinPart
{
    [JsonProperty("id")] public string id;
    [JsonProperty("origin")] public float[] origin;
    [JsonProperty("dir")] public float[] dir;
    [JsonProperty("rpm")] public float rpm;                   // + is counter-clockwise seen from the tip of dir (glTF)
    [JsonProperty("spin")] public string spin;                // ccw | cw | none
    [JsonProperty("rel_speed")] public float? relSpeed;       // spin: speed relative to the input, signed
    [JsonProperty("motor_id")] public string motorId;         // hover: the motor under this prop
    [JsonProperty("radius_mm")] public float? radiusMm;       // prop swept radius, gear pitch radius (if the hub sends it)
}

/// <summary>What the design's demo button does: launch (fly GET source?v=N), hover, spin, or none.</summary>
public class CadDemo
{
    [JsonProperty("kind")] public string kind;                // launch | hover | spin | none
    [JsonProperty("label")] public string label;              // the button: Fly | Hover | Run
    [JsonProperty("note")] public string note;                // one plain sentence
    [JsonProperty("source")] public string source;            // launch: "/cad/flight"
    [JsonProperty("axis")] public CadAxis axis;               // launch: tail -> nose; hover: thrust centre, up; spin: input shaft
    [JsonProperty("values")] public Dictionary<string, float> values = new Dictionary<string, float>();
    [JsonProperty("spin_parts")] public List<CadSpinPart> spinParts = new List<CadSpinPart>();
    [JsonProperty("readouts")] public List<CadMetric> readouts = new List<CadMetric>();
    [JsonProperty("assumptions")] public List<string> assumptions = new List<string>();
    [JsonProperty("blocked")] public string blocked;              // why it can't run (props collide), or null
    [JsonProperty("reveal_ids")] public List<string> revealIds;   // parts lifted and turned to glass (a lid), if sent
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
    [JsonProperty("label")] public string label;              // the version in words: Bit's request, a preset, the change
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

/// <summary>
/// Bit pressing a button in the CAD view (cad.action, PET_AGENT_CONTRACT Addendum H). Not sticky: it means "now".
/// CadSession applies it once the viewer shows <see cref="version"/> of <see cref="part"/>.
/// </summary>
public class CadAction
{
    [JsonProperty("action")] public string action;  // expand | demo | markers | dimensions | tab | reassemble
    [JsonProperty("on")] public bool? on;            // expand / demo / markers / dimensions: on or off
    [JsonProperty("tab")] public string tab;         // tab: overview | parameters | analysis | history
    [JsonProperty("part")] public string part;       // the design it is for
    [JsonProperty("version")] public int? version;   // the version it is for
    [JsonProperty("seq")] public int seq;            // increasing per hub run
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

/// <summary>Bit opens a workspace view (ui.mode, Addendum H); <see cref="WorkspaceRemote"/> answers with workspace.mode.</summary>
public class UiMode
{
    [JsonProperty("mode")] public string mode;  // ml | cad | code | messages; "" closes the open one
}

public class NotifyMessage
{
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;
    [JsonProperty("is_dm")] public bool isDm;                 // a DM to the user (channel_name is "dm")
    [JsonProperty("user")] public string user;
    [JsonProperty("user_name")] public string userName;
    [JsonProperty("text")] public string text;
    [JsonProperty("ts")] public string ts;                    // Slack message ts; reply thread key
    [JsonProperty("thread_ts")] public string threadTs;
    [JsonProperty("mentions_me")] public bool mentionsMe;     // a DM, or the text mentions the user
    // Slack panel additions (PROJECT_CONTEXT 8.8); the hub may leave them out, so they default to "not sent".
    [JsonProperty("reply_count")] public int? replyCount;     // thread replies under this message
    [JsonProperty("edited")] public bool edited;
    [JsonProperty("is_me")] public bool isMe;                 // the user's own message (notify.message now carries those too)
    [JsonProperty("user_initials")] public string userInitials;
}

/// <summary>One conversation in notify.channels / GET /messages/channels (channel, private, dm or group-dm).</summary>
public class SlackChannel
{
    [JsonProperty("id")] public string id;
    [JsonProperty("name")] public string name;
    [JsonProperty("kind")] public string kind;                // channel | private | dm | group-dm
    [JsonProperty("unread")] public int unread;
    [JsonProperty("mentions")] public int mentions;
    [JsonProperty("last_ts")] public string lastTs;
    [JsonProperty("last_text")] public string lastText;
    [JsonProperty("topic")] public string topic;
    [JsonProperty("is_default")] public bool isDefault;
}

/// <summary>The conversation list with unread counts; sticky (re-sent on connect) and replaced whole on every change.</summary>
public class NotifyChannels
{
    [JsonProperty("channels")] public List<SlackChannel> channels = new List<SlackChannel>();
}

/// <summary>A reply Bit wrote but has NOT sent: show it as a Send / Cancel card. Send = POST /messages/drafts/{id}/send.</summary>
public class NotifyDraft
{
    [JsonProperty("draft_id")] public string draftId;
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;
    [JsonProperty("text")] public string text;
    [JsonProperty("thread_ts")] public string threadTs;
    [JsonProperty("reply_to")] public NotifyMessage replyTo;  // the message this answers, when there is one
    [JsonProperty("edited")] public bool edited;              // re-emitted after PATCH /messages/drafts/{id}
    [JsonProperty("attachments")] public List<DraftAttachment> attachments = new List<DraftAttachment>();  // sent with it (Addendum H)
}

/// <summary>A file on a draft (the hub keeps it and uploads it with the text on Send), e.g. a CAD design's STEP.</summary>
public class DraftAttachment
{
    [JsonProperty("id")] public string id;
    [JsonProperty("name")] public string name;    // rocket_v12.step
    [JsonProperty("title")] public string title;  // "CAD model (STEP)"
    [JsonProperty("kind")] public string kind;    // step | stl | drawing | parts | file
    [JsonProperty("bytes")] public long bytes;
    [JsonProperty("source")] public string source;
}

public class NotifyDraftCancelled
{
    [JsonProperty("draft_id")] public string draftId;
    [JsonProperty("reason")] public string reason;            // cancelled | superseded | expired
}

public class NotifySent
{
    [JsonProperty("draft_id")] public string draftId;         // the draft this came from, when there was one
    [JsonProperty("channel")] public string channel;
    [JsonProperty("channel_name")] public string channelName;
    [JsonProperty("text")] public string text;
    [JsonProperty("ts")] public string ts;
}

public class NotifyDigest
{
    [JsonProperty("count")] public int count;
    [JsonProperty("mentions")] public int mentions;
    [JsonProperty("line")] public string line;                // what Bit says (and the arrival card shows); "You have no new messages today." when count is 0
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
    public const string CadActionType = "cad.action";
    public const string UiModeType = "ui.mode";
    public const string PetStateType = "pet.state";
    public const string PetSayType = "pet.say";
    public const string UiHighlightType = "ui.highlight";
    public const string NotifyMessageType = "notify.message";
    public const string NotifyDraftType = "notify.draft";
    public const string NotifyDraftCancelledType = "notify.draft_cancelled";
    public const string NotifySentType = "notify.sent";
    public const string NotifyDigestType = "notify.digest";
    public const string NotifyChannelsType = "notify.channels";

    public static event Action<MlRunState> OnMlRunState;
    public static event Action<MlMetrics> OnMlMetrics;
    public static event Action<MlOverfitAlert> OnMlOverfitAlert;
    public static event Action<MlGraphReady> OnMlGraphReady;
    public static event Action<MlResultsReady> OnMlResultsReady;
    public static event Action<MlSampleUpdated> OnMlSampleUpdated;
    public static event Action<CadModelUpdated> OnCadModelUpdated;
    public static event Action<CadRegenFailed> OnCadRegenFailed;
    public static event Action<CadView> OnCadView;
    public static event Action<CadAction> OnCadAction;
    public static event Action<UiMode> OnUiMode;
    public static event Action<PetState> OnPetState;
    public static event Action<PetSay> OnPetSay;
    public static event Action<UiHighlight> OnUiHighlight;
    public static event Action<NotifyMessage> OnNotifyMessage;
    public static event Action<NotifyDraft> OnNotifyDraft;
    public static event Action<NotifyDraftCancelled> OnNotifyDraftCancelled;
    public static event Action<NotifySent> OnNotifySent;
    public static event Action<NotifyDigest> OnNotifyDigest;
    public static event Action<NotifyChannels> OnNotifyChannels;

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
            case CadActionType: Invoke(OnCadAction, payload); return true;
            case UiModeType: Invoke(OnUiMode, payload); return true;
            case PetStateType: Invoke(OnPetState, payload); return true;
            case PetSayType: Invoke(OnPetSay, payload); return true;
            case UiHighlightType: Invoke(OnUiHighlight, payload); return true;
            case NotifyMessageType: Invoke(OnNotifyMessage, payload); return true;
            case NotifyDraftType: Invoke(OnNotifyDraft, payload); return true;
            case NotifyDraftCancelledType: Invoke(OnNotifyDraftCancelled, payload); return true;
            case NotifySentType: Invoke(OnNotifySent, payload); return true;
            case NotifyDigestType: Invoke(OnNotifyDigest, payload); return true;
            case NotifyChannelsType: Invoke(OnNotifyChannels, payload); return true;
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
