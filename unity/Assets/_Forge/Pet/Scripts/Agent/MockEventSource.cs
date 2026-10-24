using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// SANDBOX ONLY (PetSandbox / PetSandboxXR; GatorSetup adds it to the scene instance, never to the prefab, so Main
    /// has no mock mode). While it sits enabled on the gator, GatorAgentBridge uses it whatever pet_config.json says
    /// (overrideConfig); untick the component to test the live hub path from a sandbox scene.
    /// Offline event source: no network. Send("agent.ask") plays a scripted reply (editable in the
    /// inspector, pre-filled in Reset). Asks containing fix/add/change/make (or mode "code") also play
    /// the task sequence. "{ask}" in a payloadJson is replaced with the asked text. pet_id is injected
    /// into every payload, task_id into agent.task_* payloads. stt.* / hello are accepted and ignored.
    /// Send("mock.error") emits agent.error; Send("agent.cancel") stops the running script.
    /// </summary>
    public class MockEventSource : MonoBehaviour, IPetEventSource
    {
        [Serializable]
        public class ScriptedEvent
        {
            public string type;
            [Tooltip("Delay before this event, in milliseconds.")] public int delayMs;
            [TextArea(1, 4)] public string payloadJson = "{}";
            public ScriptedEvent() { }
            public ScriptedEvent(string type, int delayMs, string payloadJson) { this.type = type; this.delayMs = delayMs; this.payloadJson = payloadJson; }
        }

        [SerializeField, Tooltip("Played for every agent.ask.")]
        List<ScriptedEvent> answerSequence = new List<ScriptedEvent>();
        [SerializeField, Tooltip("Played after the answer when the ask looks like a code request.")]
        List<ScriptedEvent> taskSequence = new List<ScriptedEvent>();
        [SerializeField, Tooltip("Lower-case words that switch an 'auto' ask into code mode.")]
        string[] codeKeywords = { "fix", "add", "change", "make" };
        [SerializeField, Tooltip("Sandbox: use this mock even when pet_config.json says \"hub\". Untick (or disable the component) to test the live hub from the sandbox.")]
        bool overrideConfig = true;

        /// <summary>The bridge should use this mock regardless of pet_config.json (enabled + overrideConfig).</summary>
        public bool OverridesConfig => overrideConfig && enabled;

        public event Action<PetEvent> EventReceived;
        public bool IsConnected { get; private set; }

        PetConfig _cfg = new PetConfig();
        Coroutine _routine;
        int _taskCounter;

        void Reset() => FillDefaults();

        /// <summary>Default demo script (also used if the inspector lists are empty at runtime).</summary>
        public void FillDefaults()
        {
            answerSequence = new List<ScriptedEvent>
            {
                new ScriptedEvent("agent.thinking", 0, "{}"),
                new ScriptedEvent("agent.partial_answer", 600, "{\"text\":\"The hub lives in hub/,\"}"),
                new ScriptedEvent("agent.partial_answer", 600, "{\"text\":\"The hub lives in hub/, a FastAPI app; the agent code is in hub/agent/\"}"),
                new ScriptedEvent("agent.partial_answer", 600, "{\"text\":\"The hub lives in hub/, a FastAPI app; the agent code is in hub/agent/ and projects are listed in hub/config/projects.yaml.\"}"),
                new ScriptedEvent("agent.answer_done", 400, "{\"text\":\"The hub lives in hub/, a FastAPI app; the agent code is in hub/agent/ and projects are listed in hub/config/projects.yaml.\"}"),
            };
            taskSequence = new List<ScriptedEvent>
            {
                new ScriptedEvent("agent.task_started", 800, "{\"title\":\"{ask}\"}"),
                new ScriptedEvent("agent.task_progress", 1000, "{\"text\":\"Reading the relevant files...\",\"pct\":25}"),
                new ScriptedEvent("agent.task_progress", 1000, "{\"text\":\"Editing code...\",\"pct\":60}"),
                new ScriptedEvent("agent.task_progress", 1000, "{\"text\":\"Running checks...\",\"pct\":90}"),
                new ScriptedEvent("agent.task_complete", 1000, "{\"summary\":\"Done! Changes are on a new branch.\",\"branch\":\"pet/mock-change\"}"),
            };
        }

        public void Connect(PetConfig cfg)
        {
            _cfg = cfg ?? new PetConfig();
            if (answerSequence == null || answerSequence.Count == 0) FillDefaults();
            IsConnected = true;
        }

        public void Disconnect()
        {
            StopScript();
            IsConnected = false;
        }

        public void Send(string type, object payload)
        {
            if (string.IsNullOrEmpty(type)) return;
            if (type == "agent.ask")
            {
                JObject p = payload as JObject ?? (payload != null ? JObject.FromObject(payload) : new JObject());
                string text = (string)p["text"] ?? string.Empty;
                string mode = (string)p["mode"] ?? "auto";
                bool code = mode == "code" || (mode != "qa" && LooksLikeCode(text));
                StopScript();
                _routine = StartCoroutine(Play(text, code));
            }
            else if (type == "agent.cancel") StopScript();
            else if (type == "mock.error")
            {
                StopScript();
                Emit("agent.error", new JObject { ["message"] = "Mock error: something went wrong." });
            }
            // hello, stt.start/stop and anything else: accepted and ignored.
        }

        bool LooksLikeCode(string text)
        {
            if (string.IsNullOrEmpty(text) || codeKeywords == null) return false;
            string lower = text.ToLowerInvariant();
            foreach (var k in codeKeywords)
                if (!string.IsNullOrEmpty(k) && lower.Contains(k)) return true;
            return false;
        }

        IEnumerator Play(string asked, bool codeMode)
        {
            yield return PlayList(answerSequence, asked, null);
            if (codeMode)
            {
                string taskId = "mock-" + (++_taskCounter);
                yield return PlayList(taskSequence, asked, taskId);
            }
            _routine = null;
        }

        IEnumerator PlayList(List<ScriptedEvent> list, string asked, string taskId)
        {
            if (list == null) yield break;
            for (int i = 0; i < list.Count; i++)
            {
                var se = list[i];
                if (se == null || string.IsNullOrEmpty(se.type)) continue;
                if (se.delayMs > 0) yield return new WaitForSeconds(se.delayMs / 1000f);
                JObject p = ParsePayload(se.payloadJson, asked);
                if (taskId != null && se.type.StartsWith("agent.task_", StringComparison.Ordinal) && p["task_id"] == null)
                    p["task_id"] = taskId;
                Emit(se.type, p);
            }
        }

        static JObject ParsePayload(string json, string asked)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            if (json.Contains("{ask}"))
            {
                // JSON-escape the asked text (strip surrounding quotes from the serialized string).
                string esc = new JValue(asked ?? string.Empty).ToString(Newtonsoft.Json.Formatting.None);
                json = json.Replace("{ask}", esc.Substring(1, esc.Length - 2));
            }
            try { return JObject.Parse(json); }
            catch (Exception e) { Debug.LogWarning("[MockEventSource] Bad payloadJson (" + e.Message + "): " + json); return new JObject(); }
        }

        void Emit(string type, JObject payload)
        {
            payload["pet_id"] = _cfg != null ? _cfg.petId : "bit";
            try { EventReceived?.Invoke(new PetEvent(type, payload)); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void StopScript()
        {
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
        }

        void OnDisable() => StopScript();
    }
}
