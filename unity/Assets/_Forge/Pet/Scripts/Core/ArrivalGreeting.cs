using System;
using System.Collections;
using TMPro;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// The first-arrival greeting: a translucent card with the digest line ("You have 3 new messages, 1 mentions you.")
    /// floating beside Bit, and a START ring (one of Hudson's dock rings, <see cref="DockRing"/>) to its right. Both are
    /// built into PetRuntime.prefab by the editor (Forge > Pet > Build All, PetRuntimeSetup / ArrivalSetup), saved
    /// inactive, and driven by <see cref="ArrivalDirector"/>: <see cref="Show"/> places them in the desk frame and pops
    /// them in, <see cref="Fold"/> scales them away. Pressing START (mouse, Interaction SDK ray / poke through the ring's
    /// uGUI Button) raises <see cref="StartPressed"/>. Inter + ForgeTheme colours, the Messages cards' light fill.
    /// </summary>
    [DisallowMultipleComponent]
    public class ArrivalGreeting : MonoBehaviour
    {
        [SerializeField, Tooltip("The card canvas (1 unit = 1 mm).")]
        RectTransform card;
        [SerializeField] TMP_Text headline;
        [SerializeField] DockRing startRing;
        [SerializeField, Tooltip("Card centre from the greeting spot, desk frame (m): right of it, up, into the desk.")]
        Vector3 cardOffset = new Vector3(0.28f, 0.21f, -0.02f);
        [SerializeField, Tooltip("Gap (m) between the card's right edge and the START ring.")]
        float ringGap = 0.05f;

        /// <summary>START was pressed (once per Show).</summary>
        public event Action StartPressed;
        public bool IsShown => gameObject.activeSelf && !_folding;
        public DockRing StartRing => startRing;
        public string Line => headline != null ? headline.text : "";

        bool _folding, _pressed, _wired;
        Vector3 _cardScale, _ringScale;
        Coroutine _anim;

        void Awake() => Wire();

        void Wire()
        {
            if (_wired) return;
            _wired = true;
            if (card != null) _cardScale = card.localScale;
            if (startRing != null)
            {
                _ringScale = startRing.transform.localScale;
                startRing.Button.onClick.AddListener(OnStartPressed);
            }
        }

        /// <summary>Place the card and ring beside <paramref name="greetingSpot"/> (world, on the desk) facing the user, show the line, pop in.</summary>
        public void Show(DeskPresence presence, Vector3 greetingSpot, string line)
        {
            Wire();
            if (_anim != null) { StopCoroutine(_anim); _anim = null; }
            _folding = false;
            _pressed = false;
            if (presence == null || presence.Frame == null) return;
            Transform frame = presence.Frame;
            Vector3 spot = frame.InverseTransformPoint(greetingSpot);
            Vector3 cardLocal = new Vector3(spot.x + cardOffset.x, cardOffset.y, spot.z + cardOffset.z);
            float cardHalf = card != null ? card.sizeDelta.x * 0.5f * ForgePanel.MetersPerUnit : 0.15f;
            float ringRadius = startRing != null ? startRing.Radius * ForgePanel.MetersPerUnit : 0.045f;
            Vector3 ringLocal = cardLocal + new Vector3(cardHalf + ringGap + ringRadius, 0f, 0f);

            gameObject.SetActive(true);
            if (headline != null) headline.text = line ?? "";
            if (card != null)
            {
                card.position = frame.TransformPoint(cardLocal);
                DigestTrigger.FaceUser(card, presence);
                card.localScale = _cardScale;
                DigestTrigger.PopIn(this, card);
            }
            if (startRing != null)
            {
                Transform t = startRing.transform;
                t.position = frame.TransformPoint(ringLocal);
                DigestTrigger.FaceUser(t, presence);
                t.localScale = _ringScale;
                startRing.Interactable = true;
                DigestTrigger.PopIn(this, t);
            }
        }

        /// <summary>Change the headline while shown (the hub came live and answered the digest after all).</summary>
        public void SetLine(string line)
        {
            if (headline != null) headline.text = line ?? "";
        }

        /// <summary>Scale the card and ring away (0.25 s), then deactivate. Immediate: just hide.</summary>
        public void Fold(bool immediate = false)
        {
            if (!gameObject.activeSelf || _folding) return;
            if (startRing != null) startRing.Interactable = false;
            if (immediate) { Hidden(); return; }
            _folding = true;
            _anim = StartCoroutine(FoldRoutine());
        }

        IEnumerator FoldRoutine()
        {
            Vector3 c0 = card != null ? card.localScale : Vector3.one, r0 = startRing != null ? startRing.transform.localScale : Vector3.one;
            yield return ForgeTween.Run(0.25f, 0f, k =>
            {
                float s = 1f - ForgeTween.EaseInCubic(k) * 0.98f;
                if (card != null) card.localScale = c0 * s;
                if (startRing != null) startRing.transform.localScale = r0 * s;
            });
            Hidden();
        }

        void Hidden()
        {
            _anim = null;
            _folding = false;
            if (card != null) card.localScale = _cardScale;
            if (startRing != null) startRing.transform.localScale = _ringScale;
            gameObject.SetActive(false);
        }

        /// <summary>The START ring's Button (mouse / ray / poke) lands here.</summary>
        public void OnStartPressed()
        {
            if (!IsShown || _pressed) return;
            _pressed = true;
            StartPressed?.Invoke();
        }
    }
}
