using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// The whistle that summons the gator (GatorSummon listens to AnyPressed, so there can be one per table): one of
    /// Hudson's dock rings (Core DockRing) with a white whistle icon, floating off the desk on its column of light like an
    /// open dock mode and turned toward the head. Inputs, all ending in Press():
    ///  * the ring's invisible uGUI Button: the mouse (DesktopRig), and Interaction SDK ray + poke when the editor
    ///    builder added canvas interaction to it (WhistleSetup). No Meta types here, per docs/PET_AGENT_CONTRACT.md.
    ///  * whistleAction: F, or controller B (right secondaryButton). F is ignored while a sandbox typed-STT field is open.
    /// Placement: on its table (SetTable, one button per MRUK table via GatorMrukAdapter) or, with no table assigned,
    /// on GatorNavSurface.Desk; the near corner on the user's right, re-placed when the user walks up to that table from
    /// another side. Once Hudson's DeskDock shows on that table it sits just past the dock's right end instead and slides
    /// with it (‹ ›, grip-drag, going round the desk), and stays there while the dock is away. No table and no desk:
    /// 0.3 m right of the gator's spawn. Lies flat like the dock (canvas +Z into the desk); only the light floats.
    /// </summary>
    [DisallowMultipleComponent]
    public class WhistleButton : MonoBehaviour
    {
        public enum Visual { Idle, Active, Busy }

        [SerializeField, Tooltip("Keyboard / controller whistle. Default bindings (F, right controller B) are added if empty.")]
        InputAction whistleAction = new InputAction("Whistle", InputActionType.Button);
        [SerializeField, Tooltip("The floating ring (WhistleSetup builds it, with XR canvas interaction). Empty = the first child DockRing, else one made at runtime (mouse only).")]
        DockRing ring;
        [SerializeField, Tooltip("Ignore presses closer together than this (s).")]
        float debounce = 0.3f;
        [SerializeField] UnityEvent onPressed = new UnityEvent();

        [Header("Placement")]
        [SerializeField, Tooltip("Place automatically on the desk / next to the gator.")]
        bool autoPlace = true;
        [SerializeField, Tooltip("Distance (m) in from both edges of the near-right corner.")]
        float cornerInset = 0.08f;
        [SerializeField, Tooltip("Re-place (other side of the table) when the head comes within this distance (m) of it.")]
        float nearTableDistance = 1.2f;
        [SerializeField, Tooltip("No desk after this many seconds: place next to the gator spawn.")]
        float noDeskFallbackSeconds = 2f;
        [SerializeField, Tooltip("Fallback: metres to the right of the gator spawn.")]
        float spawnRightOffset = 0.3f;
        [SerializeField, Tooltip("Gator used for the fallback placement. Empty = first GatorController.")]
        GatorController gator;
        [SerializeField, Tooltip("Mirror to the user's left.")]
        bool leftHanded = false;
        [SerializeField, Tooltip("While the DeskDock shows on this table, sit just past its right end and slide with it.")]
        bool besideDock = true;
        [SerializeField, Tooltip("Gap (m) between the dock's frame line and the ring's edge.")]
        float dockGap = 0.015f;

        /// <summary>The ring's label, the dock's mode-label style.</summary>
        public const string Label = "WHISTLE";
        const float Hover = 0.004f;  // the ring's plane above the desk top (m), as the dock's

        /// <summary>Raised first on every press (e.g. GatorMrukAdapter makes this button's table the desk).</summary>
        public event Action<WhistleButton> Pressing;
        public event Action Pressed;
        /// <summary>Any button in the scene was pressed (after its Pressing/Pressed).</summary>
        public static event Action<WhistleButton> AnyPressed;
        /// <summary>Every enabled button.</summary>
        public static IReadOnlyList<WhistleButton> All => s_all;
        public bool Placed { get; private set; }
        public bool HasTable { get; private set; }

        static readonly List<WhistleButton> s_all = new List<WhistleButton>(8);

        Vector3 _spawnPos, _spawnRight = Vector3.right;
        bool _hasSpawn, _placedFallback, _byDock;
        DeskDock _dock;
        float _lastPress = -10f, _startTime, _nextPlaceCheck;
        Vector3 _tableTop;
        Quaternion _tableRot = Quaternion.identity;
        Vector2 _tableSize;
        Visual _visual = Visual.Idle;

        void Awake()
        {
            if (whistleAction == null) whistleAction = new InputAction("Whistle", InputActionType.Button);
            if (whistleAction.bindings.Count == 0)
            {
                whistleAction.AddBinding("<Keyboard>/f");
                whistleAction.AddBinding("<XRController>{RightHand}/secondaryButton");
            }
            if (ring == null) ring = GetComponentInChildren<DockRing>(true);
            if (ring == null) ring = DockRing.Create(transform, Label, ForgeTheme.Tool, ForgeTheme.DockModeRing, null);
            ring.Button.onClick.RemoveListener(Press);   // a clone of a live template has none, but be sure
            ring.Button.onClick.AddListener(Press);
            ring.SetIcon(ForgeTheme.DockIcon("whistle"));
            ring.Raised = true;
            ApplyVisual();
        }

        void Start()
        {
            _startTime = Time.time;
            if (gator == null) gator = FindAnyObjectByType<GatorController>();
            if (gator != null)
            {
                _spawnPos = gator.transform.position;
                _spawnRight = gator.transform.right;
                _hasSpawn = true;
            }
            if (besideDock) _dock = FindAnyObjectByType<DeskDock>(FindObjectsInactive.Include);   // hidden until the user is at a desk
        }

        void OnEnable()
        {
            whistleAction?.Enable();
            if (!s_all.Contains(this)) s_all.Add(this);
        }

        void OnDisable()
        {
            whistleAction?.Disable();
            s_all.Remove(this);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { AnyPressed = null; s_all.Clear(); }

        // ---------------------------------------------------------------- public API

        /// <summary>Whistle! (the ring's Button, F / B, or any UnityEvent.)</summary>
        public void Press()
        {
            if (Time.time - _lastPress < debounce) return;
            _lastPress = Time.time;
            Pressing?.Invoke(this);
            onPressed.Invoke();
            Pressed?.Invoke();
            AnyPressed?.Invoke(this);
        }

        /// <summary>Idle: the plain ring. Active (summoned / listening): the dock's selected look. Busy (thinking / working): dimmed, not pressable.</summary>
        public void SetVisual(Visual v)
        {
            if (_visual == v) return;
            _visual = v;
            ApplyVisual();
        }

        /// <summary>This button belongs to a table (world top-face centre, yaw, size): it lives on that table's near-right corner, or beside the dock once that shows there.</summary>
        public void SetTable(Vector3 topCenter, Quaternion yaw, Vector2 size)
        {
            _tableTop = topCenter;
            _tableRot = yaw;
            _tableSize = size;
            HasTable = true;
            Placed = false;
            _byDock = false;
            Transform head = PetUser.Head;
            if (head != null) PlaceOnRect(topCenter, yaw, size, head);
        }

        /// <summary>Put the button on the desk's near corner on the user's right.</summary>
        public void PlaceOnDesk(GatorNavSurface desk, Transform head)
        {
            if (desk == null || !desk.IsBuilt) return;
            PlaceOnRect(desk.TopCenter, desk.Rotation, desk.Size, head);
        }

        /// <summary>Near-right corner (left if leftHanded) of a horizontal rectangle, as seen from head.</summary>
        public void PlaceOnRect(Vector3 top, Quaternion rot, Vector2 size, Transform head)
        {
            if (head == null) return;
            if (TryCornerPose(top, rot, size, head.position, out Vector3 pos, out Quaternion r))
            {
                transform.SetPositionAndRotation(pos, r);
                Placed = true;
            }
        }

        bool TryCornerPose(Vector3 top, Quaternion rot, Vector2 size, Vector3 headPos, out Vector3 pos, out Quaternion r)
        {
            float inset = Mathf.Max(cornerInset, RingRadius());
            if (!DeskCorner.TryPose(top, rot, size, headPos, inset, leftHanded, 0f, out pos, out r)) return false;
            r = FlatPose(-(r * Vector3.forward));   // TryPose's forward points out of the table
            pos += Vector3.up * Hover;
            return true;
        }

        // Lying on the desk like the dock: canvas +Z into the desk, its up (the label's up) pointing into the table.
        static Quaternion FlatPose(Vector3 inward) =>
            Quaternion.LookRotation(Vector3.down, Vector3.ProjectOnPlane(inward, Vector3.up).normalized);

        // The ring's radius in metres (its glow spills past this).
        float RingRadius() => ring != null ? ring.HalfWidth * ring.transform.lossyScale.x : 0.036f;

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (autoPlace && !FollowDock() && !_byDock && Time.time >= _nextPlaceCheck) { _nextPlaceCheck = Time.time + 0.5f; UpdatePlacement(); }
            if (ring != null) ring.Viewer = PetUser.Head;

            if (whistleAction != null && whistleAction.WasPressedThisFrame() && !TypedSpeechToText.AnyListening) Press();   // sandbox text box open: F is a letter
        }

        // Throttled (0.5 s). First placement as soon as a table / desk and a head exist; afterwards only when the user is
        // at this table (so it follows them round to another side, but buttons on other tables stay put).
        void UpdatePlacement()
        {
            Transform head = PetUser.Head;
            if (head == null) return;
            if (!TryGetRect(out Vector3 top, out Quaternion rot, out Vector2 size)) { PlaceFallback(); return; }
            if (!Placed || _placedFallback) { _placedFallback = false; PlaceOnRect(top, rot, size, head); return; }
            if (DeskCorner.HorizontalDistance(top, rot, size, head.position) > nearTableDistance) return;
            if (TryCornerPose(top, rot, size, head.position, out Vector3 pos, out Quaternion r)
                && (pos - transform.position).sqrMagnitude > 0.05f * 0.05f)
                transform.SetPositionAndRotation(pos, r);
        }

        // Every frame while the dock shows on this table: just past its right end, level with its row, so it slides with
        // the dock. Kept on the table top. Sets _byDock: from then on it stays at the dock's spot while the dock is away
        // instead of going back to the corner.
        bool FollowDock()
        {
            if (!besideDock || _dock == null || !_dock.TryGetRightSide(dockGap, out Vector3 side, out Vector3 inward)) return false;
            bool hasRect = TryGetRect(out Vector3 top, out Quaternion rot, out Vector2 size);
            Vector3 edge = _dock.transform.parent.position;   // the dock's desk frame: middle of its table's front edge
            if (hasRect && (Mathf.Abs(edge.y - top.y) > 0.15f || DeskCorner.HorizontalDistance(top, rot, size, edge) > 0.05f))
                return false;                       // the dock is on another table

            float radius = RingRadius();
            Vector3 pos = side + Vector3.Cross(Vector3.up, inward) * radius;
            if (hasRect) pos = DeskCorner.ClampInside(top, rot, size, pos, radius);
            pos.y = (hasRect ? top.y : side.y) + Hover;
            Quaternion r = FlatPose(inward);
            if ((pos - transform.position).sqrMagnitude > 0.0005f * 0.0005f || Quaternion.Angle(r, transform.rotation) > 0.1f)
                transform.SetPositionAndRotation(pos, r);
            Placed = true;
            _placedFallback = false;
            _byDock = true;
            return true;
        }

        // This button's table, or with none assigned the gator's desk. False: neither (yet).
        bool TryGetRect(out Vector3 top, out Quaternion rot, out Vector2 size)
        {
            if (HasTable) { top = _tableTop; rot = _tableRot; size = _tableSize; return true; }
            GatorNavSurface desk = GatorNavSurface.Desk;
            if (desk != null && desk.IsBuilt) { top = desk.TopCenter; rot = desk.Rotation; size = desk.Size; return true; }
            top = default; rot = Quaternion.identity; size = default;
            return false;
        }

        void PlaceFallback()
        {
            if (Placed || !_hasSpawn || Time.time - _startTime < noDeskFallbackSeconds) return;
            Vector3 right = Vector3.ProjectOnPlane(leftHanded ? -_spawnRight : _spawnRight, Vector3.up);
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            right.Normalize();
            transform.SetPositionAndRotation(_spawnPos + right * spawnRightOffset + Vector3.up * Hover, FlatPose(Vector3.Cross(right, Vector3.up)));
            Placed = true;
            _placedFallback = true;                  // a desk found later (MRUK) replaces this spot
        }

        void ApplyVisual()
        {
            if (ring == null) return;
            ring.Selected = _visual == Visual.Active;
            ring.Interactable = _visual != Visual.Busy;
        }
    }
}
