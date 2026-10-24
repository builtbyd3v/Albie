using Oculus.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// The pointer holding a part in the Inspect view: Core's <see cref="ForgePointer"/> (DesktopRig's mouse, a
/// controller or hand ray) or, in the sandbox, which has no DesktopRig, the plain mouse through the main camera.
/// Made once per grab.
/// </summary>
public sealed class CadPointer
{
    readonly ForgePointer forge;
    readonly RayInteractor interactor;  // XR: its pose turns the part (twist the wrist)

    CadPointer(ForgePointer forge, RayInteractor interactor)
    {
        this.forge = forge;
        this.interactor = interactor;
    }

    /// <summary>The pointer behind a uGUI event (CadPartRaycaster sends the mouse's).</summary>
    public static CadPointer FromEvent(PointerEventData e, Transform head)
    {
        if (ForgePointer.TryFind(e, head, out ForgePointer pointer)) return new CadPointer(pointer, null);
        return Camera.main != null && Mouse.current != null ? new CadPointer(null, null) : null;
    }

    public static CadPointer FromRay(RayInteractor ray, Transform head) => new CadPointer(ForgePointer.FromRay(ray, head), ray);

    /// <summary>A mouse (no hand to twist): Shift + drag turns the part instead.</summary>
    public bool IsMouse => forge == null || forge.source == ForgePointer.Source.Mouse;

    public bool Held => forge != null ? forge.Held : Mouse.current != null && Mouse.current.leftButton.isPressed;

    public Ray Ray => forge != null ? forge.Ray : Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

    /// <summary>The ray as a pose. A controller or hand also rolls; a mouse stays upright.</summary>
    public Pose Pose
    {
        get
        {
            Ray r = Ray;
            Vector3 up = interactor != null ? interactor.transform.up : Vector3.up;
            return new Pose(r.origin, Quaternion.LookRotation(r.direction, up));
        }
    }

    /// <summary>Scroll this frame in wheel notches (+ = wheel up / thumbstick forward).</summary>
    public float Notches(float dt)
    {
        if (forge != null) return forge.Notches(dt);
        return Mouse.current != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;  // 120 per notch on Windows
    }
}
