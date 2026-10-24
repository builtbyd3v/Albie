using System.Collections.Generic;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Desktop sandbox only (PetSandbox.unity, no MRUK): builds the runtime NavMesh that MRUK + GatorMrukAdapter
    /// build in the XR scene. A 20x20 m floor surface with holes for the obstacles and the desk, plus a desk
    /// surface over the Desk cube's top face. Wired by GatorSetup.BuildSandboxScene.
    /// Surfaces are built in Awake (early execution order) so the gator's NavMeshAgent finds a NavMesh as soon
    /// as possible; the wander area is handed to the controller in Start, after it has initialised.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class GatorSandboxNav : MonoBehaviour
    {
        [SerializeField] GatorNavSurface floor;
        [SerializeField] GatorNavSurface desk;
        [SerializeField, Tooltip("Empty = first GatorController in the scene.")] GatorController controller;
        [SerializeField] Vector3 floorCenter = Vector3.zero;
        [SerializeField] Vector2 floorSize = new Vector2(20f, 20f);
        [SerializeField, Tooltip("The Desk cube (unit cube scaled to the desk size).")] Transform deskTop;
        [SerializeField, Tooltip("Desk walkable area is shrunk by this much on every edge (m).")] float deskInset = 0.03f;
        [SerializeField] List<Collider> obstacles = new List<Collider>();

        void Awake()
        {
            if (floor != null)
            {
                var holes = new List<Bounds>(obstacles.Count + 1);
                for (int i = 0; i < obstacles.Count; i++)
                    if (obstacles[i] != null) holes.Add(obstacles[i].bounds);
                if (deskTop != null)
                {
                    Collider dc = deskTop.GetComponent<Collider>();
                    holes.Add(dc != null ? dc.bounds : new Bounds(deskTop.position, deskTop.lossyScale));
                }
                floor.BuildBox(floorCenter, Quaternion.identity, floorSize, holes);
                GatorNavSurface.Floor = floor;
            }
            else Debug.LogWarning("[GatorSandboxNav] No floor GatorNavSurface assigned.", this);

            if (desk != null && deskTop != null)
            {
                Vector3 s = deskTop.lossyScale;
                Vector3 topCenter = deskTop.position + deskTop.up * (s.y * 0.5f);
                Quaternion yaw = Quaternion.Euler(0f, deskTop.eulerAngles.y, 0f);
                var size = new Vector2(Mathf.Max(0.05f, s.x - 2f * deskInset), Mathf.Max(0.05f, s.z - 2f * deskInset));
                desk.BuildBox(topCenter, yaw, size);
                GatorNavSurface.Desk = desk;
            }
        }

        void Start()
        {
            if (controller == null) controller = FindAnyObjectByType<GatorController>();
            if (controller != null && floor != null) controller.SetWanderArea(floor);
        }
    }
}
