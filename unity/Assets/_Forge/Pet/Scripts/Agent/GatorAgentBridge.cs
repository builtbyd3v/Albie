using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Glue between the event source (MockEventSource or AgentClient, picked by pet_config.json)
    /// and the gator body. Implements the event → GatorState table of docs/PET_AGENT_CONTRACT.md.
    /// Only events for this gator's pet_id are handled (several gators share one socket, see GatorCrew); a crew
    /// gator uses Bit's source (UseSharedSource) instead of opening its own.
    /// </summary>
    public class GatorAgentBridge : MonoBehaviour
    {
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        GatorController controller;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        GatorExpressions expressions;
        [SerializeField, Tooltip("Auto-found in children / scene if empty.")]
        GatorCaption caption;
        [SerializeField, Tooltip("Optional TTS (must implement ITextToSpeech). Falls back to a runtime NullTextToSpeech.")]
        MonoBehaviour tts;
        [SerializeField, Tooltip("Enter GatorState.Intro on Start and go to Wandering when the controller reports IntroComplete.")]
        bool playIntroOnStart = true;
        [SerializeField, Tooltip("When TTS finishes and the gator is still Answering, return to Wandering.")]
        bool wanderAfterSpeech = true;

        /// <summary>True when pet_config.json selected the live hub (set once the config has loaded).</summary>
        public static bool EventSourceIsHub { get; private set; }

        public IPetEventSource Source { get; private set; }
        public PetConfig Config { get; private set; }
        /// <summary>This gator's pet_id: the config's (Bit) or the crew type id.</summary>
        public string PetId => _crewPetId ?? (Config != null ? Config.petId : "bit");
        /// <summary>An ask is awaiting its answer or a code task is running.</summary>
        public bool TaskInProgress => _awaitingAnswer || _taskRunning;
        /// <summary>Working on something, incl. turns the hub started on its own (a crew task Bit assigned).</summary>
        public bool Busy => TaskInProgress || _hubBusy;

        ITextToSpeech _tts;
        bool _awaitingAnswer, _taskRunning, _hubBusy;
        string _pendingAsk, _pendingMode;
        string _crewPetId;                   // set for crew gators: events / asks use this pet_id
        GatorAgentBridge _sharedFrom;        // crew: Bit's bridge, whose Source we borrow (never connect / disconnect it)
        readonly HashSet<string> _unknownLogged = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => EventSourceIsHub = false;

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<GatorController>();
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (expressions == null) expressions = GetComponentInParent<GatorExpressions>();
            if (expressions == null) expressions = FindAnyObjectByType<GatorExpressions>();
            if (caption == null) caption = GetComponentInChildren<GatorCaption>(true);
            if (caption == null) caption = FindAnyObjectByType<GatorCaption>();

            _tts = tts != null ? tts as ITextToSpeech : null;   // Unity null: a crew copy's voice child was removed
            if (_tts == null) _tts = GetComponent<ITextToSpeech>();
            if (_tts == null) _tts = GetComponentInChildren<ITextToSpeech>(true);   // e.g. StreamedPcmTextToSpeech on a "Voice" child
            if (_tts == null) _tts = gameObject.AddComponent<NullTextToSpeech>();
            _tts.SpeechEnded += OnSpeechEnded;
        }

        void Start()
        {
            if (controller != null)
            {
                controller.IntroComplete += OnIntroComplete;
                if (playIntroOnStart) controller.SetState(GatorState.Intro);
            }
            if (_sharedFrom == null) StartCoroutine(PetConfig.LoadAsync(OnConfigLoaded));
        }

        /// <summary>Crew gator: use <paramref name="lead"/>'s event source with its own pet_id. Call before Start.</summary>
        public void UseSharedSource(string petId, GatorAgentBridge lead)
        {
            _crewPetId = petId;
            _sharedFrom = lead;
            playIntroOnStart = true;
        }

        // Crew: Bit's source exists once his config has loaded; attach then (polled, cheap).
        void Update()
        {
            if (_sharedFrom == null || Source != null || _sharedFrom.Source == null) return;
            Config = _sharedFrom.Config;
            Source = _sharedFrom.Source;
            Source.EventReceived += OnEvent;
            if (_pendingAsk != null) { var t = _pendingAsk; _pendingAsk = null; Ask(t, _pendingMode); }
        }

        void OnConfigLoaded(PetConfig cfg)
        {
            Config = cfg ?? new PetConfig();
            EventSourceIsHub = Config.IsHub;
            Debug.Log("[GatorAgentBridge] " + Config);

            var hub = GetComponent<AgentClient>();
            var mock = GetComponent<MockEventSource>();
            if (EventSourceIsHub)
            {
                if (hub == null) hub = gameObject.AddComponent<AgentClient>();
                if (mock != null) mock.Disconnect();
                Source = hub;
            }
            else
            {
                if (mock == null) mock = gameObject.AddComponent<MockEventSource>();
                if (hub != null) { hub.Disconnect(); hub.enabled = false; }
                Source = mock;
            }

            var hubStt = GetComponent<HubSpeechToText>();
            if (hubStt != null) hubStt.PetId = Config.petId;

            Source.EventReceived += OnEvent;
            Source.Connect(Config);

            if (_pendingAsk != null) { var t = _pendingAsk; _pendingAsk = null; Ask(t, _pendingMode); }
        }

        /// <summary>Sends agent.ask (cancelling any in-flight ask/task first). mode: "auto" | "qa" | "code".</summary>
        public void Ask(string text, string mode = "auto")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Source == null) { _pendingAsk = text; _pendingMode = mode; return; } // config still loading
            string petId = PetId;
            if (TaskInProgress) Source.Send("agent.cancel", new JObject { ["pet_id"] = petId });
            _taskRunning = false;
            _awaitingAnswer = true;
            Source.Send("agent.ask", new JObject
            {
                ["pet_id"] = petId, ["project_id"] = Config.projectId, ["text"] = text, ["mode"] = mode ?? "auto",
            });
        }

        // ---------------- event → state table ----------------

        void OnEvent(PetEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.type)) return;
            string pid = e.GetString("pet_id");
            if (!string.IsNullOrEmpty(pid) && pid != PetId) return;   // another gator's event
            TrackBusy(e);
            switch (e.type)
            {
                case "stt.partial":
                case "stt.final":
                    // Don't drag the gator back to Listening once PushToTalk moved on to Thinking/Answering.
                    if (State == GatorState.Listening || State == GatorState.Wandering || State == GatorState.Following)
                        SetStateIfChanged(GatorState.Listening);
                    Caption(e.GetString("text"), e.type == "stt.partial");
                    break;

                case "agent.thinking":
                    SetStateIfChanged(GatorState.Thinking);
                    ApplyMood(Mood.Squint);
                    break;

                case "agent.partial_answer":
                    SetStateIfChanged(GatorState.Answering);
                    Caption(e.GetString("text"), true);
                    break;

                case "agent.answer_done":
                    _awaitingAnswer = false;
                    Answer(e.GetString("text"));
                    break;

                case "pet.say":
                    Answer(e.GetString("text"));
                    break;

                case "agent.task_started":
                    _taskRunning = true;
                    SetStateIfChanged(GatorState.Working);
                    Caption(e.GetString("title", "Working on it..."), true);
                    break;

                case "agent.task_progress":
                    _taskRunning = true;
                    SetStateIfChanged(GatorState.Working);
                    string progress = e.GetString("text");
                    if (e.Has("pct")) progress += "  " + Mathf.RoundToInt(e.GetFloat("pct")) + "%";
                    Caption(progress, true);
                    break;

                case "agent.task_complete":
                    _taskRunning = false; _awaitingAnswer = false;
                    if (controller != null) controller.SetState(GatorState.TaskComplete); // controller spins, then Wandering
                    Caption(e.GetString("summary", "Done!"), false);
                    break;

                case "agent.error":
                    _taskRunning = false; _awaitingAnswer = false;
                    if (_tts != null) _tts.Stop();
                    if (controller != null) controller.SetState(GatorState.Error); // controller plays Fear + Sad on entry
                    ApplyMood(Mood.Sad);
                    Caption(e.GetString("message", "Something went wrong."), false);
                    break;

                case "pet.state":
                    HandlePetState(e.GetString("mode"));
                    break;

                // Voice mode: the hub streams speech (StreamedPcmTextToSpeech plays it and raises SpeechStarted/Ended).
                case "tts.start":
                    SetStateIfChanged(GatorState.Answering);
                    break;
                case "tts.end":
                case "tts.stop":
                case "voice.ready":
                    break;

                case "agent.tool_call":   // the voice model handed work to a tool (delegate = the Claude brain)
                    SetStateIfChanged(GatorState.Thinking);
                    ApplyMood(Mood.Squint);
                    break;

                default:
                    if (_unknownLogged.Add(e.type)) Debug.Log("[GatorAgentBridge] Ignoring event type '" + e.type + "' (logged once).");
                    break;
            }
        }

        void TrackBusy(PetEvent e)
        {
            switch (e.type)
            {
                case "agent.thinking": case "agent.partial_answer": case "agent.task_started": case "agent.task_progress":
                case "agent.tool_call":
                    _hubBusy = true; break;
                case "agent.answer_done": case "agent.task_complete": case "agent.error":
                    _hubBusy = false; break;
                case "pet.state":
                    string mode = e.GetString("mode");
                    if (mode == "idle") _hubBusy = false;
                    else if (mode == "thinking" || mode == "working") _hubBusy = true;
                    break;
            }
        }

        void HandlePetState(string mode)
        {
            switch (mode)
            {
                case "follow": SetStateIfChanged(GatorState.Following); break;
                case "idle": SetStateIfChanged(GatorState.Wandering); break;
                case "listening": SetStateIfChanged(GatorState.Listening); break;
                case "thinking": SetStateIfChanged(GatorState.Thinking); ApplyMood(Mood.Squint); break;
                case "working": SetStateIfChanged(GatorState.Working); break;
                case "speaking": SetStateIfChanged(GatorState.Answering); break;
                default:
                    if (_unknownLogged.Add("pet.state:" + mode)) Debug.Log("[GatorAgentBridge] Unknown pet.state mode '" + mode + "'.");
                    break;
            }
        }

        void Answer(string text)
        {
            SetStateIfChanged(GatorState.Answering); // controller plays Bounce + Happy on entry
            Caption(text, false);
            ApplyMood(Mood.Happy);
            if (_tts != null && !string.IsNullOrEmpty(text)) _tts.Speak(text);
        }

        // ---------------- helpers ----------------

        GatorState State => controller != null ? controller.State : GatorState.Wandering;

        void SetStateIfChanged(GatorState s)
        {
            if (controller != null && controller.State != s) controller.SetState(s);
        }

        void ApplyMood(Mood m)
        {
            if (expressions != null) expressions.SetMood(m);
        }

        void Caption(string text, bool partial)
        {
            if (caption != null && !string.IsNullOrEmpty(text)) caption.SetText(text, partial);
        }

        void OnIntroComplete()
        {
            if (controller != null && controller.State == GatorState.Intro) controller.SetState(GatorState.Wandering);
        }

        void OnSpeechEnded()
        {
            if (wanderAfterSpeech && controller != null && controller.State == GatorState.Answering)
                controller.SetState(GatorState.Wandering);
        }

        void OnDestroy()
        {
            if (controller != null) controller.IntroComplete -= OnIntroComplete;
            if (_tts != null) _tts.SpeechEnded -= OnSpeechEnded;
            if (Source != null)
            {
                Source.EventReceived -= OnEvent;
                if (_sharedFrom == null) Source.Disconnect();   // a crew gator only borrowed Bit's
            }
        }
    }
}
