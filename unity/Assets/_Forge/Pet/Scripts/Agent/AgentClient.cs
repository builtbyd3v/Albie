using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// The pet's hub connection, on top of Core's ForgeNet (the one /ws socket per Unity client; the hub runs the agent
    /// session on it). Subscribes to the agent's event types, sends hello whenever ForgeNet goes Live (first connect and
    /// every reconnect), and forwards binary frames (voice-mode speech, PCM16) as BinaryReceived. Everything is raised on
    /// the main thread, in socket order (tts.start → frames → tts.end). The hub address is ForgeNet's (hubUrl on the
    /// ForgeNet object or PlayerPrefs Forge.HubUrl); pet_config.json's hubUrl is no longer used.
    /// </summary>
    public class AgentClient : MonoBehaviour, IPetEventSource
    {
        // Hub → client types the pet cares about (docs/PET_AGENT_CONTRACT.md, incl. Addenda B and D).
        static readonly string[] EventTypes =
        {
            "pet.state", "pet.say",
            "stt.partial", "stt.final",
            "agent.thinking", "agent.partial_answer", "agent.answer_done",
            "agent.task_started", "agent.task_progress", "agent.task_complete", "agent.error", "agent.tool_call",
            "voice.ready", "tts.start", "tts.end", "tts.stop",
            "crew.spawn", "crew.dismiss", "wake.listen",
        };

        public event Action<PetEvent> EventReceived;
        /// <summary>Raised on the main thread when IsConnected flips.</summary>
        public event Action<bool> ConnectionChanged;
        /// <summary>Binary frame from the hub (main thread). The buffer is reused after the call:
        /// handlers must copy what they need before returning. Valid bytes = [0, length).</summary>
        public event Action<byte[], int> BinaryReceived;

        public bool IsConnected => _active && ForgeNet.Status == ForgeNetStatus.Live;
        /// <summary>Human-readable connection status for UI.</summary>
        public string Status => !_active ? "disconnected" : ForgeNet.Status == ForgeNetStatus.Live ? "connected" : ForgeNet.Status.ToString().ToLowerInvariant();
        /// <summary>The hub announced a speech-to-speech session for this socket (voice.ready); replies come as tts.* + audio.</summary>
        public bool VoiceActive { get; private set; }
        /// <summary>Sample rate from voice.ready (0 until then).</summary>
        public int VoiceSampleRate { get; private set; }

        readonly List<IDisposable> _subscriptions = new List<IDisposable>(EventTypes.Length);
        PetConfig _cfg;
        bool _active, _lastReportedConnected, _warnedDrop;
        int _audioOutRate;

        // ---------------- public API ----------------

        public void Connect(PetConfig cfg)
        {
            _cfg = cfg ?? new PetConfig();
            _audioOutRate = AudioSettings.outputSampleRate;
            Disconnect();
            if (!isActiveAndEnabled) return;              // OnEnable will start it
            for (int i = 0; i < EventTypes.Length; i++)
            {
                string type = EventTypes[i];
                _subscriptions.Add(ForgeNet.On(type, payload => OnHubEvent(type, payload)));
            }
            ForgeNet.StatusChanged += OnStatusChanged;
            ForgeNet.BinaryReceived += OnBinary;
            _active = true;
            if (ForgeNet.Status == ForgeNetStatus.Live) SendHello();
        }

        public void Disconnect()
        {
            if (_active)
            {
                ForgeNet.StatusChanged -= OnStatusChanged;
                ForgeNet.BinaryReceived -= OnBinary;
            }
            for (int i = 0; i < _subscriptions.Count; i++) _subscriptions[i]?.Dispose();
            _subscriptions.Clear();
            _active = false;
            VoiceActive = false; VoiceSampleRate = 0;
        }

        /// <summary>Sends a JSON event. Dropped (with one warning) while offline.</summary>
        public void Send(string type, object payload)
        {
            if (!IsConnected || !ForgeNet.Send(type, payload)) WarnDrop(type);
        }

        /// <summary>Sends a binary frame (audio). ForgeNet copies it, so the caller may reuse <paramref name="buf"/>.</summary>
        public void SendAudio(byte[] buf, int length)
        {
            if (!IsConnected || buf == null || length <= 0) return;
            ForgeNet.SendBinary(buf, 0, length);
        }

        // ---------------- Unity lifecycle ----------------

        void OnEnable() { if (_cfg != null && !_active) Connect(_cfg); }
        void OnDisable() => Disconnect();
        void OnDestroy() => Disconnect();

        void Update()
        {
            bool connected = IsConnected;
            if (_lastReportedConnected == connected) return;
            _lastReportedConnected = connected;
            if (!connected) { VoiceActive = false; VoiceSampleRate = 0; }
            ConnectionChanged?.Invoke(connected);
        }

        // ---------------- ForgeNet callbacks (main thread) ----------------

        void OnStatusChanged(ForgeNetStatus status)
        {
            if (status == ForgeNetStatus.Live) SendHello();   // new socket: the hub needs hello again
            else { VoiceActive = false; VoiceSampleRate = 0; }
        }

        void OnHubEvent(string type, JToken payload)
        {
            var e = new PetEvent(type, payload as JObject);
            if (type == "voice.ready") { VoiceActive = true; VoiceSampleRate = e.GetInt("sample_rate"); }
            EventReceived?.Invoke(e);
        }

        void OnBinary(byte[] data, int length) => BinaryReceived?.Invoke(data, length);

        void SendHello()
        {
            if (_cfg == null) return;
            _warnedDrop = false;
            ForgeNet.Send("hello", new JObject
            { ["client"] = "unity", ["pet_id"] = _cfg.petId, ["project_id"] = _cfg.projectId, ["audio_out_rate"] = _audioOutRate });
            Debug.Log("[AgentClient] hello sent over ForgeNet (" + ForgeNet.Url("/ws") + ")");
        }

        void WarnDrop(string type)
        {
            if (_warnedDrop) return;
            _warnedDrop = true;
            Debug.LogWarning("[AgentClient] Hub not connected (ForgeNet " + ForgeNet.Status + "); dropping outbound '" + type + "' (further drops silent until connected).");
        }
    }
}
