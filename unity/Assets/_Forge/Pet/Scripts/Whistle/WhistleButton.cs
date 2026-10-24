using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Small 3D desk button that summons the gator (GatorSummon subscribes to Pressed).
    /// Inputs, all ending in Press():
    ///  * Meta Interaction SDK: PokeInteractable / RayInteractable children with an InteractableUnityEventWrapper whose
    ///    WhenSelect → Press(), WhenHover → HoverOn(), WhenUnhover → HoverOff() (wired by Forge/Pet/6. Add Whistle
    ///    To Open Scene). No Meta types here, per docs/PET_AGENT_CONTRACT.md.
    ///  * Mouse: left click raycast from PetUser.HeadCamera (screen centre while the cursor is locked).
    ///  * whistleAction: F, or controller B (right secondaryButton). F is ignored while the typed-STT field is open.
    /// Places itself at the desk's near edge, to the user's right, once GatorNavSurface.Desk is built; without a desk
    /// it falls back to 0.3 m right of the gator's spawn.
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
        [SerializeField, Tooltip("Lateral offset (m) to the user's right of the head's projection on the near edge.")]
        float lateralOffset = 0.45f;
        [SerializeField, Tooltip("Distance (m) in from the desk's near edge.")]
        float edgeInset = 0.08f;
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

        public event Action Pressed;
        public bool PlacedOnDesk { get; private set; }

        BoxCollider _collider;
        TypedSpeechToText _typed;
        MaterialPropertyBlock _mpb;
        Vector3 _visualRest, _baseScale;
        Vector3 _spawnPos, _spawnRight = Vector3.right;
        bool _hasSpawn, _hovered, _placedFallback;
        float _lastPress = -10f, _pressedUntil = -1f, _startTime;
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

        void OnEnable() => whistleAction?.Enable();
        void OnDisable() => whistleAction?.Disable();

        // ---------------------------------------------------------------- public API

        /// <summary>Whistle! (Interaction SDK WhenSelect, mouse, F / B, or any UnityEvent.)</summary>
        public void Press()
        {
            if (Time.time - _lastPress < debounce) return;
            _lastPress = Time.time;
            _pressedUntil = Time.time + 0.12f;
            onPressed.Invoke();
            Pressed?.Invoke();
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

        /// <summary>Lay the button on the desk's edge nearest the head, to the user's right (left if leftHanded), tilted toward them.</summary>
        public void PlaceOnDesk(GatorNavSurface desk, Transform head)
        {
            if (desk == null || !desk.IsBuilt || head == null) return;
            Quaternion rot = desk.Rotation;
            Vector3 top = desk.TopCenter;
            float hx = desk.Size.x * 0.5f, hz = desk.Size.y * 0.5f;
            Vector3 l = Quaternion.Inverse(rot) * (head.position - top);

            // Near edge = the side the head is beyond the most (relative to the half extents).
            Vector3 outward, along;
            float halfAlong, edgeDist;
            if (Mathf.Abs(l.z) / Mathf.Max(0.01f, hz) >= Mathf.Abs(l.x) / Mathf.Max(0.01f, hx))
            { outward = new Vector3(0f, 0f, Mathf.Sign(l.z)); along = Vector3.right; halfAlong = hx; edgeDist = hz; }
            else
            { outward = new Vector3(Mathf.Sign(l.x), 0f, 0f); along = Vector3.forward; halfAlong = hz; edgeDist = hx; }

            Vector3 outwardW = rot * outward;
            Vector3 rightW = Vector3.Cross(Vector3.up, -outwardW);           // the user's right while facing the desk
            if (leftHanded) rightW = -rightW;
            float sideSign = Vector3.Dot(rot * along, rightW) >= 0f ? 1f : -1f;

            float lateral = Vector3.Dot(l, along) + sideSign * lateralOffset;
            float margin = Mathf.Max(0.05f, _collider.size.x * transform.lossyScale.x);
            lateral = Mathf.Clamp(lateral, -halfAlong + margin, halfAlong - margin);
            float inward = Mathf.Max(0f, edgeDist - edgeInset);

            Vector3 local = along * lateral + outward * inward;
            Vector3 tiltAxis = Vector3.Cross(Vector3.up, outwardW);
            Vector3 up = Quaternion.AngleAxis(tiltDegrees, tiltAxis) * Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(outwardW, up);
            float lift = _collider.size.y * transform.lossyScale.y * 0.5f
                       + Mathf.Sin(tiltDegrees * Mathf.Deg2Rad) * _collider.size.z * transform.lossyScale.z * 0.5f;

            transform.SetPositionAndRotation(top + rot * local + Vector3.up * lift, Quaternion.LookRotation(fwd, up));
            PlacedOnDesk = true;
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (autoPlace && !PlacedOnDesk) TryAutoPlace();

            if (whistleAction != null && whistleAction.WasPressedThisFrame() && !TypingInField()) Press();

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) TryMousePress(mouse);

            if (visual != null)
                visual.transform.localPosition = Time.time < _pressedUntil ? _visualRest - Vector3.up * 0.006f : _visualRest;
        }

        void TryAutoPlace()
        {
            GatorNavSurface desk = GatorNavSurface.Desk;
            Transform head = PetUser.Head;
            if (desk != null && desk.IsBuilt && head != null) { PlaceOnDesk(desk, head); return; }
            if (_placedFallback || !_hasSpawn || Time.time - _startTime < noDeskFallbackSeconds) return;
            Vector3 right = Vector3.ProjectOnPlane(leftHanded ? -_spawnRight : _spawnRight, Vector3.up);
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            right.Normalize();
            Vector3 lift = Vector3.up * (_collider.size.y * transform.lossyScale.y * 0.5f);
            transform.SetPositionAndRotation(_spawnPos + right * spawnRightOffset + lift, Quaternion.LookRotation(Vector3.Cross(right, Vector3.up), Vector3.up));
            _placedFallback = true;                  // keeps checking for a desk (MRUK may find one later)
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
