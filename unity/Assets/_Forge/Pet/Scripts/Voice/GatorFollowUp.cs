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
    /// Look-to-talk: the follow-up window only watches the mic (nothing sent). Speech while facing the gator opens a
    /// normal turn; speech while looking elsewhere opens a gated turn (stt.start {require_name}) that the hub answers
    /// only if it names the gator ("Albie, ..."), so teammates talking at the desk are not answered. Facing = within
    /// lookAngle of its body (plus its apparent size), or within the last lookGrace seconds.
    /// Cutting the gator off: facing it stops it at once; otherwise only "Albie, stop" does (the hub decides).
    /// Space / A / the whistle always open a turn.
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
        [SerializeField, Tooltip("Max angle (degrees) between where the user looks and the gator's body (its apparent size is added on top).")]
        float lookAngle = 40f;   // 25 never fired on the Quest (live 18:3x: facing=False every turn)
        [SerializeField, Tooltip("Looking at the gator this recently (s) still counts: you glance at it, then turn to speak.")]
        float lookGrace = 1.5f;
        [SerializeField, Tooltip("Facing the gator counts as saying its name. Off: every hands-free sentence needs \"Albie\" (at the desk it sits in front of the screen, so looking at the screen read as looking at it). Pair with the hub's VOICE_FACING=1.")]
        bool lookCountsAsName;

        ITextToSpeech _tts;
        ITalkLevel _talk;
        HubSpeechToText _monitor;
        Renderer _body;
        bool _following, _subscribed, _speaking, _attending;
        float _startAt = -1f, _speechFor, _loudFor, _attendUntil, _lastLookAt = -10f, _retryAt = -1f;

        /// <summary>Angle (degrees) to the gator's body at the last check, and the cone it had to fall in: for logs.</summary>
        public float LastLookAngle { get; private set; }
        public float LastLookLimit { get; private set; }

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
            _body = controller.GetComponentInChildren<SkinnedMeshRenderer>();   // aim at the body, not the feet
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
            // Live 2026-10-25 (5x): Microphone.Start right after the WakeListener let go failed with error 80, and
            // that reply could not be stopped by voice. One retry a moment later.
            if (voiceBargeIn && !StartMonitor()) _retryAt = Time.time + 0.3f;
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
            if (lookToTalk && FacingNow()) _lastLookAt = Time.time;
            if (_retryAt >= 0f && Time.time >= _retryAt)
            {
                _retryAt = -1f;
                if (_speaking && voiceBargeIn) StartMonitor();
            }
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
            else if (pushToTalk.CanListenHandsFree && GateNextTurn() && pushToTalk.BeginHandsFree(listenSeconds)) _following = true;
            else if (wanderAwayWhenDone) controller.WanderAway();
        }

        // The no-monitor fallback (mic busy, other STT) is a follow-up too: gated like CheckSpeech's turns. Live 18:31 it
        // went out ungated and "He's not into that, bro." was answered.
        bool GateNextTurn()
        {
            if (pushToTalk.Provider is HubSpeechToText hub) hub.RequireName = !(lookCountsAsName && Facing());
            return true;
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

        /// <summary>The user looks at the gator, or did within lookGrace. False with look-to-talk off or no camera.</summary>
        public bool UserFacing => lookToTalk && (FacingNow() || Time.time - _lastLookAt <= lookGrace);

        // Within lookAngle of the gator's body, plus half its apparent size, so it works across the desk and up close.
        bool FacingNow()
        {
            var cam = Camera.main;
            if (cam == null || controller == null) return false;
            Vector3 target = controller.transform.position;
            float radius = 0.1f;
            if (_body != null) { Bounds b = _body.bounds; target = b.center; radius = b.extents.magnitude; }
            Vector3 to = target - cam.transform.position;
            float dist = to.magnitude;
            if (dist < 0.1f) return true;
            LastLookAngle = Vector3.Angle(cam.transform.forward, to);
            LastLookLimit = lookAngle + Mathf.Rad2Deg * Mathf.Atan2(radius, dist);
            return LastLookAngle <= LastLookLimit;
        }

        bool Facing() => !lookToTalk || Camera.main == null || UserFacing;   // no way to tell: every turn counts

        void CheckSpeech()
        {
            bool facing = lookCountsAsName && Facing();
            float echo = _speaking && _talk != null ? _talk.Level * echoShare : 0f;
            float level = _monitor.Level, dt = Time.deltaTime;
            _speechFor = level >= bargeLevel && level >= echo ? _speechFor + dt : Mathf.Max(0f, _speechFor - dt);
            _loudFor = level >= loudLevel && level >= echo ? _loudFor + dt : Mathf.Max(0f, _loudFor - dt);
            if (_speechFor < bargeSeconds && _loudFor < loudSeconds) return;
            var hub = _monitor;
            bool cutIn = _speaking, reply = _attending;
            _monitor = null;
            _attending = false;
            hub.RequireName = !facing;                // looking elsewhere: the hub answers (or stops him) only on "Albie, ..."
            // Open the turn first (stt.start sends the pre-roll; SessionActive keeps OnSpeechEnded from reopening), then go quiet locally.
            if (pushToTalk.BeginHandsFree())
            {
                if (cutIn && facing) _tts.Stop();     // not facing: keep talking; the hub stops him on "Albie, stop"
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
