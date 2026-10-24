using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    public enum TerritoryScope { Desk, Floor }

    /// <summary>
    /// Bit's crew (docs/PET_AGENT_CONTRACT.md, Addendum D): hatches and sends home extra gators, one per
    /// GatorAgentType, and splits the room between everyone.
    ///
    /// Hatching: GatorHatchery (the egg on the desk), the hub's crew.spawn (Bit's spawn_agent tool, "Bit, hatch a
    /// tester"), N on the keyboard, or Spawn() from code. A crew gator is a copy of crewPrefab (else of Bit) made under
    /// an inactive parent, so Bit-only parts (mic, whistle, voice, hub socket, MRUK adapter...) are stripped before
    /// any Awake runs; its GatorAgentBridge then shares Bit's event source and only reacts to its own pet_id.
    /// Unity is the source of truth: crew.roster goes to the hub after every change and on every reconnect.
    ///
    /// Areas: with a crew, every gator (Bit included) owns an equal-walkable-area strip of the desk (GatorTerritories,
    /// the keep-out doesn't count), nearest the user first: Bit, then the crew in hatch order. Gators that don't fit
    /// (strips narrower than minStripWidth) share the floor. Scope Floor splits the floor instead. Everyone idles
    /// more while a crew exists (restful idle profile); Bit gets his solo behaviour back when the crew is gone.
    ///
    /// Whistle (any WhistleButton): clears the desk for Bit. Idle crew gators go home; busy ones (GatorAgentBridge.Busy)
    /// line up along the desk's back edge, away from the user, and keep working there. A gator leaves the line when
    /// its task is done; the others' areas stop short of the line while it exists.
    ///
    /// Workspace gate (Main): the egg / picker and the N / X keys only work while the desk mode gateModeId ("code") is open
    /// (CrewAllowed); when it closes the crew stays on the desk, keeps its antics, but its speech bubbles are muted until the
    /// mode returns. Bit is never muted; the hub's crew.spawn / crew.dismiss are never gated. Scenes without a Workspace
    /// (the sandboxes) are never gated.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorCrew : MonoBehaviour
    {
        [SerializeField, Tooltip("Bit. Empty = the first GatorController in the scene that isn't a crew member.")]
        GatorController lead;
        [SerializeField, Tooltip("Gator prefab copied for every crew member (GatorAssistant). Empty = a copy of Bit.")]
        GameObject crewPrefab;
        [SerializeField, Tooltip("Material for the hats (tinted per type). Empty = URP Lit.")]
        Material hatMaterial;

        [Header("Areas")]
        [SerializeField, Tooltip("Desk: split the desk (overflow onto the floor). Floor: split the floor.")]
        TerritoryScope scope = TerritoryScope.Desk;
        [SerializeField, Tooltip("Narrowest desk strip (m) per gator; more gators than fit go to the floor.")]
        float minStripWidth = 0.16f;

        [Header("Restful idling (applied to every gator while a crew exists)")]
        [SerializeField, Range(0f, 1f)] float restChance = 0.85f;
        [SerializeField] Vector2 restSeconds = new Vector2(6f, 16f);
        [SerializeField, Range(0f, 1f)] float runChance = 0.08f;
        [SerializeField] Vector2 shortPause = new Vector2(2.5f, 6f);

        [Header("Whistle")]
        [SerializeField, Tooltip("A whistle sends idle crew home and lines busy crew up at the back of the desk.")]
        bool whistleClearsDesk = true;
        [SerializeField, Tooltip("Gap (m) between gators in the line.")]
        float lineupSpacing = 0.17f;
        [SerializeField, Tooltip("Line distance (m) in from the back edge; the other areas keep twice this clear.")]
        float lineupInset = 0.07f;

        [Header("Workspace gate")]
        [SerializeField, Tooltip("Egg / picker and N / X only work while this desk mode is open; crew captions are muted otherwise. Empty = never gated. No Workspace in the scene = never gated.")]
        string gateModeId = "code";

        [Header("Keyboard")]
        [SerializeField, Tooltip("Hatch the type shown on the egg (or the next free type).")]
        InputAction hatchAction = new InputAction("CrewHatch", InputActionType.Button, "<Keyboard>/n");
        [SerializeField, Tooltip("Send the most recently hatched gator home.")]
        InputAction dismissAction = new InputAction("CrewDismiss", InputActionType.Button, "<Keyboard>/x");

        // Bit-only parts removed from a crew copy (by type name: no compile-time dependency on Meta / sandbox types).
        static readonly HashSet<string> StripTypes = new HashSet<string>
        {
            "GatorMrukAdapter", "GatorSandboxNav", "PetSandboxHotkeys", "GatorSummon", "GatorFollowUp", "GatorTalkAnimator",
            "PushToTalk", "HubSpeechToText", "TypedSpeechToText", "StreamedPcmTextToSpeech", "AgentClient", "MockEventSource",
            "GatorCrew", "GatorHatchery", "WhistleButton", "DesktopRig", "SandboxRigSelector", "GatorCrewMember", "WakeListener", "GatorNameTag",
            "ArrivalDirector",
        };

        public static GatorCrew Instance { get; private set; }
        public IReadOnlyList<GatorCrewMember> Members => _members;
        public GatorController Lead => lead;
        /// <summary>Raised after a gator hatched or left.</summary>
        public event Action Changed;
        /// <summary>Hatching from the egg / keyboard is available: the gate mode is open, or there is no gate / no Workspace.</summary>
        public bool CrewAllowed => string.IsNullOrEmpty(gateModeId) || !PetWorkspace.HasWorkspace || PetWorkspace.ActiveMode == gateModeId;

        readonly List<GatorCrewMember> _members = new List<GatorCrewMember>(GatorAgentTypes.All.Length);
        readonly List<GatorController> _gators = new List<GatorController>(GatorAgentTypes.All.Length + 1);
        readonly List<GatorTerritory> _areas = new List<GatorTerritory>(GatorAgentTypes.All.Length + 1);
        readonly List<GatorTerritory> _floorAreas = new List<GatorTerritory>(GatorAgentTypes.All.Length + 1);
        readonly List<MonoBehaviour> _scratch = new List<MonoBehaviour>(32);
        readonly List<GatorCrewMember> _lineup = new List<GatorCrewMember>(GatorAgentTypes.All.Length);
        GatorAgentBridge _leadBridge;
        IPetEventSource _source;
        GatorHatchery _hatchery;
        Transform _staging;
        bool _wasConnected, _leadCalm, _territoriesDirty, _lastAllowed = true;
        float _nextLayoutCheck;
        int _layoutHash;
        float _leadRestChance, _leadRunChance;
        Vector2 _leadRestSeconds, _leadShortPause;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        void Awake()
        {
            Instance = this;
            if (lead == null) lead = FindLead();
            if (lead != null) _leadBridge = lead.GetComponent<GatorAgentBridge>();
        }

        void Start() => WhistleButton.AnyPressed += OnWhistle;

        void OnEnable() { hatchAction.Enable(); dismissAction.Enable(); }
        void OnDisable() { hatchAction.Disable(); dismissAction.Disable(); }

        void OnDestroy()
        {
            if (_source != null) _source.EventReceived -= OnHubEvent;
            WhistleButton.AnyPressed -= OnWhistle;
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- public API

        public bool IsHatched(GatorAgentType t) => Find(t) != null;

        public GatorCrewMember Find(GatorAgentType t)
        {
            for (int i = 0; i < _members.Count; i++) if (_members[i] != null && _members[i].Type == t) return _members[i];
            return null;
        }

        /// <summary>First type (in catalog order, starting at fromIndex) that hasn't hatched yet; null if all have.</summary>
        public GatorAgentType NextFree(int fromIndex = 0)
        {
            var all = GatorAgentTypes.All;
            for (int i = 0; i < all.Length; i++)
            {
                GatorAgentType t = all[(fromIndex + i + all.Length) % all.Length];
                if (!IsHatched(t)) return t;
            }
            return null;
        }

        /// <summary>Hatch a gator of this type (no-op if one exists). spawnAt = where it appears (default: the egg).</summary>
        public GatorCrewMember Spawn(GatorAgentType type, Vector3? spawnAt = null)
        {
            if (type == null) return null;
            GatorCrewMember existing = Find(type);
            if (existing != null) return existing;
            if (lead == null) { Debug.LogWarning("[GatorCrew] No lead gator (Bit) in the scene; can't hatch.", this); return null; }

            GameObject source = crewPrefab != null ? crewPrefab : lead.gameObject;
            if (_staging == null)
            {
                _staging = new GameObject("CrewStaging (inactive)").transform;
                _staging.SetParent(transform, false);
                _staging.gameObject.SetActive(false);
            }
            Vector3 pos = spawnAt ?? DefaultSpawnPoint();
            Transform head = PetUser.Head;
            Vector3 look = head != null ? head.position - pos : -lead.transform.forward;
            look.y = 0f;
            Quaternion rot = look.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(look) : lead.transform.rotation;

            // Inactive parent: nothing on the copy wakes up until it's cleaned and configured.
            GameObject go = Instantiate(source, pos, rot, _staging);
            go.name = "Gator " + type.Name;
            Strip(go, source == lead.gameObject);
            var member = go.AddComponent<GatorCrewMember>();
            member.Init(type, hatMaterial);
            var controller = go.GetComponent<GatorController>();
            if (controller != null)
            {
                controller.StayInTerritoryWhenBusy = true;
                controller.SetIdleProfile(restChance, restSeconds, runChance, shortPause);
                GatorNavSurface under = SurfaceAt(pos);
                if (under != null) controller.SetWanderArea(under);
            }
            var bridge = go.GetComponent<GatorAgentBridge>();
            if (bridge != null) bridge.UseSharedSource(type.Id, _leadBridge);
            go.transform.SetParent(null, true);          // wakes up now
            member.SetMuted(!CrewAllowed);               // hatched by the hub while the code mode is closed: quiet until it opens

            _members.Add(member);
            OnCrewChanged();
            Debug.Log("[GatorCrew] Hatched " + type.Name + " (" + type.Id + ", scale " + type.Scale.ToString("F2") + ").", this);
            return member;
        }

        /// <summary>Send a crew gator home (by pet_id / type id or name).</summary>
        public bool Dismiss(string petIdOrName)
        {
            GatorCrewMember m = Find(GatorAgentTypes.Find(petIdOrName));
            if (m == null) return false;
            _members.Remove(m);
            _lineup.Remove(m);
            if (m.Bridge != null && m.Bridge.Busy && m.Bridge.Source != null)
                m.Bridge.Source.Send("agent.cancel", new JObject { ["pet_id"] = m.PetId });
            m.GoHome();
            OnCrewChanged();
            return true;
        }

        public bool DismissLast() => _members.Count > 0 && Dismiss(_members[_members.Count - 1].PetId);

        /// <summary>Whistle: idle crew go home, busy crew line up at the back of the desk (Bit is untouched).</summary>
        public void ClearDeskForLead()
        {
            for (int i = _members.Count - 1; i >= 0; i--)
            {
                GatorCrewMember m = _members[i];
                if (m == null) continue;
                if (m.Bridge != null && m.Bridge.Busy) { if (!_lineup.Contains(m)) _lineup.Add(m); }
                else Dismiss(m.PetId);
            }
            _territoriesDirty = true;
        }

        void OnWhistle(WhistleButton _)
        {
            if (whistleClearsDesk && _members.Count > 0) ClearDeskForLead();
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (lead == null) { lead = FindLead(); if (lead != null) _leadBridge = lead.GetComponent<GatorAgentBridge>(); }
            BindSource();

            bool connected = _source != null && _source.IsConnected;
            if (connected && !_wasConnected) SendRoster();      // after AgentClient's hello on (re)connect
            _wasConnected = connected;

            bool allowed = CrewAllowed;
            if (allowed != _lastAllowed)
            {
                _lastAllowed = allowed;
                for (int i = 0; i < _members.Count; i++) if (_members[i] != null) _members[i].SetMuted(!allowed);
                if (_members.Count > 0) Debug.Log("[GatorCrew] " + (allowed ? "Code mode open: crew unmuted." : "Code mode closed: crew muted."), this);
            }
            if (allowed && !TypedSpeechToText.AnyListening)   // sandbox text box open: N / X are letters
            {
                if (hatchAction.WasPressedThisFrame()) HatchFromKeyboard();
                if (dismissAction.WasPressedThisFrame()) DismissLast();
            }

            for (int i = _lineup.Count - 1; i >= 0; i--)          // done working: back to a normal area
                if (_lineup[i] == null || _lineup[i].Bridge == null || !_lineup[i].Bridge.Busy) { _lineup.RemoveAt(i); _territoriesDirty = true; }

            if (Time.time >= _nextLayoutCheck || _territoriesDirty)
            {
                _nextLayoutCheck = Time.time + 1f;
                int h = LayoutHash();
                if (_territoriesDirty || h != _layoutHash) { _layoutHash = h; _territoriesDirty = false; AssignTerritories(); }
            }
        }

        // N: the type on the egg if it isn't out yet, else the next free one (never sends anyone home).
        void HatchFromKeyboard()
        {
            GatorHatchery egg = Hatchery();
            GatorAgentType t = egg != null && !IsHatched(egg.Shown) ? egg.Shown : NextFree();
            if (t != null) Spawn(t, egg != null && egg.Placed ? egg.SpawnPoint : (Vector3?)null);
        }

        void OnCrewChanged()
        {
            for (int i = _members.Count - 1; i >= 0; i--) if (_members[i] == null) _members.RemoveAt(i);
            ApplyLeadIdleProfile();
            ShowLeadTag(_members.Count > 0);
            _territoriesDirty = true;
            SendRoster();
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- hub

        void BindSource()
        {
            IPetEventSource s = _leadBridge != null ? _leadBridge.Source : null;
            if (s == _source) return;
            if (_source != null) _source.EventReceived -= OnHubEvent;
            _source = s;
            if (_source != null) _source.EventReceived += OnHubEvent;
        }

        void OnHubEvent(PetEvent e)
        {
            if (e == null) return;
            if (e.type == "crew.spawn")
            {
                GatorAgentType t = GatorAgentTypes.Find(e.GetString("agent_type"));
                if (t != null) Spawn(t);
                else Debug.LogWarning("[GatorCrew] crew.spawn for unknown type '" + e.GetString("agent_type") + "'.", this);
            }
            else if (e.type == "crew.dismiss") Dismiss(e.GetString("pet_id"));
        }

        void SendRoster()
        {
            if (_source == null || !_source.IsConnected) return;
            var list = new JArray();
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] != null)
                    list.Add(new JObject { ["pet_id"] = _members[i].PetId, ["agent_type"] = _members[i].Type.Id });
            _source.Send("crew.roster", new JObject { ["members"] = list });
        }

        // ---------------------------------------------------------------- areas

        // Re-split when the crew changes or the surfaces move / re-bake (whistle on another table, keep-out moved).
        int LayoutHash()
        {
            unchecked
            {
                int h = (_members.Count * 31 + _lineup.Count) * 31 + (int)scope;
                h = h * 31 + SurfaceHash(GatorNavSurface.Desk);
                h = h * 31 + SurfaceHash(GatorNavSurface.Floor);
                return h;
            }
        }

        static int SurfaceHash(GatorNavSurface s)
        {
            if (s == null || !s.IsBuilt) return 0;
            unchecked
            {
                int h = Quantize(s.TopCenter).GetHashCode() * 31 + Quantize(s.Size).GetHashCode();
                var holes = s.Holes;
                h = h * 31 + holes.Count;
                for (int i = 0; i < holes.Count; i++) h = h * 31 + Quantize(holes[i].center).GetHashCode();
                return h;
            }
        }

        static Vector3Int Quantize(Vector3 v) => new Vector3Int(Mathf.RoundToInt(v.x * 50f), Mathf.RoundToInt(v.y * 50f), Mathf.RoundToInt(v.z * 50f));
        static Vector2Int Quantize(Vector2 v) => new Vector2Int(Mathf.RoundToInt(v.x * 50f), Mathf.RoundToInt(v.y * 50f));

        void AssignTerritories()
        {
            _gators.Clear();
            if (lead != null) _gators.Add(lead);
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] != null && _members[i].Controller != null && !_lineup.Contains(_members[i])) _gators.Add(_members[i].Controller);
            if (_members.Count == 0)
            {
                if (lead != null) lead.ClearTerritory();   // solo Bit: the whole desk / room, exactly as before
                return;
            }

            GatorNavSurface desk = GatorNavSurface.Desk, floor = GatorNavSurface.Floor;
            Transform head = PetUser.Head;
            bool lined = _lineup.Count > 0 && desk != null && desk.IsBuilt && AssignLineup(desk, head);
            int onDesk = 0;
            if (scope == TerritoryScope.Desk && desk != null && desk.IsBuilt)
            {
                onDesk = Mathf.Min(_gators.Count, GatorTerritories.Capacity(desk, minStripWidth));
                GatorTerritories.Split(desk, onDesk, head != null ? head.position : desk.TopCenter, _areas);
            }
            else _areas.Clear();

            int rest = _gators.Count - onDesk;
            bool floorOk = rest > 0 && floor != null && floor.IsBuilt
                           && GatorTerritories.Split(floor, rest, head != null ? head.position : floor.TopCenter, _floorAreas);
            for (int i = 0; i < _gators.Count; i++)
            {
                GatorTerritory a;
                if (i < onDesk) a = lined ? InFrontOfLine(_areas[i], desk) : _areas[i];
                else if (floorOk) a = _floorAreas[i - onDesk];
                else if (_areas.Count > 0) a = _areas[(i - onDesk) % _areas.Count];   // no floor: double up on the desk
                else { _gators[i].ClearTerritory(); continue; }
                _gators[i].SetTerritory(a.Surface, a.Center, a.Rotation, a.Size);
            }
        }

        // ---------------------------------------------------------------- lineup (whistle)

        bool _backAlongZ;      // the back edge runs along the desk's local X (back is ±Z), else along Z
        float _backSign;       // which side is the back (away from the user), in desk-local units

        // Busy crew stand in a row along the back edge, facing the user. Their "area" is a small square at their slot.
        bool AssignLineup(GatorNavSurface desk, Transform head)
        {
            Vector2 size = desk.Size;
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            Vector3 l = Quaternion.Inverse(desk.Rotation) * ((head != null ? head.position : desk.TopCenter - desk.Rotation * Vector3.forward) - desk.TopCenter);
            _backAlongZ = Mathf.Abs(l.z) / Mathf.Max(0.01f, hz) >= Mathf.Abs(l.x) / Mathf.Max(0.01f, hx);
            _backSign = _backAlongZ ? (l.z >= 0f ? -1f : 1f) : (l.x >= 0f ? -1f : 1f);
            float halfAlong = (_backAlongZ ? hx : hz) - 0.06f, depth = (_backAlongZ ? hz : hx) - lineupInset;
            int n = _lineup.Count;
            float spacing = n > 1 ? Mathf.Min(lineupSpacing, 2f * halfAlong / (n - 1)) : 0f;
            for (int k = 0; k < n; k++)
            {
                GatorCrewMember m = _lineup[k];
                if (m == null || m.Controller == null) continue;
                float along = (k - (n - 1) * 0.5f) * spacing;
                Vector3 local = _backAlongZ ? new Vector3(along, 0f, _backSign * depth) : new Vector3(_backSign * depth, 0f, along);
                m.Controller.SetTerritory(desk, desk.TopCenter + desk.Rotation * local, desk.Rotation, new Vector2(0.1f, 0.1f));
            }
            return true;
        }

        // A desk area cut short so it stops before the line (kept as is if that would leave a sliver).
        GatorTerritory InFrontOfLine(GatorTerritory a, GatorNavSurface desk)
        {
            Vector3 c = Quaternion.Inverse(desk.Rotation) * (a.Center - desk.TopCenter);
            float half = (_backAlongZ ? desk.Size.y : desk.Size.x) * 0.5f, limit = half - 2f * lineupInset;
            float mid = _backAlongZ ? c.z : c.x, ext = (_backAlongZ ? a.Size.y : a.Size.x) * 0.5f;
            float lo = mid - ext, hi = mid + ext;
            if (_backSign > 0f) hi = Mathf.Min(hi, limit); else lo = Mathf.Max(lo, -limit);
            if (hi - lo < 0.08f) return a;
            mid = (lo + hi) * 0.5f;
            if (_backAlongZ) { c.z = mid; a.Size.y = hi - lo; } else { c.x = mid; a.Size.x = hi - lo; }
            a.Center = desk.TopCenter + desk.Rotation * c;
            return a;
        }

        // Every gator idles more while there's a crew; Bit's own profile comes back when the crew is gone.
        void ApplyLeadIdleProfile()
        {
            if (lead == null) return;
            bool calm = _members.Count > 0;
            if (calm == _leadCalm) return;
            if (calm)
            {
                lead.GetIdleProfile(out _leadRestChance, out _leadRestSeconds, out _leadRunChance, out _leadShortPause);
                lead.SetIdleProfile(restChance, restSeconds, runChance, shortPause);
            }
            else lead.SetIdleProfile(_leadRestChance, _leadRestSeconds, _leadRunChance, _leadShortPause);
            _leadCalm = calm;
        }

        // Bit wears a name tag too while there's a crew (solo Bit looks exactly as before).
        void ShowLeadTag(bool on)
        {
            if (lead == null) return;
            var tag = lead.GetComponent<GatorNameTag>();
            if (tag == null)
            {
                if (!on) return;
                tag = lead.gameObject.AddComponent<GatorNameTag>();
                tag.Set("Bit", new Color(0.3f, 0.78f, 0.42f));
            }
            tag.Show(on);
        }

        // ---------------------------------------------------------------- helpers

        GatorController FindLead()
        {
            var all = FindObjectsByType<GatorController>();
            for (int i = 0; i < all.Length; i++)
                if (all[i].GetComponent<GatorCrewMember>() == null) return all[i];
            return null;
        }

        GatorHatchery Hatchery()
        {
            if (_hatchery == null) _hatchery = FindAnyObjectByType<GatorHatchery>();
            return _hatchery;
        }

        Vector3 DefaultSpawnPoint()
        {
            GatorHatchery egg = Hatchery();
            if (egg != null && egg.Placed) return egg.SpawnPoint;
            Transform l = lead.transform;
            return l.position + l.right * 0.15f;
        }

        static GatorNavSurface SurfaceAt(Vector3 p) => GatorNavSurface.TryGetSurfaceAt(p, out GatorNavSurface s) ? s : null;

        // Remove Bit-only components (and the voice child / Bit's runtime caption when copying the live Bit).
        void Strip(GameObject go, bool fromLiveLead)
        {
            for (int pass = 0; pass < 3; pass++)          // a component someone else requires goes on a later pass
            {
                bool removed = false;
                go.GetComponentsInChildren(true, _scratch);
                for (int i = 0; i < _scratch.Count; i++)
                {
                    MonoBehaviour mb = _scratch[i];
                    if (mb == null || !StripTypes.Contains(mb.GetType().Name)) continue;
                    GameObject owner = mb.gameObject;
                    DestroyImmediate(mb);
                    removed = true;
                    if (owner != go && owner.GetComponents<Component>().Length <= 2) DestroyImmediate(owner);   // e.g. the "Voice" child
                }
                if (!removed) break;
            }
            AudioSource[] sources = go.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++) DestroyImmediate(sources[i]);
            if (fromLiveLead)
            {
                foreach (string child in new[] { "GatorCaption", "NameTag" })   // Bit's runtime bubble / tag: the copy builds its own
                {
                    Transform t = go.transform.Find(child);
                    if (t != null) DestroyImmediate(t.gameObject);
                }
            }
            _scratch.Clear();
        }
    }
}
