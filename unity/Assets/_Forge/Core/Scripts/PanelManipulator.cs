using Oculus.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Which way a panel turns: toward the eyes, square with the desk's front edge, or wherever it was turned.</summary>
public enum PanelFacing { FaceMe, Straight, Custom }

public enum PanelGrab { None, Move, ResizeLeft, ResizeRight, TurnLeft, TurnRight, Tilt }

/// <summary>
/// Move, push back, resize and turn a <see cref="ForgePanel"/> with the mouse (DesktopRig), controller and hand rays,
/// or a fingertip. <see cref="Workspace"/> adds it to every panel it opens and it builds its own handles, so module
/// prefabs need nothing. The handles fade in while the panel is pointed at:
/// - Title bar: drag to move. The panel stays on the end of the pointer and keeps its <see cref="Facing"/>.
///   Push back / pull in with the scroll wheel, the thumbstick, or by reaching out / pulling the hand in.
/// - Brackets at the bottom corners: resize (the opposite corner stays put). Changes SizeMeters, not scale.
/// - Bars at the sides: turn left / right. Bar under the bottom edge: tilt. Both snap to Face me and Straight.
/// - Title bar buttons: Face me, Straight.
/// </summary>
[RequireComponent(typeof(ForgePanel))]
public class PanelManipulator : MonoBehaviour
{
    public static readonly Vector2 MinSize = new Vector2(0.3f, 0.2f);
    public static readonly Vector2 MaxSize = new Vector2(2.4f, 1.6f);

    const float HandleMarginMm = 56f;     // ray/poke hit area around the panel, for the handles outside its edges
    const float MinDistance = 0.25f, MaxDistance = 4f;  // move: grab point distance along the pointer (m)
    const float ReachGain = 4f;           // 1 cm of reaching out pushes the panel 4 cm
    const float PushPerNotch = 0.05f;     // scroll wheel / thumbstick (m)
    const float TurnGain = 2.5f;          // degrees of turn per degree of pointer swing
    const float SnapDegrees = 4f;
    const float MaxTurn = 80f, MaxTilt = 60f;

