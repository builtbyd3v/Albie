using UnityEngine;
using UnityEngine.EventSystems;
namespace Forge.ML
{
    // The hit surface lives under ForgePanel.Content; relay to the component on the prefab root.
    public class MLStackInput : MonoBehaviour, IScrollHandler, IBeginDragHandler, IDragHandler, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public DataStack owner;
        public void OnScroll(PointerEventData e) => owner.OnScroll(e);
        public void OnBeginDrag(PointerEventData e) => owner.OnBeginDrag(e);
        public void OnDrag(PointerEventData e) => owner.OnDrag(e);
        public void OnPointerEnter(PointerEventData e) => owner.OnPointerEnter(e);
        public void OnPointerExit(PointerEventData e) => owner.OnPointerExit(e);
        public void OnPointerClick(PointerEventData e) => owner.OnPointerClick(e);
    }
}
