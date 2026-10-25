using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace Forge.ML
{
    /// <summary>
    /// Small uGUI builders for the ML panels, in the Forge look: Inter text, graphite cards with hairline outlines,
    /// Horizon pills (<see cref="ForgePill"/>) and one palette. Positions are top-left based, in mm.
    /// </summary>
    public static class MLUI
    {
        // Palette. Text matches ForgeTheme; Good / Bad / Warn mark right, wrong and overfit on every ML panel.
        public static readonly Color Ink = new Color(0.01f, 0.012f, 0.018f, 0.55f);   // recessed wells (charts, dot wall)
        public static readonly Color CardColor = new Color(1f, 1f, 1f, 0.045f);
        public static readonly Color CardLit = new Color(1f, 1f, 1f, 0.085f);
        public static readonly Color TextColor = new Color(1f, 1f, 1f, 0.94f);
        public static readonly Color Sub = new Color(1f, 1f, 1f, 0.56f);
        public static readonly Color Faint = new Color(1f, 1f, 1f, 0.32f);
        public static readonly Color Accent = new Color32(0x5B, 0x9B, 0xFF, 0xFF);
        public static readonly Color Good = new Color32(0x3D, 0xDC, 0x97, 0xFF);
        public static readonly Color Bad = new Color32(0xFF, 0x5D, 0x6C, 0xFF);
        public static readonly Color Warn = new Color32(0xFF, 0xB5, 0x47, 0xFF);
        public static readonly Color Train = new Color32(0x5B, 0xC8, 0xFF, 0xFF);
        public static readonly Color Val = new Color32(0xFF, 0x9F, 0x5A, 0xFF);

        public static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); return r;
        }
        public static TMP_Text Text(Transform parent, string value, float x, float y, float w, float h, float size = 16)
        {
            var t = Rect("Label", parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            if (ForgeTheme.Font != null) t.font = ForgeTheme.Font;
            t.text = value; t.fontSize = size; t.color = TextColor; t.raycastTarget = false; return t;
        }
        /// <summary>Small spaced capitals for section titles and stat captions.</summary>
        public static TMP_Text Caps(Transform parent, string value, float x, float y, float w, float h = 14, float size = 9.5f)
        {
            var t = Text(parent, value.ToUpperInvariant(), x, y, w, h, size);
            t.characterSpacing = 8; t.color = Sub; t.fontStyle = FontStyles.Bold; t.textWrappingMode = TextWrappingModes.NoWrap;
            return t;
        }
        /// <summary>A big number under a caption (accuracy, counts).</summary>
        public static TMP_Text Stat(Transform parent, string caption, string value, float x, float y, float w, Color color)
        {
            Caps(parent, caption, x, y, w);
            var t = Text(parent, value, x, y + 13, w, 30, 24); t.color = color; t.fontStyle = FontStyles.Bold;
            t.textWrappingMode = TextWrappingModes.NoWrap; return t;
        }
        public static Image Fill(Transform parent, float x, float y, float w, float h, Color color)
        {
            var image = Rect("Fill", parent, x, y, w, h).gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
        }
        /// <summary>A rounded shape (ForgeSurface): cards, wells, chips, bars.</summary>
        public static ForgeSurface Surface(Transform parent, float x, float y, float w, float h, Color color, float corner = 10, float outline = 0)
        {
            var s = Rect("Surface", parent, x, y, w, h).gameObject.AddComponent<ForgeSurface>();
            s.color = color; s.Corner = corner; s.Outline = outline; s.raycastTarget = false; return s;
        }
        public static ForgeSurface Card(Transform parent, float x, float y, float w, float h, float corner = 14)
            => Surface(parent, x, y, w, h, CardColor, corner, 0.2f);
        public static ForgeSurface Well(Transform parent, float x, float y, float w, float h, float corner = 12)
            => Surface(parent, x, y, w, h, Ink, corner, 0.12f);
        /// <summary>A small round dot (legend keys, status lights).</summary>
        public static ForgeSurface Dot(Transform parent, float x, float y, float size, Color color)
        {
            var s = Surface(parent, x, y, size, size, color); s.Pill = true; return s;
        }
        /// <summary>A Horizon pill at a fixed size. Use <see cref="Pill"/> when it needs an on state.</summary>
        public static Button Button(Transform parent, string title, float x, float y, float w, float h, UnityAction click)
            => Pill(parent, title, x, y, w, h, click).Button;
        public static ForgePill Pill(Transform parent, string title, float x, float y, float w, float h, UnityAction click)
        {
            var pill = ForgePill.Create((RectTransform)parent, title, click, h, Mathf.Min(12.5f, h * 0.46f));
            var r = pill.Rect; r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y); if (w > 0) r.sizeDelta = new Vector2(w, h);
            return pill;
        }
        public static TMP_InputField Input(Transform parent, string name, string placeholderText, float x, float y, float w, float h)
        {
            var root = Rect(name, parent, x, y, w, h);
            var background = root.gameObject.AddComponent<Image>(); background.color = new Color(.1f, .14f, .2f);
            var field = root.gameObject.AddComponent<TMP_InputField>(); field.targetGraphic = background;
            field.lineType = TMP_InputField.LineType.SingleLine; field.characterLimit = 512;
            var viewport = Rect("Text Area", root, 5, 2, w - 10, h - 4);
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(5, 2); viewport.offsetMax = new Vector2(-5, -2);
            viewport.gameObject.AddComponent<RectMask2D>();
            var valueObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            valueObject.transform.SetParent(viewport, false);
            var valueRect = valueObject.GetComponent<RectTransform>(); valueRect.anchorMin = Vector2.zero; valueRect.anchorMax = Vector2.one;
            valueRect.offsetMin = valueRect.offsetMax = Vector2.zero;
            var value = valueObject.GetComponent<TextMeshProUGUI>(); value.fontSize = 11; value.color = Color.white; value.raycastTarget = false;
            var hintObject = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            hintObject.transform.SetParent(viewport, false);
            var hintRect = hintObject.GetComponent<RectTransform>(); hintRect.anchorMin = Vector2.zero; hintRect.anchorMax = Vector2.one;
            hintRect.offsetMin = hintRect.offsetMax = Vector2.zero;
            var hint = hintObject.GetComponent<TextMeshProUGUI>(); hint.text = placeholderText; hint.fontSize = 11;
            hint.color = new Color(.55f, .62f, .72f); hint.raycastTarget = false;
            field.textViewport = viewport; field.textComponent = value; field.placeholder = hint;
            return field;
        }
        public static RawImage Photo(Transform parent, MLSession session, int index, float x, float y, float w, float h)
        {
            var p = Rect("Image", parent, x, y, w, h).gameObject.AddComponent<RawImage>(); p.texture = session.Atlas; p.uvRect = session.UV(index); p.raycastTarget = false; return p;
        }
        /// <summary>A thin line between two points in the parent's top-left space (leaders, axes).</summary>
        public static Image Line(Transform parent, Vector2 from, Vector2 to, float width, Color color)
        {
            var image = Fill(parent, 0, 0, 1, 1, color); image.name = "Line"; SetLine(image.rectTransform, from, to, width); return image;
        }
        public static void SetLine(RectTransform line, Vector2 from, Vector2 to, float width)
        {
            Vector2 d = to - from; line.pivot = new Vector2(0, 0.5f);
            line.anchoredPosition = new Vector2(from.x, -from.y); line.sizeDelta = new Vector2(d.magnitude, width);
            line.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        }
        // Class identity colors (dataset scrub bar, class keys): ten hues at one lightness, kept apart from the
        // right / wrong green and red where they appear side by side.
        static readonly Color[] Classes =
        {
            new Color32(0x6A, 0xA7, 0xFF, 0xFF), new Color32(0xFF, 0xB4, 0x5C, 0xFF), new Color32(0x59, 0xD6, 0xA0, 0xFF), new Color32(0xFF, 0x7A, 0x8A, 0xFF),
            new Color32(0xB4, 0x8C, 0xFF, 0xFF), new Color32(0x4F, 0xD3, 0xE6, 0xFF), new Color32(0xF2, 0xD4, 0x5C, 0xFF), new Color32(0xFF, 0x8F, 0xD0, 0xFF),
            new Color32(0x9B, 0xD8, 0x6A, 0xFF), new Color32(0xC9, 0xA2, 0x7A, 0xFF),
        };
        public static Color ClassColor(int index) => index < 0 ? Faint : Classes[index % Classes.Length];
        /// <summary>A score from 0 to 1 as red, through amber, to green (accuracy, F1); below 0 = unavailable.</summary>
        public static Color Heat(float value)
        {
            if (value < 0) return new Color(1, 1, 1, 0.04f);
            value = Mathf.Clamp01(value);
            return value < 0.5f ? Color.Lerp(Bad, Warn, value * 2f) : Color.Lerp(Warn, Good, value * 2f - 1f);
        }
        /// <summary>357034 → "357K".</summary>
        public static string Count(long n) => n >= 1000000 ? (n / 1e6f).ToString(n >= 10000000 ? "0" : "0.0") + "M" : n >= 1000 ? (n / 1e3f).ToString(n >= 10000 ? "0" : "0.0") + "K" : n.ToString();
        public static void Clear(Transform root) { for (int i = root.childCount - 1; i >= 0; i--) { root.GetChild(i).gameObject.SetActive(false); Object.Destroy(root.GetChild(i).gameObject); } }
    }
}
