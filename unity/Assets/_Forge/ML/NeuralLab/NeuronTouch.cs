using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Forge.ML
{
    /// <summary>
    /// One hidden neuron of <see cref="NeuralNet3D"/> you can switch off and on: touch it with a fingertip (any
    /// PokeInteractor, checked by NeuralNet3D), select it with a controller or hand ray (its RayInteractable), or click
    /// it with the mouse (<see cref="NeuronRaycaster"/>). Same rules as CAD's Expand ring (CadExpandButton).
    /// </summary>
    public sealed class NeuronTouch : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public static readonly List<NeuronTouch> All = new List<NeuronTouch>();

        // This project enters Play mode without a domain reload: statics would carry over from the last session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => All.Clear();

        public int layer, index;  // index from 0, like the network's arrays
        public bool Hovered => mouse || poked || (ray != null && (ray.State == InteractableState.Hover || ray.State == InteractableState.Select));
        public SphereCollider Collider { get; private set; }
        bool mouse, poked, pokeArmed = true;
        RayInteractable ray;

        public void Setup(int layer, int index)
        {
            this.layer = layer; this.index = index;
            Collider = GetComponent<SphereCollider>();
            if (Collider == null) Collider = gameObject.AddComponent<SphereCollider>();
            Collider.radius = 0.6f;          // a little larger than the sphere: easier to hit
            Collider.excludeLayers = ~0;     // pointing only: it bumps into nothing
            var surface = gameObject.AddComponent<ColliderSurface>();
            surface.InjectAllColliderSurface(Collider);
            ray = gameObject.AddComponent<RayInteractable>();
            ray.InjectAllRayInteractable(surface);
            ray.WhenSelectingInteractorAdded.Action += OnRaySelect;
        }

        void OnEnable() => All.Add(this);
        void OnDisable() { All.Remove(this); mouse = poked = false; }
        void OnDestroy() { if (ray != null) ray.WhenSelectingInteractorAdded.Action -= OnRaySelect; }

        void OnRaySelect(RayInteractor interactor) => Toggle();
        public void OnPointerClick(PointerEventData e) => Toggle();
        public void OnPointerEnter(PointerEventData e) => mouse = true;
        public void OnPointerExit(PointerEventData e) => mouse = false;

        /// <summary>Fingertip at <paramref name="distance"/> (m) from the neuron's surface: a touch switches it once;
        /// pulling 2 cm away arms it again.</summary>
        public void Poke(float distance)
        {
            poked = distance < 0.04f;
            if (distance < 0.008f && pokeArmed) { pokeArmed = false; Toggle(); }
            else if (distance > 0.02f) pokeArmed = true;
        }

        void Toggle() => NeuralLabSim.Instance.ToggleNeuron(layer, index + 1);

        public static bool Raycast(Ray ray, float max, out RaycastHit best, out NeuronTouch target)
        {
            best = default; target = null; float nearest = max;
            foreach (var n in All)
                if (n.Collider != null && n.Collider.Raycast(ray, out var hit, nearest)) { nearest = hit.distance; best = hit; target = n; }
            return target != null;
        }
    }

    /// <summary>Puts the 3D neurons into the EventSystem's raycast for a mouse (DesktopRig's pointer, or a mouse input
    /// module), like CadPartRaycaster does for CAD parts. XR rays reach them through their RayInteractables.</summary>
    public sealed class NeuronRaycaster : BaseRaycaster
    {
        public override Camera eventCamera => Camera.main;

        public override void Raycast(PointerEventData e, List<RaycastResult> results)
        {
            if (NeuronTouch.All.Count == 0) return;
            Ray ray;
            var rig = DesktopRig.Current;
            if (rig != null && rig.OwnsPointer(e)) ray = rig.PointerRay;
            else if (e is ExtendedPointerEventData x && x.pointerType == UIPointerType.MouseOrPen && Camera.main != null)
                ray = Camera.main.ScreenPointToRay(e.position);
            else return;
            if (!NeuronTouch.Raycast(ray, 6f, out var hit, out var neuron)) return;
            results.Add(new RaycastResult
            {
                gameObject = neuron.gameObject, module = this, distance = hit.distance, worldPosition = hit.point,
                worldNormal = hit.normal, screenPosition = e.position, index = results.Count,
            });
        }
    }
}