    /// <summary>How many panels are being held right now (DesktopRig's hint line uses it).</summary>
    public static int Grabbing { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Grabbing = 0;

    public PanelFacing Facing { get; private set; } = PanelFacing.FaceMe;
    /// <summary>Moved, resized or turned since it opened (Workspace remembers these per mode).</summary>
    public bool UserPlaced { get; private set; }
    /// <summary>Set while the workspace animates the panel; grabs wait until it's done.</summary>
    public bool Busy { get; set; }

    ForgePanel panel;
    RectTransform rect;
    Transform head, frame;
    CanvasGroup controls;
    RayInteractable[] rays;
    PokeInteractable[] pokes;
    ForgePill faceButton, straightButton;
    float showTimer;
    Coroutine turning;

    PanelGrab grab;
    PanelHandle heldHandle;
    ForgePointer pointer;
    float grabDistance, pushed, reachAtGrab;   // move
    Vector3 grabLocal;                         // move: grab point in panel space
    Vector3 fixedCorner;                       // resize: world corner that stays put
    Vector2 cornerOffset;                      // resize: grip-to-corner offset (m), so it doesn't jump
    float yawAtGrab, pitchAtGrab, rayYawAtGrab, rayPitchAtGrab, faceYawAtGrab;  // turn / tilt

    /// <summary>Set up the handles. <paramref name="eyes"/>: the player's head; <paramref name="deskFrame"/>: what Straight squares up with.</summary>
    public void Init(Transform eyes, Transform deskFrame)
    {
        head = eyes;
        frame = deskFrame;
        panel = GetComponent<ForgePanel>();
        rect = (RectTransform)transform;
        rays = GetComponentsInChildren<RayInteractable>(true);
        pokes = GetComponentsInChildren<PokeInteractable>(true);
        if (controls == null) BuildHandles();
    }

    /// <summary>
    /// The workspace placed this panel: at its layout slot (<paramref name="byUser"/> false), or back where the user
    /// left it last time (true, so it's remembered again).
    /// </summary>
    public void SetPlacement(PanelFacing facing, bool byUser)
    {
        Cancel();
        UserPlaced = byUser;
        SetFacing(facing);
    }

    /// <summary>Drop the panel if it's held (the workspace is folding it away).</summary>
    public void Cancel()
    {
        if (grab != PanelGrab.None) EndGrab(heldHandle);
        if (turning != null) StopCoroutine(turning);
        turning = null;
    }

    public void FaceMe() => TurnTo(PanelFacing.FaceMe);
    public void Straighten() => TurnTo(PanelFacing.Straight);

    void OnDisable() => Cancel();

    // ---------------------------------------------------------------- grabbing

    public void BeginGrab(PanelHandle handle, PointerEventData e)
    {
        if (grab != PanelGrab.None || Busy || head == null) return;
        if (!ForgePointer.TryFind(e, head, out pointer))
        {
            Debug.LogWarning($"[PanelManipulator] No pointer for event {e.pointerId}; can't grab {name}.");
            return;
        }
        if (turning != null) StopCoroutine(turning);
        turning = null;

        Ray r = pointer.Ray;
        var plane = new Plane(transform.forward, transform.position);
        Vector3 hit = plane.Raycast(r, out float enter) ? r.GetPoint(enter) : e.pointerCurrentRaycast.worldPosition;
        grab = handle.grab;
        heldHandle = handle;
        handle.SetHeld(true);
        Grabbing++;
        UserPlaced = true;

        switch (grab)
        {
            case PanelGrab.Move:
                grabDistance = Vector3.Distance(r.origin, hit);
                grabLocal = transform.InverseTransformPoint(hit);
                reachAtGrab = pointer.Reach;
                pushed = 0f;
                break;
            case PanelGrab.ResizeLeft:
            case PanelGrab.ResizeRight:
                bool right = grab == PanelGrab.ResizeRight;
                Rect box = rect.rect;
                fixedCorner = transform.TransformPoint(new Vector3(right ? box.xMin : box.xMax, box.yMax, 0f));
                Vector3 moving = transform.TransformPoint(new Vector3(right ? box.xMax : box.xMin, box.yMin, 0f));
                Vector3 toCorner = moving - hit;
                cornerOffset = new Vector2(Vector3.Dot(toCorner, transform.right) * (right ? 1f : -1f),
                                           -Vector3.Dot(toCorner, transform.up));
                break;
            default:  // turn / tilt
                Angles(transform.forward, out yawAtGrab, out pitchAtGrab);
                Angles(r.direction, out rayYawAtGrab, out rayPitchAtGrab);
                Angles(Center() - head.position, out faceYawAtGrab, out _);
                break;
        }
    }

    public void EndGrab(PanelHandle handle)
    {
        if (grab == PanelGrab.None || handle != heldHandle) return;
        grab = PanelGrab.None;
        pointer = null;
        if (heldHandle != null) heldHandle.SetHeld(false);
        heldHandle = null;
        Grabbing = Mathf.Max(0, Grabbing - 1);
    }

    void LateUpdate()
    {
        if (controls == null) return;
        UpdateFade();
        if (grab == PanelGrab.None) return;
        // The release event can get lost (controller put down, hand tracking lost): poll too.
        if (!pointer.Held)
        {
            EndGrab(heldHandle);
            return;
        }

        Ray r = pointer.Ray;
        switch (grab)
        {
            case PanelGrab.Move: Move(r); break;
            case PanelGrab.ResizeLeft:
            case PanelGrab.ResizeRight: Resize(r); break;
            case PanelGrab.TurnLeft:
            case PanelGrab.TurnRight: Turn(r, tilt: false); break;
            case PanelGrab.Tilt: Turn(r, tilt: true); break;
        }
    }

    // The grab point rides the pointer at a fixed distance (so the panel swings around you), pushed by the wheel,
    // the thumbstick or reach. A poke slides it under the finger instead.
    void Move(Ray r)
    {
        Vector3 target;
        if (pointer.CanPush)
        {
            pushed += pointer.Notches(Time.deltaTime) * PushPerNotch;
            float reach = (pointer.Reach - reachAtGrab) * ReachGain;
            float d = Mathf.Clamp(grabDistance + pushed + reach, MinDistance, MaxDistance);
            pushed = d - grabDistance - reach;  // don't wind up past the limits
            target = r.origin + r.direction.normalized * d;
        }
        else
        {
            var plane = new Plane(transform.forward, transform.TransformPoint(grabLocal));
            if (!plane.Raycast(r, out float enter)) return;
            target = r.GetPoint(enter);
        }

        Quaternion rot = transform.rotation;
        if (Facing == PanelFacing.Straight) rot = StraightRotation();
        else if (Facing == PanelFacing.FaceMe)
        {
            // Face the eyes from where the panel's center will be (two passes converge plenty).
            Vector3 centerFromGrab = transform.TransformVector((Vector3)rect.rect.center - grabLocal);
            for (int i = 0; i < 2; i++)
            {
                Vector3 center = target + rot * Quaternion.Inverse(transform.rotation) * centerFromGrab;
                rot = Quaternion.LookRotation(center - head.position, Vector3.up);
            }
        }
        transform.rotation = rot;
        transform.position = target - transform.TransformVector(grabLocal);
    }

    // The grabbed corner follows the pointer across the panel's plane; the opposite top corner stays put.
    void Resize(Ray r)
    {
        var plane = new Plane(transform.forward, fixedCorner);
        if (!plane.Raycast(r, out float enter)) return;
        bool right = grab == PanelGrab.ResizeRight;
        Vector3 d = r.GetPoint(enter) - fixedCorner;
        var size = new Vector2(Vector3.Dot(d, transform.right) * (right ? 1f : -1f) + cornerOffset.x,
                               -Vector3.Dot(d, transform.up) + cornerOffset.y);

        Vector2 current = panel.SizeMeters;
        size = Vector2.Max(size, new Vector2(0.01f, 0.01f));  // the grip can cross the fixed corner
        if (panel.KeepAspect)
        {
            // Follow whichever side changed more, then grow to the minimum / shrink to the maximum as a whole.
            float aspect = current.x / Mathf.Max(current.y, 1e-4f);
            if (Mathf.Abs(size.x / current.x - 1f) > Mathf.Abs(size.y / current.y - 1f)) size.y = size.x / aspect;
            else size.x = size.y * aspect;
            size *= Mathf.Max(1f, MinSize.x / size.x, MinSize.y / size.y);
            size *= Mathf.Min(1f, MaxSize.x / size.x, MaxSize.y / size.y);
        }
        else
        {
            size = new Vector2(Mathf.Clamp(size.x, MinSize.x, MaxSize.x), Mathf.Clamp(size.y, MinSize.y, MaxSize.y));
        }
        if ((size - current).sqrMagnitude < 1e-8f) return;

        panel.SizeMeters = size;
        Rect box = rect.rect;
        Vector3 corner = transform.TransformVector(new Vector3(right ? box.xMin : box.xMax, box.yMax, 0f));
        transform.position = fixedCorner - corner;
    }

    // Swinging the pointer sideways turns the panel about its center (tilt: up and down tips it back and forward).
    void Turn(Ray r, bool tilt)
    {
        Angles(r.direction, out float rayYaw, out float rayPitch);
        float yaw = yawAtGrab, pitch = pitchAtGrab;
        Vector3 center = Center();
        Angles(center - head.position, out float faceYaw, out float facePitch);
        Angles(frame.forward, out float straightYaw, out _);

        if (tilt)
        {
            pitch = Mathf.Clamp(pitchAtGrab + Mathf.DeltaAngle(rayPitchAtGrab, rayPitch) * TurnGain, -MaxTilt, MaxTilt);
            pitch = Snap(pitch, 0f, facePitch);
        }
        else
        {
            float turn = Mathf.DeltaAngle(faceYawAtGrab, yawAtGrab) + Mathf.DeltaAngle(rayYawAtGrab, rayYaw) * TurnGain;
            yaw = Snap(faceYawAtGrab + Mathf.Clamp(turn, -MaxTurn, MaxTurn), straightYaw, faceYaw);
        }

        // Landing on Face me / Straight on both axes makes that the panel's facing again; anything else is custom.
        bool faceMe = Near(yaw, faceYaw) && Near(pitch, facePitch);
        bool straight = Near(yaw, straightYaw) && Near(pitch, 0f);
        SetFacing(faceMe ? PanelFacing.FaceMe : straight ? PanelFacing.Straight : PanelFacing.Custom);
        RotateAboutCenter(Quaternion.Euler(pitch, yaw, 0f), center);
    }

    // Snap an angle to whichever of a / b is nearest, when within SnapDegrees.
    static float Snap(float angle, float a, float b)
    {
        float da = Mathf.Abs(Mathf.DeltaAngle(angle, a)), db = Mathf.Abs(Mathf.DeltaAngle(angle, b));
        if (da < SnapDegrees && da <= db) return a;
        if (db < SnapDegrees) return b;
        return angle;
    }

    static bool Near(float a, float b) => Mathf.Abs(Mathf.DeltaAngle(a, b)) < 0.5f;

    // ---------------------------------------------------------------- facing

    void TurnTo(PanelFacing facing)
    {
        if (Busy || grab != PanelGrab.None) return;
        UserPlaced = true;
        SetFacing(facing);
        Vector3 center = Center();
        Quaternion from = transform.rotation;
        Quaternion to = facing == PanelFacing.Straight ? StraightRotation()
                      : Quaternion.LookRotation(center - head.position, Vector3.up);
        if (turning != null) StopCoroutine(turning);
        turning = StartCoroutine(ForgeTween.Run(0.25f, 0f, k =>
            RotateAboutCenter(Quaternion.Slerp(from, to, ForgeTween.EaseOutCubic(k)), center), () => turning = null));
    }

    void SetFacing(PanelFacing facing)
    {
        Facing = facing;
        if (faceButton != null) faceButton.On = facing == PanelFacing.FaceMe;
        if (straightButton != null) straightButton.On = facing == PanelFacing.Straight;
    }

    Quaternion StraightRotation() => Quaternion.LookRotation(Flat(frame.forward), Vector3.up);

    Vector3 Center() => transform.TransformPoint(rect.rect.center);

    void RotateAboutCenter(Quaternion rotation, Vector3 center)
    {
        transform.rotation = rotation;
        transform.position += center - Center();
    }

    // Yaw (about up, 0 = +Z) and pitch (positive = looking down) of a direction, in degrees, like Quaternion.Euler.
    static void Angles(Vector3 dir, out float yaw, out float pitch)
    {
        dir.Normalize();
        yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
    }

    // ---------------------------------------------------------------- handles

    // Handles fade in while any pointer is on the panel (or its handles) and stay a moment after it leaves.
    void UpdateFade()
    {
        if (IsPointedAt() || grab != PanelGrab.None) showTimer = 0.6f;
        else showTimer -= Time.deltaTime;
        float target = showTimer > 0f ? 1f : 0f;
        if (!Mathf.Approximately(controls.alpha, target))
            controls.alpha = Mathf.MoveTowards(controls.alpha, target, Time.deltaTime * 6f);
    }

    bool IsPointedAt()
    {
        var rig = DesktopRig.Current;
        if (rig != null && rig.Hovered != null && rig.Hovered.transform.IsChildOf(transform)) return true;
        foreach (var r in rays) if (r != null && r.State != InteractableState.Normal) return true;
        foreach (var p in pokes) if (p != null && p.State != InteractableState.Normal) return true;
        return false;
    }

    void BuildHandles()
    {
        panel.HitMarginMm = HandleMarginMm;
        var bar = transform.Find("TitleBar") as RectTransform;
        Graphic barGraphic = bar != null ? bar.GetComponent<Graphic>() : null;

        var root = NewRect("Manipulation", rect);
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;
        root.SetAsLastSibling();
        controls = root.gameObject.AddComponent<CanvasGroup>();
        controls.alpha = 0f;

        if (barGraphic != null)
        {
            // The header is the move handle; pointing at it shows the handles and pills, nothing else lights up.
            var move = bar.gameObject.AddComponent<PanelHandle>();
            move.owner = this;
            move.grab = PanelGrab.Move;
            faceButton = TitleButton(root, "Face me", 0f, FaceMe);
            straightButton = TitleButton(root, "Straight", faceButton.Rect.sizeDelta.x + 8f, Straighten);
        }
        else
        {
            // The module dropped the title bar: a move bar above the panel instead.
            var move = Handle(root, "Move", PanelGrab.Move, new Vector2(0.5f, 1f), new Vector2(0f, 22f), new Vector2(220f, 40f));
            move.SetTint(Bar(move.transform, new Vector2(0f, -2f), new Vector2(160f, 8f)));
        }

        // Resize brackets hugging the bottom corners, just outside the panel.
        for (int side = -1; side <= 1; side += 2)
        {
            var grip = Handle(root, side < 0 ? "ResizeLeft" : "ResizeRight", side < 0 ? PanelGrab.ResizeLeft : PanelGrab.ResizeRight,
                              new Vector2(side < 0 ? 0f : 1f, 0f), new Vector2(16f * side, -16f), new Vector2(56f, 56f));
            grip.SetTint(Bar(grip.transform, new Vector2(-22f * side, 6f), new Vector2(40f, 8f)),
                         Bar(grip.transform, new Vector2(-6f * side, 22f), new Vector2(8f, 40f)));
        }

        // Turn bars at the middle of each side, tilt bar under the bottom edge.
        for (int side = -1; side <= 1; side += 2)
        {
            var turn = Handle(root, side < 0 ? "TurnLeft" : "TurnRight", side < 0 ? PanelGrab.TurnLeft : PanelGrab.TurnRight,
                              new Vector2(side < 0 ? 0f : 1f, 0.5f), new Vector2(20f * side, 0f), new Vector2(40f, 160f));
            turn.SetTint(Bar(turn.transform, new Vector2(-2f * side, 0f), new Vector2(8f, 90f)));
        }
        var tiltGrip = Handle(root, "Tilt", PanelGrab.Tilt, new Vector2(0.5f, 0f), new Vector2(0f, -20f), new Vector2(160f, 40f));
        tiltGrip.SetTint(Bar(tiltGrip.transform, new Vector2(0f, -2f), new Vector2(90f, 8f)));
    }

    // An invisible hit area (still takes rays and the mouse) with a PanelHandle on it.
    PanelHandle Handle(RectTransform parent, string name, PanelGrab kind, Vector2 anchor, Vector2 offsetMm, Vector2 sizeMm)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.anchoredPosition = offsetMm;
        rt.sizeDelta = sizeMm;
        rt.gameObject.AddComponent<Image>().color = Color.clear;
        var handle = rt.gameObject.AddComponent<PanelHandle>();
        handle.owner = this;
        handle.grab = kind;
        return handle;
    }

    // A white rounded bar with a soft glow; PanelHandle lights it fully while pointed at or held.
    static Graphic Bar(Transform parent, Vector2 positionMm, Vector2 sizeMm)
    {
        var rt = NewRect("Bar", (RectTransform)parent);
        rt.anchoredPosition = positionMm;
        rt.sizeDelta = sizeMm;
        var bar = rt.gameObject.AddComponent<ForgeSurface>();
        bar.Pill = true;
        bar.Glow = 0.55f;
        bar.color = ForgeTheme.HandleRest;
        bar.raycastTarget = false;
        return bar;
    }

    // Horizon-style pills at the right end of the header, laid out from the right edge.
    ForgePill TitleButton(RectTransform parent, string label, float fromRight, UnityEngine.Events.UnityAction onClick)
    {
        var pill = ForgePill.Create(parent, label, onClick);
        var rt = pill.Rect;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-14f - fromRight, -ForgeTheme.PanelHeader * 0.5f);
        return pill;
    }

    static RectTransform NewRect(string name, RectTransform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }
}
