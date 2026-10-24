using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Conversation follow-up: when the gator finishes speaking an answer it keeps listening hands-free for
    /// listenSeconds (PushToTalk.BeginHandsFree), so the user can just reply. If nobody speaks (or the reply was empty)
    /// it turns around and wanders off to idle (GatorController.WanderAway), which reads clearly as "done".
    /// Skipped while the brain is still working (Thinking/Working: the spoken line was only "on it"), after errors,
    /// when a session is already open (barge-in, Space held), and with the typed STT field (it would just sit open).
    /// If the gator starts speaking again during the window, the window closes without asking anything.
    /// Voice barge-in: while the gator speaks, the mic is monitored (HubSpeechToText.StartMonitoring, nothing sent).
    /// Speech clearly louder than the gator's own echo opens a hands-free turn and cuts the gator off; the hub
    /// stops Grok on stt.start and the monitored pre-roll keeps the first words.
    /// Look-to-talk: the follow-up window only watches the mic (nothing sent). Speech opens a gated turn
    /// (stt.start {require_name}): the hub answers it if it names the gator ("Albie, ...") or, judging the transcript,
    /// it was said to him (hub/agent/addressed.py), so teammates talking at the desk are not answered.
    /// Cutting the gator off needs facing it. Space / A / the whistle always open a turn.
    /// WakeListener reads UserFacing too: facing the gator answers any hands-free turn, not only in this window.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorFollowUp : MonoBehaviour
    {
        [SerializeField, Tooltip("TTS to follow (ITextToSpeech). Auto-found in children / parents if empty.")]
        MonoBehaviour tts;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        PushToTalk pushToTalk;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        GatorController controller;
        [SerializeField, Tooltip("How long to keep listening for a reply after an answer (s).")]
        float listenSeconds = 15f;
        [SerializeField, Tooltip("Pause after speech ends before the mic opens (s): skips the speaker's tail.")]
        float startDelay = 0.35f;
        [SerializeField, Tooltip("Nobody replied: walk off and idle away from the user.")]
        bool wanderAwayWhenDone = true;

        [Header("Voice barge-in")]
        [SerializeField, Tooltip("Talking over the gator cuts it off, no button needed (mic permission must already be granted).")]
        bool voiceBargeIn = true;
        // Defaults from an AirPods run: speech 0.05-0.3, noise floor 0.006, the gator's echo ~0.01 x its output level.
        [SerializeField, Tooltip("Mic RMS (0..1) that can count as the user talking over the gator.")]
        float bargeLevel = 0.03f;
        [SerializeField, Tooltip("Mic RMS must also be above this share of the gator's output level (rejects its own echo). Raise it if the gator cuts itself off.")]
        float echoShare = 0.2f;
        [SerializeField, Tooltip("Seconds of speech before it counts; quiet gaps between syllables count down instead of resetting.")]
        float bargeSeconds = 0.3f;
        // A one-word "Stop!" lasts ~0.3 s, too short for bargeSeconds at normal level, but it is loud (0.15-0.25).
        [SerializeField, Tooltip("Mic RMS that counts as a short, loud word (\"Stop!\").")]
        float loudLevel = 0.1f;
        [SerializeField, Tooltip("Seconds above loudLevel that are enough on their own.")]
        float loudSeconds = 0.15f;

        [Header("Look to talk")]
        [SerializeField, Tooltip("Hands-free speech only counts while the user faces the gator, or names it (\"Albie, ...\"). Buttons and the whistle always work.")]
        bool lookToTalk = true;
        [SerializeField, Tooltip("Max angle (degrees) between where the user looks and the gator.")]
        float lookAngle = 25f;

        ITextToSpeech _tts;
        ITalkLevel _talk;
        HubSpeechToText _monitor;
        bool _following, _subscribed, _speaking, _attending;
        float _startAt = -1f, _speechFor, _loudFor, _attendUntil;

        void Awake()
        {
            if (pushToTalk == null) pushToTalk = GetComponent<PushToTalk>();
            if (pushToTalk == null) pushToTalk = FindAnyObjectByType<PushToTalk>();
            if (controller == null) controller = GetComponentInParent<GatorController>();
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
        }

        void Start()   // after GatorAgentBridge.Awake, which may add the runtime NullTextToSpeech
        {
            _tts = tts as ITextToSpeech;
            if (_tts == null) _tts = GetComponentInChildren<ITextToSpeech>(true);
            if (_tts == null) _tts = GetComponentInParent<ITextToSpeech>();
            if (_tts == null || pushToTalk == null || controller == null)
            {
                Debug.LogWarning("[GatorFollowUp] Needs an ITextToSpeech, PushToTalk and GatorController; disabled.", this);
                enabled = false;
                return;
            }
            _talk = _tts as ITalkLevel;
            _tts.SpeechStarted += OnSpeechStarted;
            _tts.SpeechEnded += OnSpeechEnded;
            pushToTalk.SessionEnded += OnSessionEnded;
            _subscribed = true;
        }

        void OnDestroy()
        {
            if (!_subscribed) return;
            if (_tts != null) { _tts.SpeechStarted -= OnSpeechStarted; _tts.SpeechEnded -= OnSpeechEnded; }
            if (pushToTalk != null) pushToTalk.SessionEnded -= OnSessionEnded;
        }

        void OnSpeechStarted()
        {
            _startAt = -1f;
            _speaking = true;
            _attending = false;                       // talking over our own window: close it, ask nothing
            if (_following)
            {
                _following = false;
                pushToTalk.CancelSession();
            }
            if (voiceBargeIn) StartMonitor();
        }

        void OnSpeechEnded()
        {
            _speaking = false;
            if (pushToTalk.SessionActive) { StopMonitor(); return; }   // barge-in / Space: someone is already talking
            GatorState s = controller.State;
            if (s == GatorState.Answering || s == GatorState.Wandering || s == GatorState.TaskComplete)
                _startAt = Time.time + startDelay;    // the mic keeps running into the follow-up window (no restart)
            else StopMonitor();
        }

        void Update()
        {
            if (_monitor != null && _monitor.IsMonitoring && (_speaking || _attending)) CheckSpeech();
            if (_attending && Time.time >= _attendUntil)
            {
                _attending = false;                   // nobody replied
                StopMonitor();
                if (wanderAwayWhenDone && !pushToTalk.SessionActive) controller.WanderAway();
            }
            if (_startAt < 0f || Time.time < _startAt) return;
            _startAt = -1f;
            GatorState s = controller.State;
            if (pushToTalk.SessionActive || s == GatorState.Thinking || s == GatorState.Working || s == GatorState.Error) { StopMonitor(); return; }
            if (lookToTalk && StartMonitor()) { _attending = true; _attendUntil = Time.time + listenSeconds; }
            else if (pushToTalk.CanListenHandsFree && pushToTalk.BeginHandsFree(listenSeconds)) _following = true;
            else if (wanderAwayWhenDone) controller.WanderAway();
        }

        bool StartMonitor()
        {
            if (pushToTalk.SessionActive || !pushToTalk.CanListenHandsFree || !(pushToTalk.Provider is HubSpeechToText hub)) return false;
            _monitor = hub;
            _speechFor = _loudFor = 0f;
            hub.StartMonitoring();                    // no-op if the mic is already running
            return hub.IsMonitoring;
        }

        void StopMonitor()
        {
            if (_monitor != null) { _monitor.StopMonitoring(); _monitor = null; }
        }

        /// <summary>The user looks at the gator (within lookAngle). False with look-to-talk off or no camera.</summary>
        public bool UserFacing
        {
            get
            {
                var cam = Camera.main;
                if (!lookToTalk || cam == null || controller == null) return false;
                Vector3 to = controller.transform.position - cam.transform.position;
                return to.sqrMagnitude < 0.01f || Vector3.Angle(cam.transform.forward, to) <= lookAngle;
            }
        }

        bool Facing() => !lookToTalk || Camera.main == null || UserFacing;   // no way to tell: every turn counts

        void CheckSpeech()
        {
            bool facing = Facing();
            if (_speaking && !facing) { _speechFor = _loudFor = 0f; return; }   // cutting the gator off needs facing it
            float echo = _speaking && _talk != null ? _talk.Level * echoShare : 0f;
            float level = _monitor.Level, dt = Time.deltaTime;
            _speechFor = level >= bargeLevel && level >= echo ? _speechFor + dt : Mathf.Max(0f, _speechFor - dt);
            _loudFor = level >= loudLevel && level >= echo ? _loudFor + dt : Mathf.Max(0f, _loudFor - dt);
            if (_speechFor < bargeSeconds && _loudFor < loudSeconds) return;
            var hub = _monitor;
            bool cutIn = _speaking, reply = _attending;
            _monitor = null;
            _attending = false;
            hub.RequireName = reply || !facing;       // a reply is judged by the hub (said to Albie?); looking elsewhere needs "Albie"
            // Open the turn first (stt.start sends the pre-roll; SessionActive keeps OnSpeechEnded from reopening), then go quiet locally.
            if (pushToTalk.BeginHandsFree())
            {
                if (cutIn) _tts.Stop();
                _following = reply;
            }
            else { hub.RequireName = false; hub.StopMonitoring(); }
        }

        void OnSessionEnded(bool answered)
        {
            if (!_following) return;
            _following = false;
            if (!answered && wanderAwayWhenDone && !pushToTalk.SessionActive) controller.WanderAway();
        }
    }
}
