using TMPro;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// World-space speech bubble above the gator: rounded pastel-green bubble with a green outline and green text in a
    /// rounded font (Fredoka, loaded from Resources/PetFonts as a dynamic TMP font). Creates its own TextMeshPro + bubble
    /// if no text is assigned, billboards toward PetUser.Head every LateUpdate, auto-hides final text.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorCaption : MonoBehaviour
    {
        [Header("Placement")]
        [SerializeField, Tooltip("Optional existing 3D TextMeshPro. Empty = one is created at runtime.")]
        TextMeshPro text;
        [SerializeField, Tooltip("Point the caption follows. Empty = this transform.")]
        Transform anchor;
        [SerializeField, Tooltip("World-space offset from the anchor (m).")]
        Vector3 offset = new Vector3(0f, 0.35f, 0f);

        [Header("Look")]
        [SerializeField, Tooltip("Word-wrap width (m).")]
        float wrapWidth = 0.6f;
        [SerializeField, Tooltip("TMP font size for a 3D text (0.35 ~ 3.5 cm cap height).")]
        float fontSize = 0.35f;
        [SerializeField, Tooltip("Text colour (same green as the outline by default).")]
        Color bubbleTextColor = new Color(0.13f, 0.48f, 0.24f);
        [SerializeField, Tooltip("Bubble outline colour.")]
        Color outlineColor = new Color(0.13f, 0.48f, 0.24f);
        [SerializeField, Tooltip("Bubble fill colour (pastel green; alpha < 1 lets passthrough show a little).")]
        Color fillColor = new Color(0.86f, 0.96f, 0.87f, 0.96f);
        [SerializeField, Tooltip("Font for the bubble. Empty = Fredoka (Resources/PetFonts/Fredoka-Medium) as a dynamic TMP font.")]
        TMP_FontAsset font;
        [SerializeField, Tooltip("Draw the rounded bubble behind the text (readable on passthrough).")]
        bool showBackground = true;
        [SerializeField, Tooltip("Bubble corner radius and outline width (m).")]
        float cornerRadius = 0.02f, outlineWidth = 0.0025f;
        [SerializeField, Tooltip("Bubble padding around the text (m).")]
        Vector2 padding = new Vector2(0.03f, 0.02f);

        [Header("Behaviour")]
        [SerializeField, Tooltip("Hide final (non-partial) text after this many seconds. 0 = never.")]
        float autoHideSeconds = 6f;
        [SerializeField, Range(0f, 1f), Tooltip("Alpha of partial (still-updating) text.")]
        float partialAlpha = 0.7f;

        const string DefaultFontPath = "PetFonts/Fredoka-Medium";
        const int BubbleTexSize = 64, BubbleRadiusPx = 24;
        static TMP_FontAsset s_defaultFont;

        Transform root;          // the object we position/billboard and show/hide
        SpriteRenderer background;
        float hideAt = -1f;
        bool built, muted;

        /// <summary>Muted: SetText shows nothing (crew gators while the code mode is closed; Bit while the arrival card is up).</summary>
        public bool Muted
        {
            get => muted;
            set
            {
                if (muted == value) return;
                muted = value;
                if (muted) Clear();
            }
        }

        void Awake() => EnsureBuilt();

        /// <summary>Show text. partial = dimmed italic, never auto-hides (keeps being updated). Ignored while Muted.</summary>
        public void SetText(string value, bool partial)
        {
            EnsureBuilt();
            if (string.IsNullOrEmpty(value) || muted) { Clear(); return; }

            root.gameObject.SetActive(true);
            text.text = value;
            text.fontStyle = partial ? FontStyles.Italic : FontStyles.Normal;
            Color c = bubbleTextColor;
            c.a *= partial ? partialAlpha : 1f;
            text.color = c;
            hideAt = (!partial && autoHideSeconds > 0f) ? Time.time + autoHideSeconds : -1f;

            UpdateBackground();
            LateUpdate();        // place immediately so it never pops in at the wrong spot
        }

        public void Clear()
        {
            EnsureBuilt();
            text.text = string.Empty;
            hideAt = -1f;
            root.gameObject.SetActive(false);
        }

        void EnsureBuilt()
        {
            if (built) return;
            built = true;
            if (anchor == null) anchor = transform;

            if (text == null)
            {
                var go = new GameObject("GatorCaption", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                // Cancel the pet's scale so sizes stay in metres (assumes uniform scale).
                float s = transform.lossyScale.x;
                if (s > 0.0001f) go.transform.localScale = Vector3.one / s;
                text = go.AddComponent<TextMeshPro>();
                text.rectTransform.sizeDelta = new Vector2(wrapWidth, 0.2f);
                text.rectTransform.pivot = new Vector2(0.5f, 0f);       // grow upward from the anchor
                text.alignment = TextAlignmentOptions.Bottom;
                text.textWrappingMode = TextWrappingModes.Normal;
                text.overflowMode = TextOverflowModes.Overflow;
                text.fontSize = fontSize;
                text.richText = false;                                  // agent text may contain '<'
                text.color = bubbleTextColor;
            }
            TMP_FontAsset f = font != null ? font : DefaultFont();
            if (f != null) text.font = f;
            root = text.transform;

            if (showBackground) BuildBackground();
            root.gameObject.SetActive(false);
        }

        // Rounded bubble: a small generated texture (fill + outline baked in, from the serialized colours), 9-sliced so
        // the corners keep their radius at any text size. Built once per caption.
        void BuildBackground()
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) return;
            var go = new GameObject("CaptionBubble");
            go.transform.SetParent(root, false);
            background = go.AddComponent<SpriteRenderer>();
            background.sprite = BuildBubbleSprite();
            background.drawMode = SpriteDrawMode.Sliced;
            background.sharedMaterial = new Material(sh) { renderQueue = 2990 };   // draw before TMP (3000)
            background.sortingOrder = -1;
            background.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            background.receiveShadows = false;
        }

        Sprite BuildBubbleSprite()
        {
            int n = BubbleTexSize, r = BubbleRadiusPx;
            float ppu = r / Mathf.Max(0.001f, cornerRadius);                  // texture pixels per metre
            float border = Mathf.Max(1f, outlineWidth * ppu);
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            Color clear = new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // Signed distance (px) to the rounded square's edge; negative inside.
                    float cx = Mathf.Max(Mathf.Abs(x + 0.5f - n * 0.5f) - (n * 0.5f - r), 0f);
                    float cy = Mathf.Max(Mathf.Abs(y + 0.5f - n * 0.5f) - (n * 0.5f - r), 0f);
                    float d = Mathf.Sqrt(cx * cx + cy * cy) - r;
                    Color c = Color.Lerp(fillColor, outlineColor, Mathf.Clamp01(d + border + 0.5f));   // fill → outline
                    px[y * n + x] = Color.Lerp(c, clear, Mathf.Clamp01(d + 0.5f));                     // anti-aliased edge
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        // Fit the bubble to the rendered text bounds (called only when text changes).
        void UpdateBackground()
        {
            if (background == null) return;
            text.ForceMeshUpdate();
            Bounds b = text.textBounds;
            background.transform.localPosition = new Vector3(b.center.x, b.center.y, 0.005f);   // just behind the text
            float min = 2f * cornerRadius;
            background.size = new Vector2(Mathf.Max(min, b.size.x + padding.x * 2f), Mathf.Max(min, b.size.y + padding.y * 2f));
        }

        /// <summary>Fredoka as a dynamic TMP font (shared with other pet labels, e.g. GatorHatchery).</summary>
        public static TMP_FontAsset DefaultFont()
        {
            if (s_defaultFont != null) return s_defaultFont;
            Font ttf = Resources.Load<Font>(DefaultFontPath);
            if (ttf == null) { Debug.LogWarning("[GatorCaption] Font Resources/" + DefaultFontPath + " missing; using the TMP default."); return null; }
            s_defaultFont = TMP_FontAsset.CreateFontAsset(ttf);            // dynamic SDF atlas, glyphs added on demand
            if (s_defaultFont != null) s_defaultFont.name = "Fredoka-Medium (dynamic)";
            return s_defaultFont;
        }

        void LateUpdate()
        {
            if (!built || !root.gameObject.activeSelf) return;

            if (hideAt > 0f && Time.time >= hideAt) { Clear(); return; }

            root.position = anchor.position + offset;
            Transform head = PetUser.Head;
            if (head == null) return;
            Vector3 dir = root.position - head.position;   // TMP faces -Z, so +Z points away from viewer
            if (dir.sqrMagnitude > 0.0001f) root.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }
    }
}
