using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Forge.Pet
{
    /// <summary>
    /// Poke / click the gator: plays "Clicked", shows Mood.Happy briefly, then restores the previous mood.
    /// Fallback input (no Meta types): left mouse click raycast from the head camera, or an XR
    /// controller trigger raycast straight forward from PetUser.Head.
    /// Expects a Collider on this root (adds a BoxCollider fitted to the renderers if missing).
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorInteraction : MonoBehaviour
    {
        [Header("Poke")]
        [SerializeField, Tooltip("Seconds Mood.Happy is shown before restoring the previous mood.")]
        float happySeconds = 1.5f;
        [SerializeField, Tooltip("Ignore pokes closer together than this (s).")]
        float cooldown = 0.3f;

        [Header("Fallback input (mouse / XR trigger raycast)")]
        [SerializeField, Tooltip("Enable the built-in mouse/trigger raycast.")]
        bool fallbackInput = true;
        [SerializeField, Tooltip("Max raycast distance (m).")]
        float maxDistance = 10f;
        [SerializeField, Tooltip("Layers the raycast can hit (occluders included).")]
        LayerMask raycastMask = ~0;
        [SerializeField, Tooltip("XR select button. Default: any XR controller trigger.")]
        InputAction xrSelect = new InputAction("XRSelect", InputActionType.Button, "<XRController>/{TriggerButton}");

        [Header("Meta Interaction SDK")]
        // A PokeInteractable / RayInteractable on this gator with an InteractableUnityEventWrapper can call
        // GatorInteraction.Poke() from its WhenSelect UnityEvent (wire it in the prefab). No Meta types here.
        [SerializeField, Tooltip("Raised after every accepted poke (for SFX, haptics, etc.).")]
        UnityEvent onPoked = new UnityEvent();

        public event Action Poked;

        GatorController controller;
        GatorExpressions expressions;
        Collider ownCollider;
        Mood moodBeforePoke;
        float revertAt = -1f, lastPoke = -10f;

        void Awake()
        {
            controller = GetComponent<GatorController>();
            expressions = GetComponent<GatorExpressions>();
            ownCollider = GetComponent<Collider>();
            if (ownCollider == null) ownCollider = AddFittedCollider();
        }

        void OnEnable() { if (fallbackInput) xrSelect.Enable(); }
        void OnDisable() => xrSelect.Disable();

        /// <summary>Poke the gator (call from UnityEvents, Interaction SDK wrappers, hotkeys...).</summary>
        public void Poke()
        {
            if (Time.time - lastPoke < cooldown) return;
            lastPoke = Time.time;

            if (controller != null) controller.PlayEmote("Clicked");
            if (expressions != null)
            {
                if (revertAt < 0f) moodBeforePoke = expressions.CurrentMood;   // keep the original across rapid pokes
                expressions.SetMood(Mood.Happy, 0.1f);
                revertAt = Time.time + happySeconds;
            }
            onPoked.Invoke();
            Poked?.Invoke();
        }

        void Update()
        {
            // Restore the previous mood unless someone else changed it in the meantime.
            if (revertAt > 0f && Time.time >= revertAt)
            {
                revertAt = -1f;
                if (expressions != null && expressions.CurrentMood == Mood.Happy) expressions.SetMood(moodBeforePoke);
            }

            if (!fallbackInput) return;

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                Camera cam = PetUser.HeadCamera;
                if (cam != null) TryPoke(cam.ScreenPointToRay(mouse.position.ReadValue()));
            }
            if (xrSelect.enabled && xrSelect.WasPressedThisFrame())
            {
                Transform head = PetUser.Head;
                if (head != null) TryPoke(new Ray(head.position, head.forward));
            }
        }

        void TryPoke(Ray ray)
        {
            if (ownCollider == null) return;
            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Collide)
                && (hit.collider == ownCollider || hit.collider.transform.IsChildOf(transform)))
                Poke();
        }

        // Fallback: trigger BoxCollider around all child renderers (local space of this root).
        Collider AddFittedCollider()
        {
            Debug.LogWarning("[GatorInteraction] No Collider on " + name + "; adding a fitted BoxCollider.", this);
            var box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            Renderer[] rs = GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return box;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            box.center = transform.InverseTransformPoint(b.center);
            Vector3 s = transform.lossyScale;
            box.size = new Vector3(b.size.x / Mathf.Max(s.x, 1e-4f), b.size.y / Mathf.Max(s.y, 1e-4f), b.size.z / Mathf.Max(s.z, 1e-4f));
            return box;
        }
    }
}
