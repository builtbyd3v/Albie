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
        [SerializeField, Tooltip("Mic RMS (0..1) that can count as the user talking over the gator.")]
        float bargeLevel = 0.06f;
        [SerializeField, Tooltip("Mic RMS must also be this many times the gator's output level (rejects its own echo). Raise it if the gator cuts itself off.")]
        float echoRatio = 1.5f;
        [SerializeField, Tooltip("Seconds of loud speech before it counts (ignores bumps and short noises).")]
        float bargeSeconds = 0.3f;

        ITextToSpeech _tts;
        ITalkLevel _talk;
        HubSpeechToText _monitor;
        bool _following, _subscribed;
        float _startAt = -1f, _loudFor;

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
            if (_following)
            {
                _following = false;                   // talking over our own window: close it, ask nothing
                pushToTalk.CancelSession();
            }
            if (voiceBargeIn && !pushToTalk.SessionActive && pushToTalk.CanListenHandsFree && pushToTalk.Provider is HubSpeechToText hub)
            {
                _monitor = hub;
                _loudFor = 0f;
                hub.StartMonitoring();
            }
        }

        void OnSpeechEnded()
        {
            if (_monitor != null) { _monitor.StopMonitoring(); _monitor = null; }
            if (pushToTalk.SessionActive) return;     // barge-in / Space: someone is already talking
            GatorState s = controller.State;
            if (s == GatorState.Answering || s == GatorState.Wandering || s == GatorState.TaskComplete)
                _startAt = Time.time + startDelay;
        }

        void Update()
        {
            if (_monitor != null && _monitor.IsMonitoring) CheckBargeIn();
            if (_startAt < 0f || Time.time < _startAt) return;
            _startAt = -1f;
            GatorState s = controller.State;
            if (pushToTalk.SessionActive || s == GatorState.Thinking || s == GatorState.Working || s == GatorState.Error) return;
            if (pushToTalk.CanListenHandsFree && pushToTalk.BeginHandsFree(listenSeconds)) _following = true;
            else if (wanderAwayWhenDone) controller.WanderAway();
        }

        void CheckBargeIn()
        {
            float echo = _talk != null ? _talk.Level * echoRatio : 0f;
            _loudFor = _monitor.Level >= bargeLevel && _monitor.Level >= echo ? _loudFor + Time.deltaTime : 0f;
            if (_loudFor < bargeSeconds) return;
            var hub = _monitor;
            _monitor = null;
            // Open the turn first (stt.start sends the pre-roll; SessionActive keeps OnSpeechEnded from reopening), then go quiet locally.
            if (pushToTalk.BeginHandsFree()) _tts.Stop();
            else hub.StopMonitoring();
        }

        void OnSessionEnded(bool answered)
        {
            if (!_following) return;
            _following = false;
            if (!answered && wanderAwayWhenDone && !pushToTalk.SessionActive) controller.WanderAway();
        }
    }
}
