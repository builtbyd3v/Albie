using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// The buttons on Albie's pet.say card (the hub's fix offers: "Fix it" / "Not now", agent/watcher.py): a small
    /// ForgePanel above him, facing you: the card title (the error) over one ForgePill per action. A tap sends
    /// pet.card_action {pet_id, action_id} and folds the card; a new line without actions, or showSeconds, folds it too.
    /// Mouse and Interaction SDK ray / poke come with the ForgePanel prefab. Lead gator only (PetRuntimeSetup adds it).
    /// No allocations in Update.
    /// </summary>
    [DisallowMultipleComponent]
    public class GatorFixCard : MonoBehaviour
    {
        [SerializeField, Tooltip("Core/Prefabs/ForgePanel.prefab.")]
        ForgePanel panelPrefab;
        [SerializeField, Tooltip("The hub socket. Auto-found on this GameObject or in the scene if empty.")]
        AgentClient client;
        [SerializeField, Tooltip("Only this pet's pet.say lines.")]
        string petId = "bit";
        [SerializeField, Tooltip("Card size (m).")]
        Vector2 size = new Vector2(0.3f, 0.18f);
        [SerializeField, Tooltip("Card centre this high above Albie (m).")]
        float above = 0.2f;
        [SerializeField, Tooltip("Fold the card after this long (s) without a tap.")]
        float showSeconds = 20f;

        const float PillGap = 12f;  // mm, between the buttons
        const float Pad = 16f, TextSize = 16f;  // mm

        ForgePanel panel;
        TMP_Text line;  // the card title, in the content (the header leaves no room for an error message)
        readonly List<ForgePill> pills = new List<ForgePill>();
        float hideAt;

        void OnEnable()
        {
            if (client == null) client = GetComponent<AgentClient>();
            if (client == null) client = FindFirstObjectByType<AgentClient>();
            if (client != null) client.EventReceived += OnEvent;
        }

        void OnDisable()
        {
            if (client != null) client.EventReceived -= OnEvent;
            Hide();
        }

        void OnDestroy()
        {
            if (panel != null) Destroy(panel.gameObject);
        }

        void OnEvent(PetEvent e)
        {
            if (e == null || e.type != "pet.say") return;
            string pid = e.GetString("pet_id");
            if (!string.IsNullOrEmpty(pid) && pid != petId) return;
            var card = e.Get("card") as JObject;
            if (!(card?["actions"] is JArray actions) || actions.Count == 0)
            {
                Hide();  // a new line without buttons: the old offer is stale
                return;
            }
            Show((string)card["title"] ?? "", actions);
        }

        void Show(string title, JArray actions)
        {
            if (panelPrefab == null) return;
            if (panel == null)
            {
                panel = Instantiate(panelPrefab);
                panel.name = "AlbieCard";
                panel.SizeMeters = size;
                panel.Title = "Albie";
                var placeholder = panel.Content.Find("Placeholder");
                if (placeholder != null) placeholder.gameObject.SetActive(false);
                line = new GameObject("Line", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
                line.rectTransform.SetParent(panel.Content, false);
                line.rectTransform.anchorMin = new Vector2(0f, 0.45f);
                line.rectTransform.anchorMax = Vector2.one;
                line.rectTransform.offsetMin = new Vector2(Pad, 0f);
                line.rectTransform.offsetMax = new Vector2(-Pad, -Pad / 4f);
                if (ForgeTheme.Font != null) line.font = ForgeTheme.Font;
                line.fontSize = TextSize;
                line.alignment = TextAlignmentOptions.Center;
                line.textWrappingMode = TextWrappingModes.Normal;
                line.overflowMode = TextOverflowModes.Ellipsis;
                line.raycastTarget = false;
            }
            panel.gameObject.SetActive(true);
            line.text = title;
            foreach (var p in pills)
                if (p != null) Destroy(p.gameObject);
            pills.Clear();
            float width = 0f;
            foreach (var a in actions)
            {
                string id = (string)a["action_id"];
                if (string.IsNullOrEmpty(id)) continue;
                var pill = ForgePill.Create(panel.Content, (string)a["label"] ?? id, () => Tap(id));
                pills.Add(pill);
                width += pill.Rect.sizeDelta.x;
            }
            width += PillGap * (pills.Count - 1);
            float x = -width / 2f;
            foreach (var pill in pills)
            {
                var rt = pill.Rect;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.25f);
                rt.anchoredPosition = new Vector2(x + rt.sizeDelta.x / 2f, 0f);
                x += rt.sizeDelta.x + PillGap;
            }
            hideAt = Time.time + showSeconds;
            Place();
        }

        void Tap(string actionId)
        {
            if (client != null) client.Send("pet.card_action", new JObject { ["pet_id"] = petId, ["action_id"] = actionId });
            Hide();
        }

        void Update()
        {
            if (panel == null || !panel.gameObject.activeSelf) return;
            if (Time.time > hideAt) Hide();
            else Place();
        }

        // Above Albie, turned to face you (a world-space canvas reads from behind its +Z).
        void Place()
        {
            Vector3 at = transform.position + Vector3.up * above;
            Transform head = PetUser.Head;
            Vector3 look = head != null ? at - head.position : transform.forward;
            look.y = 0f;
            if (look.sqrMagnitude < 1e-6f) look = Vector3.forward;
            panel.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look));
        }

        void Hide()
        {
            if (panel != null) panel.gameObject.SetActive(false);
        }
    }
}
