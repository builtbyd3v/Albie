using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Small 3D desk button that summons the gator (GatorSummon listens to AnyPressed, so there can be one per table).
    /// Inputs, all ending in Press():
    ///  * Meta Interaction SDK: PokeInteractable / RayInteractable children with an InteractableUnityEventWrapper whose
    ///    WhenSelect → Press(), WhenHover → HoverOn(), WhenUnhover → HoverOff() (wired by Forge/Pet/6. Add Whistle
    ///    To Open Scene). No Meta types here, per docs/PET_AGENT_CONTRACT.md.
    ///  * Mouse: left click raycast from PetUser.HeadCamera (screen centre while the cursor is locked).
    ///  * whistleAction: F, or controller B (right secondaryButton). F is ignored while a sandbox typed-STT field is open.
    /// Placement: on its table (SetTable, one button per MRUK table via GatorMrukAdapter) or, with no table assigned,
    /// on GatorNavSurface.Desk; the near corner on the user's right, re-placed when the user walks up to that table from
    /// another side. Once Hudson's DeskDock shows on that table it sits just past the dock's right end instead and slides
    /// with it (‹ ›, going round the desk), and stays there while the dock is away. No table and no desk: 0.3 m right of
    /// the gator's spawn.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]   // LateUpdate after DeskDock's (it moves the dock there), so it never lags the dock a frame
    public class WhistleButton : MonoBehaviour
    {
        public enum Visual { Idle, Active, Busy }

        [SerializeField, Tooltip("Keyboard / controller whistle. Default bindings (F, right controller B) are added if empty.")]
        InputAction whistleAction = new InputAction("Whistle", InputActionType.Button);
        [SerializeField, Tooltip("Visual child (pressed-depth + colour). Empty = first child Renderer.")]
        Renderer visual;
        [SerializeField, Tooltip("Ignore presses closer together than this (s).")]
        float debounce = 0.3f;
        [SerializeField, Tooltip("Max mouse raycast distance (m).")]
        float maxDistance = 5f;
        [SerializeField] LayerMask raycastMask = ~0;
        [SerializeField] UnityEvent onPressed = new UnityEvent();

        [Header("Placement")]
        [SerializeField, Tooltip("Place automatically on the desk / next to the gator.")]
        bool autoPlace = true;
        [SerializeField, Tooltip("Distance (m) in from both edges of the near-right corner.")]
        float cornerInset = 0.08f;
        [SerializeField, Tooltip("Re-place (other side of the table) when the head comes within this distance (m) of it.")]
        float nearTableDistance = 1.2f;
        [SerializeField, Tooltip("Tilt (deg) of the button face toward the user.")]
        float tiltDegrees = 25f;
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
        [SerializeField, Tooltip("Gap (m) between the dock's frame line and the button's edge.")]
        float dockGap = 0.015f;

        [Header("Colours")]
        [SerializeField] Color idleColor = new Color(1f, 0.62f, 0.15f);
        [SerializeField] Color activeColor = new Color(0.35f, 0.85f, 0.45f);
        [SerializeField] Color busyColor = new Color(0.55f, 0.55f, 0.6f);

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

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

        BoxCollider _collider;
        MaterialPropertyBlock _mpb;
        Vector3 _visualRest, _baseScale;
        Vector3 _spawnPos, _spawnRight = Vector3.right;
        bool _hasSpawn, _hovered, _placedFallback, _byDock;
        DeskDock _dock;
        Vector3 _dockPoint, _dockAway;
        float _dockHalf;
        float _lastPress = -10f, _pressedUntil = -1f, _startTime, _nextPlaceCheck;
        Vector3 _tableTop;
        Quaternion _tableRot = Quaternion.identity;
        Vector2 _tableSize;
        Visual _visual = Visual.Idle;

        void Awake()
        {
            _collider = GetComponent<BoxCollider>();
            _collider.isTrigger = true;              // a prop to press, not something to bump into
            if (whistleAction == null) whistleAction = new InputAction("Whistle", InputActionType.Button);
            if (whistleAction.bindings.Count == 0)
            {
                whistleAction.AddBinding("<Keyboard>/f");
                whistleAction.AddBinding("<XRController>{RightHand}/secondaryButton");
            }
            if (visual == null) visual = GetComponentInChildren<Renderer>(true);
            if (visual != null) _visualRest = visual.transform.localPosition;
            _baseScale = transform.localScale;
            _mpb = new MaterialPropertyBlock();
            ApplyColor();
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

        /// <summary>Whistle! (Interaction SDK WhenSelect, mouse, F / B, or any UnityEvent.)</summary>
        public void Press()
        {
            if (Time.time - _lastPress < debounce) return;
            _lastPress = Time.time;
            _pressedUntil = Time.time + 0.12f;
            Pressing?.Invoke(this);
            onPressed.Invoke();
            Pressed?.Invoke();
            AnyPressed?.Invoke(this);
        }

        public void HoverOn() => SetHovered(true);
        public void HoverOff() => SetHovered(false);

        public void SetHovered(bool hovered)
        {
            _hovered = hovered;
            transform.localScale = _hovered ? _baseScale * 1.08f : _baseScale;
        }

        public void SetVisual(Visual v)
        {
            if (_visual == v) return;
            _visual = v;
            ApplyColor();
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

        /// <summary>Near-right corner (left if leftHanded) of a horizontal rectangle, as seen from head, tilted toward it.</summary>
        public void PlaceOnRect(Vector3 top, Quaternion rot, Vector2 size, Transform head)
        {
            if (head == null || _collider == null) return;
            if (TryCornerPose(top, rot, size, head.position, out Vector3 pos, out Quaternion r))
            {
                transform.SetPositionAndRotation(pos, r);
                Placed = true;
            }
        }

        bool TryCornerPose(Vector3 top, Quaternion rot, Vector2 size, Vector3 headPos, out Vector3 pos, out Quaternion r)
        {
            if (!DeskCorner.TryPose(top, rot, size, headPos, cornerInset, leftHanded, tiltDegrees, out pos, out r)) return false;
            pos += Vector3.up * Lift();
            return true;
        }

        // Height of the button's origin above the desk top so the tilted collider rests on it (unhovered size).
        float Lift()
        {
            Vector3 s = RestScale();
            return _collider.size.y * s.y * 0.5f + Mathf.Sin(tiltDegrees * Mathf.Deg2Rad) * _collider.size.z * s.z * 0.5f;
        }

        Vector3 RestScale() => transform.parent != null ? Vector3.Scale(transform.parent.lossyScale, _baseScale) : _baseScale;

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (autoPlace && !_byDock && Time.time >= _nextPlaceCheck) { _nextPlaceCheck = Time.time + 0.5f; UpdatePlacement(); }

            if (whistleAction != null && whistleAction.WasPressedThisFrame() && !TypedSpeechToText.AnyListening) Press();   // sandbox text box open: F is a letter

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) TryMousePress(mouse);

            if (visual != null)
                visual.transform.localPosition = Time.time < _pressedUntil ? _visualRest - Vector3.up * 0.006f : _visualRest;
        }

        void LateUpdate()
        {
            if (autoPlace) FollowDock();
        }

        /// <summary>While it sits by the dock: the desk-top point under its centre, the direction along the edge away from
        /// the dock, and its half width (m). Props beside it (GatorHatchery) ask every LateUpdate to slide with it.</summary>
        public bool TryGetDockSpot(out Vector3 deskPoint, out Vector3 away, out float halfWidth)
        {
            deskPoint = _dockPoint; away = _dockAway; halfWidth = _dockHalf;
            return _byDock && isActiveAndEnabled;
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

        // Every frame (LateUpdate) while the dock shows on this table: just past its right end, level with its row, tilted toward the
        // user, so it slides with the dock. Kept on the table top. Sets _byDock: from then on it stays at the dock's spot
        // while the dock is away instead of going back to the corner.
        bool FollowDock()
        {
            if (!besideDock || _dock == null || !_dock.TryGetRightSide(dockGap, out Vector3 side, out Vector3 inward)) return false;
            bool hasRect = TryGetRect(out Vector3 top, out Quaternion rot, out Vector2 size);
            Vector3 edge = _dock.transform.parent.position;   // the dock's desk frame: middle of its table's front edge
            if (hasRect && (Mathf.Abs(edge.y - top.y) > 0.15f || DeskCorner.HorizontalDistance(top, rot, size, edge) > 0.05f))
                return false;                       // the dock is on another table

            float halfWidth = _collider.size.x * RestScale().x * 0.5f;
            Vector3 pos = side + Vector3.Cross(Vector3.up, inward) * halfWidth;
            if (hasRect) pos = DeskCorner.ClampInside(top, rot, size, pos, halfWidth);
            _dockPoint = pos; _dockAway = Vector3.Cross(Vector3.up, inward); _dockHalf = halfWidth;
            pos += Vector3.up * Lift();
            Quaternion r = DeskCorner.TiltToward(-inward, tiltDegrees);
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
            Vector3 lift = Vector3.up * (_collider.size.y * transform.lossyScale.y * 0.5f);
            transform.SetPositionAndRotation(_spawnPos + right * spawnRightOffset + lift, Quaternion.LookRotation(Vector3.Cross(right, Vector3.up), Vector3.up));
            Placed = true;
            _placedFallback = true;                  // a desk found later (MRUK) replaces this spot
        }

        void TryMousePress(Mouse mouse)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null || _collider == null) return;
            Ray ray = Cursor.lockState == CursorLockMode.Locked
                ? cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : cam.ScreenPointToRay(mouse.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Collide)
                && (hit.collider == _collider || hit.collider.transform.IsChildOf(transform)))
                Press();
        }

        void ApplyColor()
        {
            if (visual == null || _mpb == null) return;
            Color c = _visual == Visual.Active ? activeColor : _visual == Visual.Busy ? busyColor : idleColor;
            visual.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, c);
            _mpb.SetColor(ColorId, c);
            visual.SetPropertyBlock(_mpb);
        }
    }
}
