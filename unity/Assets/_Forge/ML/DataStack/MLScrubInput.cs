using UnityEngine;
using UnityEngine.EventSystems;

namespace Forge.ML
{
    /// <summary>Press or drag on the dataset barcode to jump the deck there (mouse, ray, poke).</summary>
    public sealed class MLScrubInput : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public DataStack owner;
        public void OnPointerDown(PointerEventData e) { if (owner != null) owner.ScrubBar(e); }
        public void OnBeginDrag(PointerEventData e) { if (owner != null) owner.ScrubBar(e); }
        public void OnDrag(PointerEventData e) { if (owner != null) owner.ScrubBar(e); }
        public void OnEndDrag(PointerEventData e) { }
    }
}
