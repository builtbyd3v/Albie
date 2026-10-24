using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// First-person player for testing without a headset or the simulator (picked by <see cref="RigSelector"/>).
/// WASD walk, mouse look, left-click presses UI under the crosshair, E sits at / stands up from the desk,
/// Esc frees the cursor (then click UI under the cursor, hold right mouse to look, click empty space to grab it again).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class DesktopRig : MonoBehaviour
{
    public DeskPresence presence;

    [SerializeField] Camera cam;
    [SerializeField] GameObject crosshair;
    [SerializeField] TMP_Text hint;

    public float standingEyeHeight = 1.65f;
    public float seatedEyeHeight = 1.2f;
    public float walkSpeed = 1.6f;
    public float lookDegreesPerPixel = 0.12f;
    [Tooltip("Where you sit, measured back from the desk's front edge (m).")]
    public float seatDistance = 0.45f;
    [Tooltip("UI further than this can't be clicked (m).")]
    public float reach = 4f;

    public Transform Head => cam.transform;
    public bool Seated { get; private set; }

    /// <summary>The desktop rig in use, if any (panel handles ask it for the mouse ray).</summary>
    public static DesktopRig Current { get; private set; }
    /// <summary>UI under the crosshair / cursor right now, or null.</summary>
    public GameObject Hovered => hovered;
    /// <summary>Ray from the eyes through the crosshair or the free cursor.</summary>
    public Ray PointerRay => cam.ScreenPointToRay(pointer != null ? pointer.position : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
    /// <summary>Whether a uGUI event came from this rig's pointer.</summary>
    public bool OwnsPointer(PointerEventData e) => e != null && e == pointer;

    CharacterController body;
    float pitch;
    bool cursorLocked;
    Coroutine seatMove;

    PointerEventData pointer;
    readonly List<RaycastResult> hits = new List<RaycastResult>();
    GameObject hovered;
    GameObject pressed;
    int hintState = -1;
    CanvasGroup hintGroup;
    float hintShownAt;
    Vector3 pressDirection;
    const float ClickSlopDegrees = 1.5f;  // a press that turns the pointer further than this is a drag (about uGUI's 10 px)
    const float HintHold = 7f, HintFade = 0.6f;  // the toast shows this long after it changes, then fades (s)

    void Awake()
    {
        body = GetComponent<CharacterController>();
        SetEyeHeight(standingEyeHeight);
    }

    void OnEnable()
    {
        Current = this;
        SetCursorLocked(true);
    }

    void OnDisable()
    {
        if (Current == this) Current = null;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.escapeKey.wasPressedThisFrame) SetCursorLocked(!cursorLocked);
        if (cursorLocked || mouse.rightButton.isPressed) Look(mouse.delta.ReadValue());
        if (!Seated && seatMove == null) Walk(kb);
        if (kb.eKey.wasPressedThisFrame) ToggleSeat();

        Vector2 screen = cursorLocked ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) : mouse.position.ReadValue();
        bool hitUi = Point(screen, mouse.leftButton.wasPressedThisFrame, mouse.leftButton.wasReleasedThisFrame);
        // Free cursor: clicking empty space grabs the mouse again.
        if (!cursorLocked && !hitUi && mouse.leftButton.wasPressedThisFrame) SetCursorLocked(true);

        // The crosshair swells over anything that takes a click, like Horizon's cursor.
        if (crosshair != null && crosshair.activeSelf)
        {
            float s = Mathf.MoveTowards(crosshair.transform.localScale.x, hitUi ? 1.6f : 1f, Time.deltaTime * 6f);
            crosshair.transform.localScale = new Vector3(s, s, 1f);
        }

        UpdateHint();
    }

    void Look(Vector2 delta)
    {
        transform.Rotate(0f, delta.x * lookDegreesPerPixel, 0f, Space.World);
        pitch = Mathf.Clamp(pitch - delta.y * lookDegreesPerPixel, -85f, 85f);
        cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Walk(Keyboard kb)
    {
        Vector2 input = Vector2.zero;
        if (kb.wKey.isPressed) input.y += 1f;
        if (kb.sKey.isPressed) input.y -= 1f;
        if (kb.dKey.isPressed) input.x += 1f;
        if (kb.aKey.isPressed) input.x -= 1f;
        if (input == Vector2.zero) return;
        Vector3 move = (transform.forward * input.y + transform.right * input.x).normalized;
        float speed = kb.leftShiftKey.isPressed ? walkSpeed * 2f : walkSpeed;
        body.Move(move * speed * Time.deltaTime);
        // No gravity: stay on the room's floor.
        float floor = presence != null && presence.locator != null ? presence.locator.FloorY : 0f;
        var p = transform.position;
        if (!Mathf.Approximately(p.y, floor)) transform.position = new Vector3(p.x, floor, p.z);
    }

    /// <summary>Sit at the desk you're standing next to, or stand back up.</summary>
    public void ToggleSeat()
    {
        if (seatMove != null) return;
        if (Seated)
        {
            Seated = false;
            seatMove = StartCoroutine(MoveTo(transform.position, transform.rotation, standingEyeHeight, 0f));
            return;
        }
        if (presence == null || !presence.AtDesk) return;

        presence.UpdateFrame();
        Transform frame = presence.Frame;
        Vector3 forward = Vector3.ProjectOnPlane(frame.forward, Vector3.up).normalized;
        Vector3 seat = frame.position - forward * seatDistance;
        seat.y = presence.locator.FloorY;
        Seated = true;
        seatMove = StartCoroutine(MoveTo(seat, Quaternion.LookRotation(forward, Vector3.up), seatedEyeHeight, 20f));
    }

    IEnumerator MoveTo(Vector3 position, Quaternion rotation, float eyeHeight, float endPitch)
    {
        body.enabled = false;  // move through the chair; the controller would block it
        Vector3 p0 = transform.position;
        Quaternion r0 = transform.rotation;
        float h0 = cam.transform.localPosition.y, pitch0 = pitch;
        yield return ForgeTween.Run(0.45f, 0f, k =>
        {
            float e = ForgeTween.EaseOutCubic(k);
            transform.SetPositionAndRotation(Vector3.Lerp(p0, position, e), Quaternion.Slerp(r0, rotation, e));
            SetEyeHeight(Mathf.Lerp(h0, eyeHeight, e));
            pitch = Mathf.Lerp(pitch0, endPitch, e);
            cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        });
        body.enabled = true;
        seatMove = null;
    }

    /// <summary>Turn to face a point (handy for scripted demos and tests).</summary>
    public void LookAt(Vector3 point)
    {
        Vector3 d = point - cam.transform.position;
        Vector3 flat = Vector3.ProjectOnPlane(d, Vector3.up);
        if (flat.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        pitch = Mathf.Clamp(-Mathf.Atan2(d.y, flat.magnitude) * Mathf.Rad2Deg, -85f, 85f);
        cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void SetEyeHeight(float h) => cam.transform.localPosition = new Vector3(0f, h, 0f);

    void SetCursorLocked(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
        if (crosshair != null) crosshair.SetActive(locked);
    }

    // Minimal pointer for world-space canvases: hover, press, drag and click, like a mouse on a 2D UI.
    // Returns true when the point is over UI.
    bool Point(Vector2 screen, bool down, bool up)
    {
        var events = EventSystem.current;
        if (events == null) return false;
        if (pointer == null) pointer = new PointerEventData(events) { button = PointerEventData.InputButton.Left };

        pointer.position = screen;
        hits.Clear();
        events.RaycastAll(pointer, hits);
        RaycastResult hit = default;
        foreach (var h in hits)
        {
            if (h.distance <= reach) { hit = h; break; }
        }
        // Volumes' grab boxes aren't UI: whichever is nearer, the UI or a box, gets the events.
        float uiDistance = hit.gameObject != null ? hit.distance : reach;
        if (VolumeHandle.Raycast(cam.ScreenPointToRay(screen), uiDistance, out RaycastHit box, out VolumeHandle handle))
            hit = new RaycastResult { gameObject = handle.gameObject, distance = box.distance, worldPosition = box.point, worldNormal = box.normal };
        pointer.pointerCurrentRaycast = hit;
        GameObject target = hit.gameObject;

        if (target != hovered)
        {
            if (hovered != null) ExecuteEvents.ExecuteHierarchy(hovered, pointer, ExecuteEvents.pointerExitHandler);
            if (target != null) ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerEnterHandler);
            hovered = target;
        }

        if (down && target != null)
        {
            pressDirection = cam.ScreenPointToRay(screen).direction;
            pointer.pressPosition = screen;
            pointer.pointerPressRaycast = hit;
            pointer.eligibleForClick = true;
            pointer.rawPointerPress = target;
            pressed = ExecuteEvents.ExecuteHierarchy(target, pointer, ExecuteEvents.pointerDownHandler)
                      ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            pointer.pointerPress = pressed;
            pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(target);
            if (pointer.pointerDrag != null)
            {
                ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.beginDragHandler);
                pointer.dragging = true;
            }
        }
        else if (pointer.dragging && pointer.pointerDrag != null)
        {
            ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.dragHandler);
        }

        // A press that moves (the view turns, or the free cursor slides) is a drag, not a click: dragging a scroll list
        // by one of its buttons mustn't press the button. The locked crosshair never moves on screen, so measure the ray.
        if (pressed != null && pointer.eligibleForClick && !down &&
            Vector3.Angle(pressDirection, cam.ScreenPointToRay(screen).direction) > ClickSlopDegrees)
            pointer.eligibleForClick = false;

        if (up)
        {
            if (pressed != null)
            {
                ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerUpHandler);
                if (pointer.eligibleForClick && target != null && ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) == pressed)
                    ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerClickHandler);
            }
            if (pointer.dragging && pointer.pointerDrag != null)
                ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.endDragHandler);
            pressed = null;
            pointer.pointerPress = null;
            pointer.pointerDrag = null;
            pointer.dragging = false;
            pointer.eligibleForClick = false;
        }
        return target != null;
    }

    // Rebuilt only when what it says changes (not a new string every frame). The toast shows for a few seconds after
    // each change (sitting down, grabbing a panel, freeing the cursor), then gets out of the way.
    void UpdateHint()
    {
        if (hint == null) return;
        if (hintGroup == null)
        {
            // The toast pill around the text (Forge > Restyle Desktop HUD), or just the text on an older HUD.
            var pill = hint.transform.parent != null && hint.transform.parent.name == "HintPill" ? hint.transform.parent : hint.transform;
            hintGroup = pill.GetComponent<CanvasGroup>();
            if (hintGroup == null) hintGroup = pill.gameObject.AddComponent<CanvasGroup>();
            hintGroup.blocksRaycasts = hintGroup.interactable = false;
        }
        bool atDesk = presence != null && presence.AtDesk;
        int state = (PanelManipulator.Grabbing > 0 ? 1 : 0) | (cursorLocked ? 2 : 0) | (Seated ? 4 : 0) | (atDesk ? 8 : 0);
        bool holding = PanelManipulator.Grabbing > 0;
        float alpha = holding ? 1f : Mathf.Clamp01(1f - (Time.time - hintShownAt - HintHold) / HintFade);
        if (!Mathf.Approximately(hintGroup.alpha, alpha)) hintGroup.alpha = alpha;
        if (state == hintState) return;
        hintState = state;
        hintShownAt = Time.time;
        hintGroup.alpha = 1f;
        if (PanelManipulator.Grabbing > 0)
        {
            hint.text = cursorLocked ? "Holding a panel · look around to carry it · scroll: push back / pull in · release to drop"
                                     : "Holding a panel · move the mouse to carry it · scroll: push back / pull in · release to drop";
            return;
        }
        string cursor = cursorLocked ? "Esc: free cursor" : "Click empty space: grab mouse · hold right mouse: look";
        string desk = Seated ? "E: stand up"
                    : atDesk ? "E: sit at desk"
                    : "Walk up to a table";
        hint.text = $"WASD walk · Shift run · click buttons · {desk} · {cursor}";
    }
}
