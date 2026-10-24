using UnityEngine.EventSystems;

namespace Forge.ML
{
    /// <summary>Mouse, ray and poke input on the <see cref="Model3D"/> stage: hover, click, drag (turn), scroll (zoom).</summary>
    public sealed class MLModelLayerInput : UnityEngine.MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerMoveHandler,
        IPointerDownHandler, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
    {
        public Model3D owner;
        public void OnPointerEnter(PointerEventData e) { if (owner != null) owner.PointerMove(e); }
        public void OnPointerExit(PointerEventData e) { if (owner != null) owner.PointerExit(); }
        public void OnPointerMove(PointerEventData e) { if (owner != null) owner.PointerMove(e); }
        public void OnPointerDown(PointerEventData e) { if (owner != null) owner.PointerDown(); }
        public void OnPointerClick(PointerEventData e) { if (owner != null) owner.Click(e); }
        public void OnBeginDrag(PointerEventData e) { if (owner != null) owner.BeginDrag(e); }
        public void OnDrag(PointerEventData e) { if (owner != null) owner.Drag(e); }
        public void OnEndDrag(PointerEventData e) { if (owner != null) owner.EndDrag(); }
        public void OnScroll(PointerEventData e) { if (owner != null) owner.Scroll(e); }
    }
}
