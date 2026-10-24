using TMPro;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// World-space caption bubble above the gator. Creates its own TextMeshPro + dark backing quad
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
        [SerializeField] Color textColor = Color.white;
        [SerializeField, Tooltip("Draw a semi-transparent quad behind the text (readable on passthrough).")]
        bool showBackground = true;
        [SerializeField] Color backgroundColor = new Color(0f, 0f, 0f, 0.6f);
        [SerializeField, Tooltip("Optional background material. Empty = Sprites/Default tinted with backgroundColor.")]
        Material backgroundMaterial;
        [SerializeField, Tooltip("Background padding around the text (m).")]
        Vector2 padding = new Vector2(0.03f, 0.02f);

        [Header("Behaviour")]
        [SerializeField, Tooltip("Hide final (non-partial) text after this many seconds. 0 = never.")]
        float autoHideSeconds = 6f;
        [SerializeField, Range(0f, 1f), Tooltip("Alpha of partial (still-updating) text.")]
        float partialAlpha = 0.7f;

        Transform root;          // the object we position/billboard and show/hide
        Transform background;
        float hideAt = -1f;
        bool built;

        void Awake() => EnsureBuilt();

        /// <summary>Show text. partial = dimmed italic, never auto-hides (keeps being updated).</summary>
        public void SetText(string value, bool partial)
        {
            EnsureBuilt();
            if (string.IsNullOrEmpty(value)) { Clear(); return; }

            root.gameObject.SetActive(true);
            text.text = value;
            text.fontStyle = partial ? FontStyles.Italic : FontStyles.Normal;
            Color c = textColor;
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
                text.color = textColor;
            }
            root = text.transform;

            if (showBackground) BuildBackground();
            root.gameObject.SetActive(false);
        }

        void BuildBackground()
        {
            Material mat = backgroundMaterial;
            if (mat == null)
            {
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) return;
                mat = new Material(sh) { color = backgroundColor, renderQueue = 2990 };  // draw before TMP (3000)
            }
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "CaptionBackground";
            DestroyImmediate(quad.GetComponent<Collider>());   // must not block poke/click raycasts
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            background = quad.transform;
            background.SetParent(root, false);
        }

        // Fit the quad to the rendered text bounds (called only when text changes).
        void UpdateBackground()
        {
            if (background == null) return;
            text.ForceMeshUpdate();
            Bounds b = text.textBounds;
            background.localPosition = new Vector3(b.center.x, b.center.y, 0.005f);   // just behind the text
            background.localScale = new Vector3(b.size.x + padding.x * 2f, b.size.y + padding.y * 2f, 1f);
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
