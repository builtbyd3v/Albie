using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// Albie brings you a CAD part ("Albie, hand me a fin"). CadTakeApart takes the part off and it flies into his
    /// mouth (this sets CadSession.PartCarrier); on CadSession.PartTaken he walks to the spot nearest you
    /// (GatorController.StationAt, which faces you), bounces and hands it over: the part floats up in front of your
    /// eyes (CadTakeApart.TakeOff with no carrier). Put back, or grabbed out of his mouth: the trip ends. The link is
    /// one-way: CAD never knows about the pet. Lead gator only (PetRuntimeSetup adds it). No allocations in Update.
    /// </summary>
    [RequireComponent(typeof(GatorController))]
    [DisallowMultipleComponent]
    public class GatorFetch : MonoBehaviour
    {
        [SerializeField, Tooltip("Jaw bone the part rides on. Auto-found by name if empty (Gator_Model: Rig/root/body/head/jaw).")]
        Transform jawBone;
        [SerializeField, Tooltip("Bone name searched when jawBone is empty.")]
        string jawBoneName = "jaw";
        [SerializeField, Tooltip("Where the part sits, in the gator's own space at rest (m): between the jaws, near the snout.")]
        Vector3 mouth = new Vector3(0f, 0.04f, 0.13f);
        [SerializeField, Tooltip("Stop this far (m, flat) short of the point under your head.")]
        float standOff = 0.25f;
        [SerializeField, Tooltip("Hand over after this long (s) even if he hasn't got there.")]
        float maxTripSeconds = 5f;

        GatorController gator;
        NavMeshAgent agent;
        Transform carry;          // child of the jaw at the mouth: CadSession.PartCarrier
        CadSession session;
        string carrying;          // the part in his mouth, until it is handed over or put back
        float tripStart;
        bool wasStationed;

        void Awake()
        {
            gator = GetComponent<GatorController>();
            agent = GetComponent<NavMeshAgent>();
            if (jawBone == null) jawBone = FindChild(transform, jawBoneName);
            carry = new GameObject("FetchCarry").transform;
            Transform bone = jawBone != null ? jawBone : transform;
            carry.SetParent(bone, false);
            carry.position = transform.TransformPoint(mouth);
        }

        void OnDisable() => Hook(null);

        void OnDestroy()
        {
            if (carry != null) Destroy(carry.gameObject);
        }

        void Update()
        {
            if (session != CadSession.Instance) Hook(CadSession.Instance);  // CAD loads later than the pet
            if (carrying == null) return;
            float elapsed = Time.time - tripStart;
            bool still = agent == null || !agent.enabled || agent.velocity.sqrMagnitude < 0.0004f;
            if ((elapsed > 0.8f && still && !gator.IsHopping) || elapsed > maxTripSeconds) HandOver();
        }

        void Hook(CadSession next)
        {
            if (session != null)
            {
                session.PartTaken -= OnPartTaken;
                session.PartPutBack -= OnPartPutBack;
                if (session.PartCarrier == carry) session.PartCarrier = null;
            }
            session = next;
            if (session == null) return;
            session.PartTaken += OnPartTaken;
            session.PartPutBack += OnPartPutBack;
            session.PartCarrier = carry;
        }

        void OnPartTaken(string id)
        {
            carrying = id;
            tripStart = Time.time;
            wasStationed = gator.Stationed;
            Transform head = PetUser.Head;
            if (head == null || wasStationed) return;  // the arrival greeting holds his spot: hand over from there
            Vector3 under = head.position;
            under.y = transform.position.y;
            Vector3 back = transform.position - under;
            back.y = 0f;
            Vector3 spot = back.sqrMagnitude > standOff * standOff ? under + back.normalized * standOff : transform.position;
            gator.StationAt(spot);  // clamped onto his surface, keep-out safe, facing you
        }

        void OnPartPutBack(string id)
        {
            if (id != carrying) return;
            EndTrip();
        }

        void HandOver()
        {
            string id = carrying;
            EndTrip();
            gator.PlayEmote("Bounce");
            var apart = FindFirstObjectByType<CadTakeApart>();
            if (apart != null) apart.TakeOff(id, null);
        }

        void EndTrip()
        {
            carrying = null;
            if (!wasStationed) gator.ReleaseStation();
        }

        static Transform FindChild(Transform root, string childName)
        {
            if (string.IsNullOrEmpty(childName)) return null;
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChild(root.GetChild(i), childName);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
