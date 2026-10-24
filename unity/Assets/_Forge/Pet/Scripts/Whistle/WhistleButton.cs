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
    ///  * whistleAction: F, or controller B (right secondaryButton). F is ignored while the typed-STT field is open.
    /// Placement: on its table (SetTable, one button per MRUK table via GatorMrukAdapter) or, with no table assigned,
    /// on GatorNavSurface.Desk; always the near corner on the user's right, re-placed when the user walks up to that
    /// table from another side. No table and no desk: 0.3 m right of the gator's spawn.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [DisallowMultipleComponent]
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
        TypedSpeechToText _typed;
        MaterialPropertyBlock _mpb;
        Vector3 _visualRest, _baseScale;
        Vector3 _spawnPos, _spawnRight = Vector3.right;
        bool _hasSpawn, _hovered, _placedFallback;
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

        /// <summary>This button belongs to a table (world top-face centre, yaw, size): it lives on that table's near-right corner.</summary>
        public void SetTable(Vector3 topCenter, Quaternion yaw, Vector2 size)
        {
            _tableTop = topCenter;
            _tableRot = yaw;
            _tableSize = size;
            HasTable = true;
            Placed = false;
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
            float lift = _collider.size.y * transform.lossyScale.y * 0.5f
                       + Mathf.Sin(tiltDegrees * Mathf.Deg2Rad) * _collider.size.z * transform.lossyScale.z * 0.5f;
            pos += Vector3.up * lift;
            return true;
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (autoPlace && Time.time >= _nextPlaceCheck) { _nextPlaceCheck = Time.time + 0.5f; UpdatePlacement(); }

            if (whistleAction != null && whistleAction.WasPressedThisFrame() && !TypingInField()) Press();

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) TryMousePress(mouse);

            if (visual != null)
                visual.transform.localPosition = Time.time < _pressedUntil ? _visualRest - Vector3.up * 0.006f : _visualRest;
        }

        // Throttled (0.5 s). First placement as soon as a table / desk and a head exist; afterwards only when the user is
        // at this table (so it follows them round to another side, but buttons on other tables stay put).
        void UpdatePlacement()
        {
            Transform head = PetUser.Head;
            if (head == null) return;
            Vector3 top; Quaternion rot; Vector2 size;
            if (HasTable) { top = _tableTop; rot = _tableRot; size = _tableSize; }
            else
            {
                GatorNavSurface desk = GatorNavSurface.Desk;
                if (desk == null || !desk.IsBuilt) { PlaceFallback(); return; }
                top = desk.TopCenter; rot = desk.Rotation; size = desk.Size;
            }
            if (!Placed || _placedFallback) { _placedFallback = false; PlaceOnRect(top, rot, size, head); return; }
            if (DeskCorner.HorizontalDistance(top, rot, size, head.position) > nearTableDistance) return;
            if (TryCornerPose(top, rot, size, head.position, out Vector3 pos, out Quaternion r)
                && (pos - transform.position).sqrMagnitude > 0.05f * 0.05f)
                transform.SetPositionAndRotation(pos, r);
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

        // Only evaluated when F/B is pressed; PushToTalk may add the typed field at runtime, so look it up lazily.
        bool TypingInField()
        {
            if (_typed == null) _typed = FindAnyObjectByType<TypedSpeechToText>();
            return _typed != null && _typed.IsListening;
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
