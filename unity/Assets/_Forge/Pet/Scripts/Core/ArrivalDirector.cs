using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Bit's arrival choreography: the ONE pet script that listens to DeskPresence.stateChanged (Main.unity, on PetRuntime).
    ///   Near / Seated → the desk NavMesh follows DeskPresence.Table (GatorMrukAdapter.SyncDeskToPresence) and Bit hops onto it.
    ///   First arrival of a session (re-armed after rearmAwaySeconds Away; stepping away for a moment or walking round the
    ///   desk doesn't count) → the dock is held back (Workspace.HoldDock), Bit walks to the greeting spot (in front of the
    ///   seated user, a little to their left, outside the DeskDock keep-out) and faces them (GatorController.StationAt),
    ///   POST /messages/digest, and the greeting card shows the digest line (which the hub also speaks; count 0 =
    ///   "You have no new messages today.", hub never live = "Hub offline.") with a START pill beside it (ArrivalGreeting).
    ///   START → card + ring fold, the dock comes in (frame slides in, rings plop on), Bit walks over to the right of the desk, and after
    ///   the dock has landed Workspace.Open("code") (fallback "screens"). No timeout: the card stays up until START or Away.
    ///   Later arrivals → just the hop; the dock shows on Near as Core always did.
    ///   Away → everything cancels and folds, the hold is released; Bit hops down and follows (GatorState.Following) or
    ///   wanders off (followOnAway).
    /// It also tells the hub which desk mode is open: workspace.mode {pet_id, mode} on Workspace.modeChanged and once after
    /// every (re)connect, mode "" when closed. Null-safe: without a DeskPresence (sandbox) it idles; without a Workspace the
    /// dock / mode steps are skipped; without a hub the card says so. One [Arrival] log line per step; every step is a toggle.
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
        [SerializeField, Tooltip("The greeting card + START pill (built into PetRuntime by the editor). Auto-found in children if empty.")]
        ArrivalGreeting greeting;

        [Header("Steps")]
        [SerializeField, Tooltip("Near / Seated: hop onto the desk the user walked up to.")]
        bool hopOnNear = true;
        [SerializeField, Tooltip("First arrival: hold the dock, greeting spot, digest, card + START.")]
        bool greetOnFirstArrival = true;
        [SerializeField, Tooltip("START: open the code mode once the dock has come in.")]
        bool openModeOnStart = true;
        [SerializeField, Tooltip("Away: Following (hops down, trails the user). Off = WanderAway.")]
        bool followOnAway = true;
        [SerializeField, Tooltip("Send workspace.mode to the hub on mode changes and after every (re)connect.")]
        bool sendWorkspaceMode = true;

        [Header("Timing (s)")]
        [SerializeField] float landTimeout = 2.5f;
        [SerializeField, Tooltip("Arrived while ForgeNet is still connecting: wait this long for it to go Live before the card says Hub offline.")]
        float hubWait = 3f;
        [SerializeField, Tooltip("notify.digest must arrive within this long after the POST.")]
        float digestReplyTimeout = 5f;
        [SerializeField, Tooltip("Away this long re-arms the first-arrival greeting.")]
        float rearmAwaySeconds = 120f;
        [SerializeField, Tooltip("START: the dock's entrance (slide + ring plops) takes about this long; the code mode opens after it.")]
        float dockEntranceSeconds = 1.3f;

        [Header("Places (desk frame, m: x right of the front-edge midpoint, z into the desk)")]
        [SerializeField, Tooltip("Greeting spot: in front of the seated user, to their left of the laptop / dock keep-out. Clamped to the desk; nudged further left while inside the keep-out.")]
        Vector3 greetingSpot = new Vector3(-0.42f, 0f, 0.18f);
        [SerializeField, Tooltip("Where Bit wanders off to after START (his side of the desk). Clamped to the desk, keep-out safe.")]
        Vector3 afterStartSpot = new Vector3(0.35f, 0f, 0.30f);

        [Header("Modes")]
        [SerializeField] string codeModeId = "code";
        [SerializeField, Tooltip("Used while Core still calls the code mode 'screens'.")]
        string fallbackModeId = "screens";

        const string WorkspaceModeType = "workspace.mode";
        const string HubOfflineLine = "Hub offline.";

        DeskPresence _presence;
        Workspace _workspace;
        Coroutine _sequence, _modeSend, _openAfterDock;
        float _nextWorkspaceCheck, _awaySince = -1f;
        bool _greeted, _greetingUp, _digestSent, _digestArrived;
        string _digestLine;
        GatorCaption _caption;

        /// <summary>The first-arrival greeting has played this session (until rearmAwaySeconds Away).</summary>
        public bool Greeted => _greeted;

        void Awake()
        {
            if (controller == null) controller = GetComponentInChildren<GatorController>(true);
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (bridge == null) bridge = controller != null ? controller.GetComponent<GatorAgentBridge>() : null;
            if (bridge == null) bridge = GetComponentInChildren<GatorAgentBridge>(true);
            if (bridge == null) bridge = FindAnyObjectByType<GatorAgentBridge>();
            if (adapter == null) adapter = GetComponentInChildren<GatorMrukAdapter>(true);
            if (adapter == null) adapter = FindAnyObjectByType<GatorMrukAdapter>();
            if (greeting == null) greeting = GetComponentInChildren<ArrivalGreeting>(true);
        }

        void Start()
        {
            ForgeNet.StatusChanged += OnNetStatus;
            ForgeEvents.OnNotifyDigest += OnDigest;
            if (greeting != null) greeting.StartPressed += OnStartPressed;
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
            if (greeting != null) greeting.StartPressed -= OnStartPressed;
            ForgeNet.StatusChanged -= OnNetStatus;
            ForgeEvents.OnNotifyDigest -= OnDigest;
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
            // The first arrival is still ahead: keep the dock back from the moment the Workspace exists.
            if (greetOnFirstArrival && !_greeted) PetWorkspace.HoldDock(true);
        }

        // ---------------------------------------------------------------- desk presence

        void OnDeskState(DeskState state)
        {
            switch (state)
            {
                case DeskState.Near:
                case DeskState.Seated:
                    if (_greeted && _awaySince >= 0f && Time.time - _awaySince >= rearmAwaySeconds)
                    {
                        Log("Away for " + (Time.time - _awaySince).ToString("F0") + " s: the arrival greeting is armed again.");
                        _greeted = false;
                    }
                    _awaySince = -1f;
                    if (_sequence != null || _greetingUp) break;              // already arriving / presenting (Near -> Seated)
                    if (greetOnFirstArrival && !_greeted) _sequence = StartCoroutine(FirstArrivalRoutine());
                    else if (hopOnNear) _sequence = StartCoroutine(NearRoutine());
                    break;
                case DeskState.Away:
                    CancelSequence();
                    if (_openAfterDock != null) { StopCoroutine(_openAfterDock); _openAfterDock = null; }
                    _awaySince = Time.time;
                    if (_greetingUp)
                    {
                        Log("Away with the greeting up: folding it.");
                        GreetingUp(false);
                        if (greeting != null) greeting.Fold();
                    }
                    if (controller != null) controller.ReleaseStation();
                    PetWorkspace.HoldDock(false);                             // nothing stays stuck; the dock is hidden on Away anyway
                    Leave();
                    break;
            }
        }

        IEnumerator NearRoutine()
        {
            Log("At the desk.");
            yield return EnsureOnDesk();
            _sequence = null;
        }

        // Hop onto the desk DeskPresence picked (the adapter builds / moves the desk NavMesh first), then wait for the landing.
        IEnumerator EnsureOnDesk()
        {
            if (controller == null || !hopOnNear) yield break;
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

        // ---------------------------------------------------------------- first arrival

        IEnumerator FirstArrivalRoutine()
        {
            _greeted = true;                                   // once per session (re-armed after rearmAwaySeconds Away)
            _digestSent = _digestArrived = false;
            _digestLine = null;
            Log("First arrival: holding the dock.");
            PetWorkspace.HoldDock(true);
            yield return EnsureOnDesk();

            Vector3 spot = GreetingSpot();
            if (controller != null)
            {
                Log("To the greeting spot.");
                controller.StationAt(spot);                    // Wandering, parked, facing the user
            }

            float deadline = Time.time + hubWait;
            while (ForgeNet.Status == ForgeNetStatus.Connecting && Time.time < deadline) yield return null;
            if (ForgeNet.Status == ForgeNetStatus.Live) yield return DigestStep();
            else Log("Hub not live; the card says so (asked when it connects).");

            string line = _digestArrived && !string.IsNullOrEmpty(_digestLine) ? _digestLine : HubOfflineLine;
            if (greeting != null)
            {
                GreetingUp(true);
                greeting.Show(_presence, spot, line);
                Log("Greeting card: \"" + line + "\" + START.");
            }
            else
            {
                Log("No ArrivalGreeting under PetRuntime (rebuild the prefab); opening without START.");
                OnStartPressed();
            }
            _sequence = null;
        }

        IEnumerator DigestStep()
        {
            if (_digestSent) yield break;
            _digestSent = true;
            _digestArrived = false;
            Log("POST /messages/digest.");
            DigestTrigger.RequestDigest();
            float deadline = Time.time + digestReplyTimeout;
            while (!_digestArrived && Time.time < deadline) yield return null;
            Log(_digestArrived ? "Digest: \"" + _digestLine + "\"" : "No notify.digest within " + digestReplyTimeout.ToString("F0") + " s.");
        }

        // The hub came live while the card already says Hub offline: ask now and swap the headline (the hub speaks it).
        IEnumerator LateDigestRoutine()
        {
            yield return DigestStep();
            if (_greetingUp && _digestArrived && greeting != null && !string.IsNullOrEmpty(_digestLine)) greeting.SetLine(_digestLine);
            _sequence = null;
        }

        // The card shows the digest line, and the hub's pet.say puts the same line in Bit's bubble, right over the card
        // and the START pill. Keep the bubble quiet until START; the hub still speaks the line.
        void GreetingUp(bool up)
        {
            _greetingUp = up;
            if (_caption == null && controller != null) _caption = controller.GetComponentInChildren<GatorCaption>(true);
            if (_caption != null) _caption.Muted = up;
        }

        void OnStartPressed()
        {
            if (!_greetingUp && greeting != null) return;
            Log("START.");
            GreetingUp(false);
            if (greeting != null) greeting.Fold();
            if (controller != null)
            {
                Vector3 to = AfterStartSpot();
                controller.WanderTo(to);                           // releases the station, walks to his side of the desk
            }
            bool entrance = PetWorkspace.HasWorkspace && _presence != null && _presence.AtDesk;
            PetWorkspace.HoldDock(false);                                   // the dock comes in (frame slides, rings plop)
            if (entrance) Log("Dock: entrance.");
            if (_openAfterDock != null) StopCoroutine(_openAfterDock);
            _openAfterDock = StartCoroutine(OpenAfterDock(entrance ? dockEntranceSeconds : 0f));
        }

        IEnumerator OpenAfterDock(float wait)
        {
            if (wait > 0f) yield return new WaitForSeconds(wait);
            _openAfterDock = null;
            if (!openModeOnStart) yield break;
            if (!PetWorkspace.HasWorkspace) { Log("No Workspace in this scene; mode step skipped."); yield break; }
            string open = PetWorkspace.ActiveMode;
            if (open != null) { Log("Mode '" + open + "' is already open; leaving it."); yield break; }
            if (_presence != null && !_presence.AtDesk) yield break;
            bool ok = PetWorkspace.Open(codeModeId, fallbackModeId);
            Log(ok ? "Opened mode '" + PetWorkspace.ActiveMode + "'." : "Could not open '" + codeModeId + "' (nor '" + fallbackModeId + "').");
        }

        // In front of the seated user, to their left of the laptop / dock keep-out: greetingSpot in the desk frame, clamped
        // to this desk's width, pulled onto the desk surface, and nudged further left while it falls inside a keep-out hole.
        Vector3 GreetingSpot()
        {
            Transform frame = _presence.Frame;
            float half = Mathf.Max(0.15f, _presence.FrameWidth * 0.5f);
            float x = -Mathf.Min(Mathf.Abs(greetingSpot.x), half - 0.12f);
            GatorNavSurface desk = GatorNavSurface.Desk;
            Vector3 p = frame.TransformPoint(new Vector3(x, 0f, greetingSpot.z));
            if (desk == null || !desk.IsBuilt) return p;
            p = desk.ClosestPoint(p, 0.08f);
            for (int i = 1; i <= 6 && desk.IsInsideHole(p, out _); i++)
                p = desk.ClosestPoint(frame.TransformPoint(new Vector3(x - 0.06f * i, 0f, greetingSpot.z)), 0.08f);
            return p;
        }

        Vector3 AfterStartSpot()
        {
            Transform frame = _presence.Frame;
            float half = Mathf.Max(0.15f, _presence.FrameWidth * 0.5f);
            float x = Mathf.Min(afterStartSpot.x, half - 0.15f);
            float z = Mathf.Min(afterStartSpot.z, Mathf.Max(0.15f, _presence.FrameDepth - 0.12f));
            Vector3 p = frame.TransformPoint(new Vector3(x, 0f, z));
            GatorNavSurface desk = GatorNavSurface.Desk;
            return desk != null && desk.IsBuilt ? desk.ClosestPoint(p, 0.08f) : p;
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
            // The card went up saying Hub offline: ask for the digest now and update it.
            if (_greetingUp && !_digestSent && _sequence == null) _sequence = StartCoroutine(LateDigestRoutine());
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
            _digestLine = d.line;
        }

        string PetId => bridge != null ? bridge.PetId : "bit";

        void Log(string line) => Debug.Log("[Arrival] " + line, this);
    }
}
