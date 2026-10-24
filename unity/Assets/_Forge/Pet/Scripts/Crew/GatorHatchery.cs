using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// The crew egg on the desk's near-LEFT corner (the whistle has the right one). A label above it shows one agent
    /// type (name + what it does) in that type's colour:
    ///  * press the egg   → hatch that type (GatorCrew.Spawn), or send it home if it's already out;
    ///  * press the arrow → show the next type.
    /// Inputs, all ending in Hatch() / NextType(): Meta Interaction SDK poke/ray wrappers (wired by
    /// Forge/Pet/7. Add Agent Crew To Open Scene, no Meta types here), mouse click, and GatorCrew's N key.
    /// No desk yet: 0.3 m left of the gator's spawn. No allocations per frame (the label only changes on events).
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [DisallowMultipleComponent]
    public class GatorHatchery : MonoBehaviour
    {
        [SerializeField, Tooltip("Egg renderer (tinted with the shown type's colour). Empty = first child Renderer.")]
        Renderer egg;
        [SerializeField, Tooltip("Collider of the 'next type' arrow button (a child).")]
        Collider nextButton;
        [SerializeField, Tooltip("Ignore presses closer together than this (s).")]
        float debounce = 0.35f;
        [SerializeField, Tooltip("Max mouse raycast distance (m).")]
        float maxDistance = 5f;
        [SerializeField] LayerMask raycastMask = ~0;

        [Header("Placement")]
        [SerializeField] bool autoPlace = true;
        [SerializeField, Tooltip("Distance (m) in from both edges of the near-left corner.")]
        float cornerInset = 0.09f;
        [SerializeField, Tooltip("Re-place (other side of the desk) when the head comes within this distance (m) of it.")]
        float nearTableDistance = 1.2f;
        [SerializeField, Tooltip("Put it on the user's right instead (if the whistle lives on the left).")]
        bool rightSide = false;
        [SerializeField, Tooltip("No desk after this many seconds: place next to the gator spawn.")]
        float noDeskFallbackSeconds = 2f;

        [Header("Label")]
        [SerializeField, Tooltip("Label height above the egg (m).")]
        float labelHeight = 0.11f;
        [SerializeField] float labelFontSize = 0.22f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public bool Placed { get; private set; }
        public GatorAgentType Shown => GatorAgentTypes.All[_index];
        /// <summary>Where a hatched gator appears: on the desk next to the egg, toward the desk's middle.</summary>
        public Vector3 SpawnPoint
        {
            get
            {
                GatorNavSurface desk = GatorNavSurface.Desk;
                if (desk == null || !desk.IsBuilt) return transform.position;
                Vector3 toMid = desk.TopCenter - transform.position; toMid.y = 0f;
                Vector3 p = transform.position + (toMid.sqrMagnitude > 0.0001f ? toMid.normalized * 0.1f : Vector3.zero);
                p.y = desk.TopCenter.y;
                return p;
            }
        }

        BoxCollider _collider;
        MaterialPropertyBlock _mpb;
        TextMeshPro _label;
        GatorCrew _crew;
        Transform _eggT;
        Vector3 _eggRest, _eggScale;
        int _index;
        float _lastPress = -10f, _pressedUntil = -1f, _startTime, _nextPlaceCheck;
        bool _hovered, _placedFallback, _hasSpawn;
        Vector3 _spawnPos, _spawnRight = Vector3.right;

        void Awake()
        {
            _collider = GetComponent<BoxCollider>();
            _collider.isTrigger = true;
            if (egg == null) egg = GetComponentInChildren<Renderer>(true);
            if (egg != null) { _eggT = egg.transform; _eggRest = _eggT.localPosition; _eggScale = _eggT.localScale; }
            _mpb = new MaterialPropertyBlock();
            BuildLabel();
        }

        void Start()
        {
            _startTime = Time.time;
            GatorController lead = GatorCrew.Instance != null ? GatorCrew.Instance.Lead : FindAnyObjectByType<GatorController>();
            if (lead != null) { _spawnPos = lead.transform.position; _spawnRight = lead.transform.right; _hasSpawn = true; }
            BindCrew();
            Refresh();
        }

        void OnDestroy()
        {
            if (_crew != null) _crew.Changed -= Refresh;
        }

        // ---------------------------------------------------------------- public API

        /// <summary>Hatch the shown type, or send it home if it's already out.</summary>
        public void Hatch()
        {
            if (!Debounced()) return;
            BindCrew();
            if (_crew == null) { Debug.LogWarning("[GatorHatchery] No GatorCrew in the scene.", this); return; }
            GatorAgentType t = Shown;
            if (_crew.IsHatched(t)) _crew.Dismiss(t.Id);
            else if (_crew.Spawn(t, SpawnPoint) != null)
            {
                GatorAgentType next = _crew.NextFree(_index + 1);   // ready for the next press
                if (next != null) _index = GatorAgentTypes.IndexOf(next);
            }
            Refresh();
        }

        /// <summary>Show the next agent type on the label.</summary>
        public void NextType()
        {
            if (!Debounced()) return;
            _index = (_index + 1) % GatorAgentTypes.All.Length;
            Refresh();
        }

        public void HoverOn() => SetHovered(true);
        public void HoverOff() => SetHovered(false);

        public void SetHovered(bool hovered)
        {
            _hovered = hovered;
            if (_eggT != null) _eggT.localScale = _hovered ? _eggScale * 1.08f : _eggScale;
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (autoPlace && Time.time >= _nextPlaceCheck) { _nextPlaceCheck = Time.time + 0.5f; UpdatePlacement(); }

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) TryMousePress(mouse);

            if (_eggT != null)
            {
                // Idle wobble, a quick dip when pressed.
                float wobble = Mathf.Sin(Time.time * 2.3f) * 4f;
                _eggT.localRotation = Quaternion.Euler(0f, 0f, wobble);
                _eggT.localPosition = Time.time < _pressedUntil ? _eggRest - Vector3.up * 0.006f : _eggRest;
            }
            if (_label != null && _label.gameObject.activeSelf) FaceLabel();
        }

        bool Debounced()
        {
            if (Time.time - _lastPress < debounce) return false;
            _lastPress = Time.time;
            _pressedUntil = Time.time + 0.12f;
            return true;
        }

        void BindCrew()
        {
            if (_crew != null) return;
            _crew = GatorCrew.Instance != null ? GatorCrew.Instance : FindAnyObjectByType<GatorCrew>();
            if (_crew != null) _crew.Changed += Refresh;
        }

        // Label + egg colour for the shown type (only on changes).
        void Refresh()
        {
            GatorAgentType t = Shown;
            bool out_ = _crew != null && _crew.IsHatched(t);
            if (_label != null)
            {
                _label.text = out_ ? t.Name + " is out\n<size=70%>press to send home</size>"
                                   : t.Name + "\n<size=70%>" + t.Role + "</size>";
                _label.color = Color.Lerp(t.Color, Color.black, t.Color.grayscale > 0.75f ? 0.45f : 0.15f);
            }
            if (egg != null)
            {
                Color c = out_ ? Color.Lerp(t.Color, new Color(0.6f, 0.6f, 0.62f), 0.65f) : Color.Lerp(t.Color, Color.white, 0.35f);
                egg.GetPropertyBlock(_mpb);
                _mpb.SetColor(BaseColorId, c);
                _mpb.SetColor(ColorId, c);
                egg.SetPropertyBlock(_mpb);
            }
        }

        void BuildLabel()
        {
            var go = new GameObject("HatcheryLabel", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            float s = transform.lossyScale.x;
            if (s > 0.0001f) go.transform.localScale = Vector3.one / s;
            _label = go.AddComponent<TextMeshPro>();
            _label.rectTransform.sizeDelta = new Vector2(0.4f, 0.1f);
            _label.rectTransform.pivot = new Vector2(0.5f, 0f);
            _label.alignment = TextAlignmentOptions.Bottom;
            _label.textWrappingMode = TextWrappingModes.Normal;
            _label.fontSize = labelFontSize;
            _label.richText = true;
            TMP_FontAsset f = GatorCaption.DefaultFont();
            if (f != null) _label.font = f;
        }

        void FaceLabel()
        {
            Transform lt = _label.transform;
            lt.position = transform.position + Vector3.up * labelHeight;
            Transform head = PetUser.Head;
            if (head == null) return;
            Vector3 dir = lt.position - head.position;
            if (dir.sqrMagnitude > 0.0001f) lt.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        void TryMousePress(Mouse mouse)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null) return;
            Ray ray = Cursor.lockState == CursorLockMode.Locked
                ? cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : cam.ScreenPointToRay(mouse.position.ReadValue());
            if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Collide)) return;
            if (nextButton != null && hit.collider == nextButton) NextType();
            else if (hit.collider == _collider) Hatch();
        }

        // ---------------------------------------------------------------- placement (mirrors WhistleButton)

        void UpdatePlacement()
        {
            Transform head = PetUser.Head;
            if (head == null) return;
            GatorNavSurface desk = GatorNavSurface.Desk;
            if (desk == null || !desk.IsBuilt) { PlaceFallback(); return; }
            Vector3 top = desk.TopCenter; Quaternion rot = desk.Rotation; Vector2 size = desk.Size;
            if (Placed && !_placedFallback && DeskCorner.HorizontalDistance(top, rot, size, head.position) > nearTableDistance) return;
            if (!DeskCorner.TryPose(top, rot, size, head.position, cornerInset, !rightSide, 0f, out Vector3 pos, out Quaternion r)) return;
            pos += Vector3.up * (_collider.size.y * transform.lossyScale.y * 0.5f);
            if (Placed && !_placedFallback && (pos - transform.position).sqrMagnitude < 0.05f * 0.05f) return;
            transform.SetPositionAndRotation(pos, r);
            Placed = true;
            _placedFallback = false;
        }

        void PlaceFallback()
        {
            if (Placed || !_hasSpawn || Time.time - _startTime < noDeskFallbackSeconds) return;
            Vector3 right = Vector3.ProjectOnPlane(_spawnRight, Vector3.up);
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            right.Normalize();
            Vector3 side = rightSide ? right : -right;
            Vector3 lift = Vector3.up * (_collider.size.y * transform.lossyScale.y * 0.5f);
            transform.SetPositionAndRotation(_spawnPos + side * 0.3f + lift, Quaternion.LookRotation(Vector3.Cross(right, Vector3.up), Vector3.up));
            Placed = true;
            _placedFallback = true;
        }
    }
}
