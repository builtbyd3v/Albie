using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace Forge.ML
{
    public static class MLUI
    {
        public static readonly Color Ink = new Color(.07f, .1f, .16f, .98f);
        public static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); return r;
        }
        public static TMP_Text Text(Transform parent, string value, float x, float y, float w, float h, float size = 16)
        {
            var t = Rect("Label", parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            t.text = value; t.fontSize = size; t.color = Color.white; t.raycastTarget = false; return t;
        }
        public static Image Fill(Transform parent, float x, float y, float w, float h, Color color)
        {
            var image = Rect("Fill", parent, x, y, w, h).gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
        }
        public static Button Button(Transform parent, string title, float x, float y, float w, float h, UnityAction click)
        {
            var r = Rect(title, parent, x, y, w, h); var image = r.gameObject.AddComponent<Image>(); image.color = new Color(.17f, .25f, .36f);
            var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = image; b.onClick.AddListener(click);
            Text(r, title, 5, 4, w - 10, h - 4, 13); return b;
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
        public static void Clear(Transform root) { for (int i = root.childCount - 1; i >= 0; i--) { root.GetChild(i).gameObject.SetActive(false); Object.Destroy(root.GetChild(i).gameObject); } }
    }
}
