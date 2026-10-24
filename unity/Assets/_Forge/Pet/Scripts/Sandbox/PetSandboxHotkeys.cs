using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Keyboard testing for the pet sandbox:
    /// 1-9 = GatorState Intro..Error (enum order), P = poke, J/B/S = Jump/Bounce/Spin,
    /// C = ComeToUser, M = cycle moods, T = test caption, H = hop onto the user's desk, G = hop back to the floor,
    /// L = lay down (rest), R = roll sideways (off the desk edge -> fall + stun + hop back). P x3 within 4 s also rolls.
    /// Note: S also walks backwards on DesktopRig; rebind spinKey if that bothers you.
    /// </summary>
    public class PetSandboxHotkeys : MonoBehaviour
    {
        [SerializeField, Tooltip("Gator to drive. Empty = first GatorController found at Awake.")]
        GatorController gator;
        [SerializeField] Key jumpKey = Key.J;
        [SerializeField] Key bounceKey = Key.B;
        [SerializeField] Key spinKey = Key.K; // not S: that walks the DesktopRig backwards
        [SerializeField] Key pokeKey = Key.P;
        [SerializeField] Key comeKey = Key.C;
        [SerializeField] Key moodKey = Key.M;
        [SerializeField] Key captionKey = Key.T;
        [SerializeField, Tooltip("GatorController.HopToUserDesk().")] Key hopDeskKey = Key.H;
        [SerializeField, Tooltip("GatorController.HopToFloor().")] Key hopFloorKey = Key.G;
        [SerializeField, Tooltip("GatorController.Rest(RestPose.Lay).")] Key layKey = Key.L;
        [SerializeField, Tooltip("GatorController.StartRoll().")] Key rollKey = Key.R;

        GatorInteraction interaction;
        GatorExpressions expressions;
        GatorCaption caption;
        int moodIndex;

        void Awake()
        {
            if (gator == null) gator = FindAnyObjectByType<GatorController>();
            if (gator == null) { Debug.LogWarning("[PetSandboxHotkeys] No GatorController in scene.", this); return; }
            interaction = gator.GetComponent<GatorInteraction>();
            expressions = gator.GetComponent<GatorExpressions>();
            caption = gator.GetComponent<GatorCaption>();
        }

        void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || gator == null) return;

            // 1..9 (top row or numpad) -> GatorState 0..8.
            for (int i = 0; i < 9; i++)
            {
                if (kb[Key.Digit1 + i].wasPressedThisFrame || kb[Key.Numpad1 + i].wasPressedThisFrame)
                {
                    gator.SetState((GatorState)i);
                    Debug.Log("[PetSandboxHotkeys] State -> " + (GatorState)i, this);
                }
            }

            if (kb[jumpKey].wasPressedThisFrame) gator.PlayEmote("Jump");
            if (kb[bounceKey].wasPressedThisFrame) gator.PlayEmote("Bounce");
            if (kb[spinKey].wasPressedThisFrame) gator.PlayEmote("Spin");
            if (kb[comeKey].wasPressedThisFrame) gator.ComeToUser();
            if (kb[hopDeskKey].wasPressedThisFrame) gator.HopToUserDesk();
            if (kb[hopFloorKey].wasPressedThisFrame) gator.HopToFloor();
            if (kb[layKey].wasPressedThisFrame) gator.Rest(RestPose.Lay);
            if (kb[rollKey].wasPressedThisFrame) gator.StartRoll();
            if (kb[pokeKey].wasPressedThisFrame && interaction != null) interaction.Poke();

            if (kb[moodKey].wasPressedThisFrame && expressions != null)
            {
                moodIndex = (moodIndex + 1) % 8;               // Mood has 8 values (incl. Sleepy)
                expressions.SetMood((Mood)moodIndex);
            }
            if (kb[captionKey].wasPressedThisFrame && caption != null)
                caption.SetText("Hi! I'm Bit. Ask me anything about the project.", false);
        }
    }
}
