using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Bit's arrival choreography: the ONE pet script that listens to DeskPresence.stateChanged (Main.unity, on PetRuntime).
    ///   Near   → the desk NavMesh follows DeskPresence.Table (GatorMrukAdapter.SyncDeskToPresence) and Bit hops onto it.
    ///   Seated → wait until he has landed (landTimeout) → POST /messages/digest once per sit (DigestTrigger.RequestDigest;
    ///            the hub answers notify.digest + pet.say) → wait for the digest line to be spoken (spokenTimeout, or
    ///            silentDigestWait when count == 0) → optionally wait for screen.status "sharing" → Workspace.Open("code")
    ///            (fallback "screens"), unless a mode is already open.
    ///   Away   → the sequence is cancelled and re-armed; Bit hops down and follows the user (GatorState.Following) or
    ///            wanders off (followOnAway).
    /// It also tells the hub which desk mode is open: workspace.mode {pet_id, mode} on Workspace.modeChanged and once after
    /// every (re)connect, mode "" when closed (hub PR A). Null-safe: without a DeskPresence (sandbox) it idles; without a
    /// Workspace the mode steps are skipped; without a hub the digest is skipped. One [Arrival] log line per step.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArrivalDirector : MonoBehaviour
    {
        [SerializeField, Tooltip("Bit. Auto-found in children / scene if empty.")]
        GatorController controller;
        [SerializeField, Tooltip("Bit's bridge (pet_id). Auto-found in children / scene if empty.")]
        GatorAgentBridge bridge;
        [SerializeField, Tooltip("Builds the desk NavMesh from DeskPresence.Table. Auto-found in children / scene if empty.")]
        GatorMrukAdapter adapter;
        [SerializeField, Tooltip("TTS to wait on (ITextToSpeech). Auto-found in children if empty.")]
        MonoBehaviour tts;

        [Header("Steps")]
        [SerializeField, Tooltip("Near: hop onto the desk the user walked up to.")]
        bool hopOnNear = true;
        [SerializeField, Tooltip("Seated: POST /messages/digest once per sit (hub live only).")]
        bool digestOnSeated = true;
        [SerializeField, Tooltip("Seated: open the code mode after the digest.")]
        bool openModeOnSeated = true;
        [SerializeField, Tooltip("Before opening the mode, wait (screenShareTimeout) for screen.status 'sharing' so the screen rises with a picture.")]
        bool waitForScreenShare = true;
        [SerializeField, Tooltip("Away: Following (hops down, trails the user). Off = WanderAway.")]
        bool followOnAway = true;
        [SerializeField, Tooltip("Send workspace.mode to the hub on mode changes and after every (re)connect.")]
        bool sendWorkspaceMode = true;

        [Header("Timing (s)")]
        [SerializeField] float landTimeout = 2.5f;
        [SerializeField, Tooltip("Seated while ForgeNet is still connecting: wait this long for it to go Live before skipping the digest.")]
        float hubWait = 3f;
        [SerializeField, Tooltip("notify.digest must arrive within this long after the POST.")]
        float digestReplyTimeout = 5f;
        [SerializeField, Tooltip("Longest wait for Bit to finish saying the digest line.")]
        float spokenTimeout = 12f;
        [SerializeField, Tooltip("Pause when the digest is empty (count == 0, nothing spoken).")]
        float silentDigestWait = 1.5f;
        [SerializeField] float screenShareTimeout = 3f;

        [Header("Modes")]
        [SerializeField] string codeModeId = "code";
        [SerializeField, Tooltip("Used while Core still calls the code mode 'screens'.")]
        string fallbackModeId = "screens";

        const string ScreenStatusType = "screen.status";
        const string WorkspaceModeType = "workspace.mode";

        DeskPresence _presence;
        Workspace _workspace;
        ITextToSpeech _tts;
        IDisposable _screenSub;
        Coroutine _sequence, _modeSend;
        float _nextWorkspaceCheck;
        bool _digestSentThisSit, _openedThisSit;
        bool _digestArrived, _spokeAfterDigest;
        int _digestCount;
        string _screenState;

        /// <summary>Last screen.status state from the hub: starting | sharing | stopped | error (null = none yet).</summary>
        public string ScreenState => _screenState;

        void Awake()
        {
            if (controller == null) controller = GetComponentInChildren<GatorController>(true);
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (bridge == null) bridge = controller != null ? controller.GetComponent<GatorAgentBridge>() : null;
            if (bridge == null) bridge = GetComponentInChildren<GatorAgentBridge>(true);
            if (bridge == null) bridge = FindAnyObjectByType<GatorAgentBridge>();
            if (adapter == null) adapter = GetComponentInChildren<GatorMrukAdapter>(true);
            if (adapter == null) adapter = FindAnyObjectByType<GatorMrukAdapter>();
        }

        void Start()   // after GatorAgentBridge.Awake, which may add a NullTextToSpeech
        {
            _tts = tts as ITextToSpeech;
            if (_tts == null) _tts = GetComponentInChildren<ITextToSpeech>(true);
            if (_tts == null && controller != null) _tts = controller.GetComponentInChildren<ITextToSpeech>(true);
            if (_tts != null) _tts.SpeechEnded += OnSpeechEnded;

            ForgeNet.StatusChanged += OnNetStatus;
            ForgeEvents.OnNotifyDigest += OnDigest;
            _screenSub = ForgeNet.On(ScreenStatusType, OnScreenStatus, replayLast: true);
            BindWorkspace();

            _presence = FindAnyObjectByType<DeskPresence>();
            if (_presence == null)
            {
                Log("No DeskPresence in this scene (sandbox): arrival choreography off.");
                return;
            }
            _presence.stateChanged.AddListener(OnDeskState);
            if (_presence.State != DeskState.Away) OnDeskState(_presence.State);   // e.g. forceSeated at load
        }

        void OnDestroy()
        {
            if (_presence != null) _presence.stateChanged.RemoveListener(OnDeskState);
            if (_workspace != null) _workspace.modeChanged.RemoveListener(OnModeChanged);
            if (_tts != null) _tts.SpeechEnded -= OnSpeechEnded;
            ForgeNet.StatusChanged -= OnNetStatus;
            ForgeEvents.OnNotifyDigest -= OnDigest;
            _screenSub?.Dispose();
            _screenSub = null;
        }

        // The Workspace is in Main from the start, but keep looking so a late one still gets its modes reported.
        void Update()
        {
            if (_workspace != null || Time.unscaledTime < _nextWorkspaceCheck) return;
            _nextWorkspaceCheck = Time.unscaledTime + 1f;
            BindWorkspace();
        }

        void BindWorkspace()
        {
            if (_workspace != null) return;
            Workspace w = PetWorkspace.Current;
            if (w == null) return;
            _workspace = w;
            w.modeChanged.AddListener(OnModeChanged);
            if (ForgeNet.Status == ForgeNetStatus.Live) SendWorkspaceMode(w.ActiveMode);
        }

        // ---------------------------------------------------------------- desk presence

        void OnDeskState(DeskState state)
        {
            switch (state)
            {
                case DeskState.Near:
                    CancelSequence();
                    if (hopOnNear) _sequence = StartCoroutine(NearRoutine());
                    break;
                case DeskState.Seated:
                    CancelSequence();
                    _sequence = StartCoroutine(SeatedRoutine());
                    break;
                case DeskState.Away:
                    CancelSequence();
                    _digestSentThisSit = _openedThisSit = false;
                    Leave();
                    break;
            }
        }

        IEnumerator NearRoutine()
        {
            Log("Near the desk.");
            yield return EnsureOnDesk();
            _sequence = null;
        }

        IEnumerator SeatedRoutine()
        {
            Log("Seated.");
            yield return EnsureOnDesk();
            yield return DigestStep();
            yield return OpenModeStep();
            _sequence = null;
        }

        // Hop onto the desk DeskPresence picked (the adapter builds / moves the desk NavMesh first), then wait for the landing.
        IEnumerator EnsureOnDesk()
        {
            if (controller == null) yield break;
            float deadline = Time.time + Mathf.Max(0.1f, landTimeout);
            bool asked = false;
            while (Time.time < deadline)
            {
                if (controller.OnDeskSurface)
                {
                    if (asked) Log("Landed on the desk.");
                    yield break;
                }
                if (!asked && !controller.IsHopping)
                {
                    if (adapter != null) adapter.SyncDeskToPresence();
                    GatorNavSurface desk = GatorNavSurface.Desk;
                    if (desk != null && desk.IsBuilt)
                    {
                        if (controller.State == GatorState.Following) controller.SetState(GatorState.Wandering);   // follow = floor
                        Log("Hop onto the desk.");
                        controller.HopToUserDesk();
                        asked = true;
                    }
                }
                yield return null;
            }
            Log(asked ? "Landing took too long; carrying on." : "No desk surface yet; carrying on without the hop.");
        }

        IEnumerator DigestStep()
        {
            if (!digestOnSeated || _digestSentThisSit) yield break;
            float deadline = Time.time + hubWait;
            while (ForgeNet.Status == ForgeNetStatus.Connecting && Time.time < deadline) yield return null;
            if (ForgeNet.Status != ForgeNetStatus.Live)
            {
                Log("Hub not live; digest skipped (asked later if it connects).");
                yield break;
            }

            _digestSentThisSit = true;
            _digestArrived = _spokeAfterDigest = false;
            _digestCount = 0;
            Log("POST /messages/digest.");
            DigestTrigger.RequestDigest();

            deadline = Time.time + digestReplyTimeout;
            while (!_digestArrived && Time.time < deadline) yield return null;
            if (!_digestArrived)
            {
                Log("No notify.digest within " + digestReplyTimeout.ToString("F0") + " s; moving on.");
                yield break;
            }
            if (_digestCount == 0)
            {
                Log("Digest: nothing new.");
                yield return new WaitForSeconds(silentDigestWait);
                yield break;
            }
            Log("Digest: " + _digestCount + " message(s); waiting for Bit to say it.");
            deadline = Time.time + spokenTimeout;
            while (!_spokeAfterDigest && Time.time < deadline) yield return null;
            Log(_spokeAfterDigest ? "Digest line spoken." : "Digest line not heard within " + spokenTimeout.ToString("F0") + " s; moving on.");
        }

        IEnumerator OpenModeStep()
        {
            if (!openModeOnSeated || _openedThisSit) yield break;
            if (!PetWorkspace.HasWorkspace)
            {
                Log("No Workspace in this scene; mode step skipped.");
                yield break;
            }
            string open = PetWorkspace.ActiveMode;
            if (open != null)
            {
                Log("Mode '" + open + "' is already open; leaving it.");
                _openedThisSit = true;
                yield break;
            }
            if (waitForScreenShare && ForgeNet.Status == ForgeNetStatus.Live && _screenState != "sharing")
            {
                Log("Waiting for screen.status 'sharing' (up to " + screenShareTimeout.ToString("F0") + " s).");
                float deadline = Time.time + screenShareTimeout;
                while (_screenState != "sharing" && Time.time < deadline) yield return null;
                if (_screenState != "sharing") Log("No screen share yet; opening anyway.");
            }
            if (_presence != null && _presence.State != DeskState.Seated) yield break;
            _openedThisSit = true;
            bool ok = PetWorkspace.Open(codeModeId, fallbackModeId);
            Log(ok ? "Opened mode '" + PetWorkspace.ActiveMode + "'." : "Could not open '" + codeModeId + "' (nor '" + fallbackModeId + "').");
        }

        void Leave()
        {
            if (controller == null) return;
            if (followOnAway)
            {
                Log("Away: following the user.");
                controller.SetState(GatorState.Following);   // Following hops to the floor when on the desk
            }
            else
            {
                Log("Away: wandering off.");
                controller.WanderAway();
            }
        }

        void CancelSequence()
        {
            if (_sequence != null) StopCoroutine(_sequence);
            _sequence = null;
        }

        // ---------------------------------------------------------------- hub

        void OnNetStatus(ForgeNetStatus status)
        {
            if (status != ForgeNetStatus.Live) return;
            // workspace.mode after hello (AgentClient sends it in its own StatusChanged handler; order between handlers isn't ours).
            if (_modeSend != null) StopCoroutine(_modeSend);
            _modeSend = StartCoroutine(SendModeSoon());
            // Sat down before the socket was up: ask for the digest now (the old DigestTrigger behaviour).
            if (_presence != null && _presence.State == DeskState.Seated && _sequence == null && digestOnSeated && !_digestSentThisSit)
                _sequence = StartCoroutine(SeatedRoutine());
        }

        IEnumerator SendModeSoon()
        {
            yield return new WaitForSeconds(0.25f);
            _modeSend = null;
            SendWorkspaceMode(_workspace != null ? _workspace.ActiveMode : null);
        }

        void OnModeChanged(string modeId)
        {
            Log("Workspace mode -> '" + (modeId ?? "") + "'.");
            SendWorkspaceMode(modeId);
        }

        void SendWorkspaceMode(string modeId)
        {
            if (!sendWorkspaceMode || _workspace == null || ForgeNet.Status != ForgeNetStatus.Live) return;
            ForgeNet.Send(WorkspaceModeType, new JObject { ["pet_id"] = PetId, ["mode"] = modeId ?? "" });
        }

        void OnDigest(NotifyDigest d)
        {
            if (d == null) return;
            _digestArrived = true;
            _digestCount = d.count;
        }

        void OnSpeechEnded()
        {
            if (_digestArrived) _spokeAfterDigest = true;
        }

        void OnScreenStatus(JToken payload)
        {
            string state = payload is JObject o && o["state"] != null && o["state"].Type == JTokenType.String ? (string)o["state"] : null;
            if (state == _screenState) return;
            _screenState = state;
            Log("screen.status '" + (state ?? "") + "'.");
        }

        string PetId => bridge != null ? bridge.PetId : "bit";

        void Log(string line) => Debug.Log("[Arrival] " + line, this);
    }
}
