using Oculus.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// The 3D pointer behind a uGUI event, whichever input sent it: <see cref="DesktopRig"/>'s mouse or crosshair,
/// an Interaction SDK ray (controller, or pinching hand) or a poke (fingertip). uGUI only reports where the pointer
/// is on the canvas; this gives the ray itself, so a dragged panel can follow it in 3D and be pushed back or pulled in.
/// Made once per grab (not per frame).
/// </summary>
public class ForgePointer
{
    public enum Source { Mouse, Ray, Poke }

    // Interaction SDK interactors in the scene, found on the first grab and again if one isn't known yet.
    static RayInteractor[] rayInteractors;
    static PokeInteractor[] pokeInteractors;

    public readonly Source source;
    readonly DesktopRig rig;
    readonly RayInteractor ray;
    readonly PokeInteractor poke;
    readonly Transform head;
    readonly OVRInput.Controller controller;

    ForgePointer(Source source, DesktopRig rig, RayInteractor ray, PokeInteractor poke, Transform head)
    {
        this.source = source;
        this.rig = rig;
        this.ray = ray;
        this.poke = poke;
        this.head = head;
        if (ray != null) controller = HandOf(ray.transform);
    }

    /// <summary>Find the pointer that sent <paramref name="e"/>. <paramref name="head"/> is the player's eyes.</summary>
    public static bool TryFind(PointerEventData e, Transform head, out ForgePointer pointer)
    {
        pointer = null;
        var rig = DesktopRig.Current;
        if (rig != null && rig.OwnsPointer(e))
        {
            pointer = new ForgePointer(Source.Mouse, rig, null, null, head);
            return true;
        }

        // Interaction SDK: PointableCanvasModule uses the interactor's Identifier as the uGUI pointerId.
        for (int pass = 0; pass < 2 && pointer == null; pass++)
        {
            if (pass == 1 || rayInteractors == null)
            {
                rayInteractors = Object.FindObjectsByType<RayInteractor>();
                pokeInteractors = Object.FindObjectsByType<PokeInteractor>();
            }
            foreach (var r in rayInteractors)
                if (r != null && r.Identifier == e.pointerId) pointer = new ForgePointer(Source.Ray, null, r, null, head);
            foreach (var p in pokeInteractors)
                if (p != null && p.Identifier == e.pointerId) pointer = new ForgePointer(Source.Poke, null, null, p, head);
        }
        return pointer != null;
    }

    /// <summary>A ray interactor that grabbed something directly (not through a canvas).</summary>
    public static ForgePointer FromRay(RayInteractor ray, Transform head) => new ForgePointer(Source.Ray, null, ray, null, head);

    /// <summary>Still pressed (mouse button down, ray trigger/pinch held, finger still pushed in).</summary>
    public bool Held
    {
        get
        {
            switch (source)
            {
                case Source.Mouse: return rig != null && rig.isActiveAndEnabled && Mouse.current != null && Mouse.current.leftButton.isPressed;
                case Source.Ray: return ray != null && ray.State == InteractorState.Select;
                default: return poke != null && poke.State == InteractorState.Select;
            }
        }
    }

    /// <summary>
    /// The pointing ray. A poke has none, so it's the line from the eyes through the fingertip: the point on the
    /// panel under the finger.
    /// </summary>
    public Ray Ray
    {
        get
        {
            switch (source)
            {
                case Source.Mouse: return rig.PointerRay;
                case Source.Ray: return ray.Ray;
                default: return new Ray(head.position, poke.Origin - head.position);
            }
        }
    }

    /// <summary>Whether the pointer can push a panel back or pull it in (a poke can only slide it).</summary>
    public bool CanPush => source != Source.Poke;

    /// <summary>Hand tracking is driving the rig (controllers put down). Hands have no thumbstick or buttons.</summary>
    public static bool HandsActive => (OVRInput.GetActiveController() & OVRInput.Controller.Hands) != 0;

    /// <summary>A pinching hand's ray: <see cref="Notches"/> stays 0, so reaching out stands in for the thumbstick.</summary>
    public bool IsHand => source == Source.Ray && HandsActive;

    /// <summary>
    /// How far the hand or controller is from the eyes (m). Reaching out or pulling in pushes a held panel.
    /// 0 for the mouse, whose ray starts at the eyes.
    /// </summary>
    public float Reach => source == Source.Ray ? Vector3.Distance(ray.Ray.origin, head.position) : 0f;

    /// <summary>
    /// Scroll this frame, in wheel notches (+ = wheel up / thumbstick forward): the mouse wheel, or the thumbstick of
    /// the controller holding the ray at 10 notches a second. Panels push back with it, volumes rise or grow.
    /// </summary>
    public float Notches(float dt)
    {
        if (source == Source.Mouse)
            return Mouse.current != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;  // 120 per notch on Windows
        if (source == Source.Ray && controller != OVRInput.Controller.None)
            return OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller).y * 10f * dt;
        return 0f;
    }

    /// <summary>
    /// Which controller an interactor belongs to: the rig's interactors sit under ...InteractorsLeft / ...InteractorsRight.
    /// Hands report no thumbstick or grip.
    /// </summary>
    public static OVRInput.Controller HandOf(Transform t)
    {
        for (; t != null; t = t.parent)
        {
            if (t.name.Contains("Left")) return OVRInput.Controller.LTouch;
            if (t.name.Contains("Right")) return OVRInput.Controller.RTouch;
        }
        return OVRInput.Controller.None;
    }
}
