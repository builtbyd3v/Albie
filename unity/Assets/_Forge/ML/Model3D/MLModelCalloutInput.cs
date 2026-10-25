using UnityEngine.EventSystems;

namespace Forge.ML
{
    /// <summary>Keeps the <see cref="Model3D"/> block callout open while a pointer is on it, so Open can be pressed.</summary>
    public sealed class MLModelCalloutInput : UnityEngine.MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Model3D owner;
        public void OnPointerEnter(PointerEventData e) { if (owner != null) owner.CalloutHover(true); }
        public void OnPointerExit(PointerEventData e) { if (owner != null) owner.CalloutHover(false); }
    }
}
