using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// The crew egg on the desk's near-LEFT corner (the whistle has the right one): a white egg with "Spawn Crew Agent"
    /// above it. Pressing the egg opens a carousel picker over it:
    ///
    ///        <   Builder   [ Fixer ]   Tester   >          (neighbours faded, the centre in its type colour)
    ///             hunts down bugs and fixes the root cause
    ///                        [ Hatch ]                     ("Send home" if that agent is already out)
    ///
    /// < / > browse (with a short slide), Hatch hatches it (GatorCrew.Spawn) and closes the picker. The egg toggles the
    /// picker; it also closes after closeAfter seconds untouched. Inputs, all ending in TogglePicker / Previous / Next /
    /// Confirm: Meta Interaction SDK poke/ray wrappers (wired by Forge/Pet/7. Add Agent Crew To Open Scene, no Meta
    /// types here) and mouse clicks. The picker billboards toward the user (yaw only). No allocations per frame.
    /// Gate: while GatorCrew.CrewAllowed is false (the code mode is closed in Main) the whole egg is hidden and inert;
    /// it comes back when the mode opens. Sandbox scenes have no Workspace, so the egg is always out there.
    /// Once the whistle sits by Hudson's DeskDock, the egg sits just past the whistle (away from the dock) and slides with it.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(60)]   // LateUpdate after WhistleButton's, which follows the dock
    public class GatorHatchery : MonoBehaviour
    {
        [SerializeField, Tooltip("Egg renderer (white). Empty = first child Renderer.")]
        Renderer egg;
        [SerializeField, Tooltip("Picker root (child): shown while picking, billboards toward the user.")]
        Transform picker;
        [SerializeField] Collider prevButton, nextButton, hatchButton;
        [SerializeField, Tooltip("Label renderer on the Hatch button (tinted with the shown type's colour).")]
        Renderer hatchVisual;
        [SerializeField, Tooltip("Ignore presses closer together than this (s).")]
        float debounce = 0.3f;
        [SerializeField, Tooltip("Close the picker after this many seconds without a press.")]
        float closeAfter = 30f;
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
        [SerializeField, Tooltip("While the whistle sits by the DeskDock, sit just past it (away from the dock) and slide with it.")]
        bool besideWhistle = true;
        [SerializeField, Tooltip("Gap (m) between the whistle's edge and the egg's.")]
        float whistleGap = 0.015f;

        [Header("Look")]
        [SerializeField, Tooltip("\"Spawn Crew Agent\" height above the egg (m).")]
        float labelHeight = 0.075f;
        [SerializeField, Tooltip("Picker height above the egg (m, its centre).")]
        float pickerHeight = 0.2f;

        const float SideX = 0.155f, SlideSeconds = 0.2f;
        static readonly Color EggWhite = new Color(0.97f, 0.96f, 0.93f);
        static readonly Color32 Outline = new Color32(20, 30, 25, 255);
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public bool Placed { get; private set; }
        public bool PickerOpen => picker != null && picker.gameObject.activeSelf;
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
        TextMeshPro _title, _name, _prevName, _nextName, _desc, _count, _hatchLabel;
        GatorCrew _crew;
        Transform _eggT;
        Vector3 _eggRest, _eggScale;
        int _index;
        float _lastPress = -10f, _pressedUntil = -1f, _startTime, _nextPlaceCheck, _lastUse, _slide;
        bool _placedFallback, _hasSpawn, _gated, _byWhistle;
        Vector3 _spawnPos, _spawnRight = Vector3.right;

        void Awake()
        {
            _collider = GetComponent<BoxCollider>();
            _collider.isTrigger = true;
            if (egg == null) egg = GetComponentInChildren<Renderer>(true);
            if (egg != null) { _eggT = egg.transform; _eggRest = _eggT.localPosition; _eggScale = _eggT.localScale; }
            _mpb = new MaterialPropertyBlock();
            Tint(egg, EggWhite);
            _title = Label(transform, "SpawnLabel", "Spawn Crew Agent", 0.2f, new Vector2(0.4f, 0.06f), Color.white);
            _title.transform.localPosition = Vector3.up * labelHeight;
            BuildPicker();
            if (picker != null) picker.gameObject.SetActive(false);
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

        // ---------------------------------------------------------------- public API (UnityEvents / ISDK wrappers)

        /// <summary>Hatching is blocked right now (GatorCrew.CrewAllowed is false: the code mode is closed).</summary>
        public bool Gated => _gated;

        /// <summary>The egg: open or close the picker.</summary>
        public void TogglePicker()
        {
            if (_gated || !Debounced() || picker == null) return;
            bool open = !PickerOpen;
            picker.gameObject.SetActive(open);
            _title.gameObject.SetActive(!open);
            if (open) { BindCrew(); _slide = 0f; Refresh(); FacePicker(); }
        }

        public void Previous() => Step(-1);
        public void Next() => Step(1);

        /// <summary>Hatch the shown agent (then close), or send it home if it's already out.</summary>
        public void Confirm()
        {
            if (_gated || !PickerOpen || !Debounced()) return;
            BindCrew();
            if (_crew == null) { Debug.LogWarning("[GatorHatchery] No GatorCrew in the scene.", this); return; }
            GatorAgentType t = Shown;
            if (_crew.IsHatched(t)) { _crew.Dismiss(t.Id); Refresh(); return; }
            if (_crew.Spawn(t, SpawnPoint) == null) return;
            picker.gameObject.SetActive(false);
            _title.gameObject.SetActive(true);
            GatorAgentType next = _crew.NextFree(_index + 1);   // the next free agent is ready for the next visit
            if (next != null) _index = GatorAgentTypes.IndexOf(next);
        }

        public void HoverOn() { if (_eggT != null) _eggT.localScale = _eggScale * 1.08f; }
        public void HoverOff() { if (_eggT != null) _eggT.localScale = _eggScale; }

        void Step(int dir)
        {
            if (_gated || !PickerOpen || !Debounced()) return;
            int n = GatorAgentTypes.All.Length;
            _index = (_index + dir + n) % n;
            _slide = dir;                                   // slides in from that side
            Refresh();
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (Time.time >= _nextPlaceCheck)
            {
                _nextPlaceCheck = Time.time + 0.5f;
                if (autoPlace && !_byWhistle) UpdatePlacement();
                BindCrew();
            }
            bool gated = _crew != null && !_crew.CrewAllowed;
            if (gated != _gated) ApplyGate(gated);
            if (_gated) return;

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) TryMousePress(mouse);

            if (_eggT != null)
            {
                float wobble = Mathf.Sin(Time.time * 2.3f) * (PickerOpen ? 1.5f : 4f);
                _eggT.localRotation = Quaternion.Euler(0f, 0f, wobble);
                _eggT.localPosition = Time.time < _pressedUntil ? _eggRest - Vector3.up * 0.006f : _eggRest;
            }
            FaceLabel(_title.transform);

            if (!PickerOpen) return;
            if (Time.time - _lastUse > closeAfter) { picker.gameObject.SetActive(false); _title.gameObject.SetActive(true); return; }
            FacePicker();
            if (_slide != 0f)
            {
                _slide = Mathf.MoveTowards(_slide, 0f, Time.deltaTime / SlideSeconds);
                LayoutCarousel();
            }
        }

        void LateUpdate()
        {
            _byWhistle = autoPlace && besideWhistle && FollowWhistle();
        }

        // Hide the egg, its label, the picker and the poke / ray children while gated; put them back when the mode opens.
        void ApplyGate(bool gated)
        {
            _gated = gated;
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform c = transform.GetChild(i);
                if (c == picker) continue;
                c.gameObject.SetActive(!gated);
            }
            if (picker != null) picker.gameObject.SetActive(false);
            if (_title != null) _title.gameObject.SetActive(!gated);
            if (_collider != null) _collider.enabled = !gated;
            if (!gated) Refresh();
        }

        bool Debounced()
        {
            if (Time.time - _lastPress < debounce) return false;
            _lastPress = _lastUse = Time.time;
            _pressedUntil = Time.time + 0.12f;
            return true;
        }

        void BindCrew()
        {
            if (_crew != null) return;
            _crew = GatorCrew.Instance != null ? GatorCrew.Instance : FindAnyObjectByType<GatorCrew>();
            if (_crew != null) _crew.Changed += Refresh;
        }

        // ---------------------------------------------------------------- picker

        // Text and colours for the shown agent (only on changes).
        void Refresh()
        {
            if (_name == null) return;
            var all = GatorAgentTypes.All;
            int n = all.Length;
            GatorAgentType t = Shown, prev = all[(_index - 1 + n) % n], next = all[(_index + 1) % n];
            bool out_ = _crew != null && _crew.IsHatched(t);
            _name.text = t.Name;
            _name.color = Readable(t.Color);
            _prevName.text = prev.Name;
            _nextName.text = next.Name;
            _desc.text = out_ ? t.Name + " is already out. Send it home?" : char.ToUpper(t.Role[0]) + t.Role.Substring(1) + ".";
            _count.text = (_index + 1) + " / " + n;
            _hatchLabel.text = out_ ? "Send home" : "Hatch";
            Tint(hatchVisual, out_ ? new Color(0.55f, 0.56f, 0.6f) : Color.Lerp(t.Color, Color.white, 0.15f));
            LayoutCarousel();
        }

        // Centre name + faded neighbours, offset by the slide (1 → 0) so a step glides in.
        void LayoutCarousel()
        {
            float o = _slide * SideX;
            _name.transform.localPosition = new Vector3(o, 0.035f, 0f);
            _prevName.transform.localPosition = new Vector3(-SideX + o, 0.035f, 0f);
            _nextName.transform.localPosition = new Vector3(SideX + o, 0.035f, 0f);
            float fade = 1f - Mathf.Abs(_slide);
            _name.alpha = 0.35f + 0.65f * fade;
            _desc.alpha = fade;
        }

        void BuildPicker()
        {
            if (picker == null)
            {
                picker = new GameObject("Picker").transform;
                picker.SetParent(transform, false);
            }
            picker.localPosition = Vector3.up * pickerHeight;
            Transform panel = picker.Find("Panel");
            if (panel == null) BuildPanel();
            Color dim = new Color(1f, 1f, 1f, 0.45f);
            _name = Label(picker, "Name", "", 0.28f, new Vector2(0.16f, 0.05f), Color.white);
            _prevName = Label(picker, "PrevName", "", 0.15f, new Vector2(0.1f, 0.04f), dim);
            _nextName = Label(picker, "NextName", "", 0.15f, new Vector2(0.1f, 0.04f), dim);
            _desc = Label(picker, "Description", "", 0.15f, new Vector2(0.34f, 0.05f), new Color(1f, 1f, 1f, 0.9f));
            _desc.textWrappingMode = TextWrappingModes.Normal;
            _desc.alignment = TextAlignmentOptions.Top;
            _desc.rectTransform.pivot = new Vector2(0.5f, 1f);
            _desc.transform.localPosition = new Vector3(0f, 0.012f, 0f);
            _count = Label(picker, "Count", "", 0.11f, new Vector2(0.1f, 0.02f), new Color(1f, 1f, 1f, 0.5f));
            _count.transform.localPosition = new Vector3(0f, -0.118f, 0f);
            Transform hatchT = hatchButton != null ? hatchButton.transform : null;
            _hatchLabel = Label(hatchT != null ? hatchT : picker, "HatchLabel", "Hatch", 0.17f, new Vector2(0.1f, 0.03f), Color.white);
            _hatchLabel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _hatchLabel.alignment = TextAlignmentOptions.Center;
            _hatchLabel.transform.localPosition = new Vector3(0f, 0f, -0.006f);
            ArrowLabel(prevButton, "<");
            ArrowLabel(nextButton, ">");
        }

        // Dark rounded panel behind the carousel (sprite like GatorCaption's bubble, built once).
        void BuildPanel()
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) return;
            var go = new GameObject("Panel");
            go.transform.SetParent(picker, false);
            go.transform.localPosition = new Vector3(0f, 0.01f, 0.004f);   // just behind the text
            var sr = go.AddComponent<SpriteRenderer>();
            const int n = 32, r = 12;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Max(Mathf.Abs(x + 0.5f - n * 0.5f) - (n * 0.5f - r), 0f);
                    float cy = Mathf.Max(Mathf.Abs(y + 0.5f - n * 0.5f) - (n * 0.5f - r), 0f);
                    float d = Mathf.Sqrt(cx * cx + cy * cy) - r;
                    px[y * n + x] = new Color(0.1f, 0.12f, 0.13f, 0.85f * Mathf.Clamp01(0.5f - d));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), r / 0.02f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(0.44f, 0.12f);
            sr.sharedMaterial = new Material(sh) { renderQueue = 2990 };
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void ArrowLabel(Collider button, string glyph)
        {
            if (button == null) return;
            TextMeshPro t = Label(button.transform, "Glyph", glyph, 0.32f, new Vector2(0.04f, 0.05f), Color.white);
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            t.alignment = TextAlignmentOptions.Center;
            t.transform.localPosition = new Vector3(0f, 0.002f, -0.006f);
        }

        // 3D text in metres with a dark outline, Fredoka like the name tags. Re-uses an existing child of that name.
        TextMeshPro Label(Transform parent, string goName, string text, float size, Vector2 box, Color color)
        {
            Transform existing = parent.Find(goName);
            if (existing != null) Destroy(existing.gameObject);
            var go = new GameObject(goName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            float s = parent.lossyScale.x;
            if (s > 0.0001f) go.transform.localScale = Vector3.one / s;
            var t = go.AddComponent<TextMeshPro>();
            t.rectTransform.sizeDelta = box;
            t.rectTransform.pivot = new Vector2(0.5f, 0f);
            t.alignment = TextAlignmentOptions.Bottom;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.fontSize = size;
            t.richText = false;
            TMP_FontAsset f = GatorCaption.DefaultFont();
            if (f != null) t.font = f;
            t.outlineWidth = 0.2f;
            t.outlineColor = Outline;
            t.color = color;
            t.text = text;
            return t;
        }

        static Color Readable(Color c) => c.grayscale < 0.35f ? Color.Lerp(c, Color.white, 0.45f) : c;

        void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, c);
            _mpb.SetColor(ColorId, c);
            r.SetPropertyBlock(_mpb);
        }

        void FaceLabel(Transform t)
        {
            if (t == null || !t.gameObject.activeSelf) return;
            Transform head = PetUser.Head;
            if (head == null) return;
            Vector3 dir = t.position - head.position;   // TMP faces -Z: +Z points away from the viewer
            if (dir.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // Yaw only, so the buttons stay upright and pokeable.
        void FacePicker()
        {
            Transform head = PetUser.Head;
            if (head == null) return;
            Vector3 dir = picker.position - head.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) picker.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        void TryMousePress(Mouse mouse)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null) return;
            Ray ray = Cursor.lockState == CursorLockMode.Locked
                ? cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : cam.ScreenPointToRay(mouse.position.ReadValue());
            if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Collide)) return;
            Collider c = hit.collider;
            if (c == _collider) TogglePicker();
            else if (!PickerOpen) return;
            else if (c == prevButton) Previous();
            else if (c == nextButton) Next();
            else if (c == hatchButton) Confirm();
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

        // Every frame while a whistle sits by the dock: just past it on the side away from the dock, level and facing the
        // user like the egg's corner spot, so it slides with the dock (‹ ›, grip drag) right after the whistle does. No room
        // past the whistle at the desk's end: just behind it (into the desk) instead.
        bool FollowWhistle()
        {
            var all = WhistleButton.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].TryGetDockSpot(out Vector3 at, out Vector3 away, out float whistleHalf)) continue;
                float half = _collider.size.x * transform.lossyScale.x * 0.5f;
                Vector3 inward = Vector3.Cross(away, Vector3.up);
                Vector3 pos = at + away * (whistleHalf + whistleGap + half);
                GatorNavSurface desk = GatorNavSurface.Desk;
                if (desk != null && desk.IsBuilt
                    && (DeskCorner.ClampInside(desk.TopCenter, desk.Rotation, desk.Size, pos, half) - pos).sqrMagnitude > 0.005f * 0.005f)
                    pos = at + inward * (whistleHalf + whistleGap + half);   // ponytail: assumes the whistle is about as deep as wide
                pos += Vector3.up * (_collider.size.y * transform.lossyScale.y * 0.5f);
                Quaternion r = Quaternion.LookRotation(-inward, Vector3.up);
                if ((pos - transform.position).sqrMagnitude > 0.0005f * 0.0005f || Quaternion.Angle(r, transform.rotation) > 0.1f)
                    transform.SetPositionAndRotation(pos, r);
                Placed = true;
                _placedFallback = false;
                return true;
            }
            return false;
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
