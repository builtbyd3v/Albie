using UnityEngine.EventSystems;

namespace Forge.ML
{
    public sealed class MLModelLayerInput : UnityEngine.MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public Model3D owner;
        public int layerIndex;
        public void OnPointerEnter(PointerEventData eventData) { if (owner != null) owner.HighlightLayer(layerIndex); }
        public void OnPointerExit(PointerEventData eventData) { if (owner != null) owner.ClearHighlight(); }
        public void OnPointerClick(PointerEventData eventData) { if (owner != null) owner.HighlightLayer(layerIndex); }
    }
}
