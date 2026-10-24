using TMPro;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Small floating name over a gator ("Tester", "Bit"), in its colour with a dark outline so it reads on
    /// passthrough, billboarded toward PetUser.Head. Sits just above the body, under the speech bubble (GatorCaption
    /// is 0.35 m up). GatorCrewMember adds one to every crew gator; GatorCrew shows Bit's while a crew exists.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorNameTag : MonoBehaviour
    {
        [SerializeField] string label = "Bit";
        [SerializeField] Color color = new Color(0.25f, 0.75f, 0.4f);
        [SerializeField, Tooltip("Height above the gator's root (m, before the gator's own scale).")]
        float height = 0.2f;
        [SerializeField, Tooltip("TMP font size for 3D text (0.16 ~ 1.6 cm cap height).")]
        float fontSize = 0.16f;

        TextMeshPro _text;

        /// <summary>Change the label and colour (dark colours are lightened so they stay readable).</summary>
        public void Set(string text, Color c)
        {
            label = text;
            color = c;
            if (_text == null) return;
            _text.text = label;
            _text.color = c.grayscale < 0.35f ? Color.Lerp(c, Color.white, 0.45f) : c;
        }

        public void Show(bool on)
        {
            if (_text != null) _text.gameObject.SetActive(on);
        }

        void Awake()
        {
            var go = new GameObject("NameTag", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            float s = transform.lossyScale.x;
            if (s > 0.0001f) go.transform.localScale = Vector3.one / s;   // keep the text in metres
            _text = go.AddComponent<TextMeshPro>();
            _text.rectTransform.sizeDelta = new Vector2(0.3f, 0.05f);
            _text.rectTransform.pivot = new Vector2(0.5f, 0f);
            _text.alignment = TextAlignmentOptions.Bottom;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.fontSize = fontSize;
            _text.richText = false;
            TMP_FontAsset f = GatorCaption.DefaultFont();
            if (f != null) _text.font = f;
            _text.outlineWidth = 0.22f;                                  // per-tag material instance, created once
            _text.outlineColor = new Color32(20, 30, 25, 255);
            Set(label, color);
        }

        void LateUpdate()
        {
            if (_text == null || !_text.gameObject.activeSelf) return;
            Transform t = _text.transform;
            t.position = transform.position + Vector3.up * (height * transform.lossyScale.y);
            Transform head = PetUser.Head;
            if (head == null) return;
            Vector3 dir = t.position - head.position;   // TMP faces -Z, so +Z points away from the viewer
            if (dir.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }
    }
}
