using UnityEngine;

/// <summary>
/// Marks a Messages thing that floats over the desk top: the floating Send / Cancel card (<see cref="DraftPresenter"/>)
/// hovers at the desk's front-right, beside Bit, with its bottom edge at the desk.
/// <see cref="DigestTrigger.SpawnAtDesk"/> puts one on every floating holder, so while
/// <see cref="Count"/> is above zero the pet keeps its gators out from under them: GatorMrukAdapter adds
/// <see cref="Footprint"/> to the desk NavMesh keep-out (and removes it again when the cards fold).
/// <see cref="Footprint"/> is in the desk frame (DeskPresence.Frame: x right of the front-edge midpoint, z into the desk,
/// metres) on the desk top; it covers DraftPresenter.floatingOffset (0.35, 0.22, 0.02) with the 0.42 m panel width plus
/// a margin. Docked cards (under the open panel) don't count.
/// </summary>
public class MessagesDeskZone : MonoBehaviour
{
    /// <summary>Desk-frame rectangle: x = Rect.x..xMax (right of the front-edge midpoint), z = Rect.y..yMax (into the desk).</summary>
    public static readonly Rect Footprint = new Rect(0.35f - 0.26f, -0.04f, 0.52f, 0.22f);

    /// <summary>Floating Messages holders alive right now.</summary>
    public static int Count { get; private set; }

    void OnEnable() => Count++;
    void OnDisable() => Count = Mathf.Max(0, Count - 1);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Count = 0;
}
