using UnityEngine;
using UnityEngine.EventSystems;

namespace Forge.ML
{
    /// <summary>Ray, poke and mouse hit surface for the GPU-instanced results wall.</summary>
    public class MLResultsWallInput : MonoBehaviour, IPointerClickHandler, IPointerMoveHandler, IPointerExitHandler
    {
        public ResultsWall owner;
        public void OnPointerClick(PointerEventData eventData) => owner.SelectAt(eventData);
        public void OnPointerMove(PointerEventData eventData) => owner.HoverAt(eventData);
        public void OnPointerExit(PointerEventData eventData) => owner.HideHover();
    }
}
