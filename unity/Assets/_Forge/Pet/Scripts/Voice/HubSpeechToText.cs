using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Real STT: streams the default microphone as PCM16 mono 16 kHz binary frames over the
    /// AgentClient socket (stt.start → binary chunks → stt.stop). Transcripts come back as
    /// stt.partial / stt.final events and are re-raised as Partial/FinalTranscript.
    /// No per-frame allocations: fixed chunk buffers are allocated once per mic start.
    /// Level (IMicLevel) is the RMS of the last chunk sent, for PushToTalk's hands-free endpointing.
    /// Monitoring (barge-in, GatorFollowUp): the mic runs and Level updates while the gator speaks, but nothing is
    /// sent. The last preRollSeconds are kept and sent right after stt.start, so the first words of a barge-in survive.
    /// </summary>
    public class HubSpeechToText : MonoBehaviour, ISpeechToText, IMicLevel
    {
        const int TargetRate = 16000;

        [SerializeField, Tooltip("Socket used for audio + transcripts. Auto-found on this GameObject / scene if empty.")]
        AgentClient client;
        [SerializeField, Tooltip("Pet id sent in stt.* payloads (overridden by GatorAgentBridge config when available).")]
        string petId = "bit";
        [SerializeField, Tooltip("Microphone ring-buffer length in seconds.")]
        int ringSeconds = 2;
        [SerializeField, Tooltip("How often new mic samples are read and sent (seconds).")]
        float sendInterval = 0.1f;
        [SerializeField, Tooltip("Monitoring: audio kept before stt.start and sent with it (seconds).")]
        float preRollSeconds = 0.5f;

        public bool IsListening { get; private set; }
        public bool IsMonitoring { get; private set; }
        /// <summary>RMS (0..1) of the last ~100 ms chunk; 0 when not listening.</summary>
        public float Level { get; private set; }
        public event Action<string> PartialTranscript;
        public event Action<string> FinalTranscript;
        public event Action<string> Error;
        /// <summary>Raised just before this starts the mic, so an always-on listener (WakeListener) can let go of it.</summary>
        public static event Action MicNeeded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => MicNeeded = null;

        public string PetId { get => petId; set => petId = value; }

        AudioClip _clip;
        int _clipSamples, _readPos, _chunkIn;     // _chunkIn = device samples per ~100 ms
        float[] _inBuf;                            // device-rate float samples
        byte[] _outBuf;                            // PCM16 LE @16 kHz (1600 samples * 2 bytes)
        float _timer;
        bool _awaitingFinal;                       // a session was started; accept stt.* until stt.final
        byte[][] _preRoll;                         // monitoring: last few chunks, oldest at _preNext once full
        int _preNext, _preCount;
        bool _subscribed;

        void Start() => ResolveClient();

        void ResolveClient()
        {
            if (client == null) client = GetComponent<AgentClient>();
            if (client == null) client = FindAnyObjectByType<AgentClient>();
            if (client != null && !_subscribed) { client.EventReceived += OnEvent; _subscribed = true; }
        }

        public void StartListening()
        {
            if (IsListening) return;
            ResolveClient();
            if (client == null || !client.IsConnected) { StopMonitoring(); Error?.Invoke("hub not connected"); return; }
            if (!IsMonitoring && !StartMic()) return;

            client.Send("stt.start", new JObject { ["pet_id"] = petId, ["sample_rate"] = TargetRate, ["encoding"] = "pcm16" });
            if (IsMonitoring)
            {
                int first = _preCount < _preRoll.Length ? 0 : _preNext;
                for (int i = 0; i < _preCount; i++) client.SendAudio(_preRoll[(first + i) % _preRoll.Length], _outBuf.Length);
                IsMonitoring = false;
            }
            IsListening = true;
            _awaitingFinal = true;
        }

        /// <summary>Barge-in: run the mic and update Level without sending (needs mic permission already granted).</summary>
        public void StartMonitoring()
        {
            if (IsListening || IsMonitoring || !MicPermission.HasPermission) return;
            ResolveClient();
            if (client == null || !client.IsConnected || !StartMic()) return;
            _preNext = _preCount = 0;
            IsMonitoring = true;
        }

        public void StopMonitoring()
        {
            if (!IsMonitoring) return;
            IsMonitoring = false;
            Microphone.End(null);
            _clip = null;
            Level = 0f;
        }

        bool StartMic()
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0) { Error?.Invoke("no microphone"); return false; }
            MicNeeded?.Invoke();                       // an always-on WakeListener lets go of the mic first

            // Ask for 16 kHz; some devices only offer other rates, so we resample on the fly.
            Microphone.GetDeviceCaps(null, out int minF, out int maxF);
            int rate = (minF == 0 && maxF == 0) ? TargetRate : Mathf.Clamp(TargetRate, minF, maxF);
            _clip = Microphone.Start(null, true, Mathf.Max(1, ringSeconds), rate);
            if (_clip == null) { Error?.Invoke("microphone failed to start"); return false; }

            _clipSamples = _clip.samples;
            _chunkIn = Mathf.Max(1, Mathf.RoundToInt(_clip.frequency * sendInterval));
            int chunkOut = Mathf.RoundToInt(TargetRate * sendInterval);
            if (_inBuf == null || _inBuf.Length != _chunkIn * _clip.channels) _inBuf = new float[_chunkIn * _clip.channels];
            if (_outBuf == null || _outBuf.Length != chunkOut * 2) _outBuf = new byte[chunkOut * 2];
            int pre = Mathf.Max(1, Mathf.RoundToInt(preRollSeconds / sendInterval));
            if (_preRoll == null || _preRoll.Length != pre || _preRoll[0].Length != _outBuf.Length)
            {
                _preRoll = new byte[pre][];
                for (int i = 0; i < pre; i++) _preRoll[i] = new byte[_outBuf.Length];
            }
            _readPos = 0;
            _timer = 0f;
            Level = 0f;
            return true;
        }

        public void StopListening()
        {
            if (!IsListening) return;
            IsListening = false;
            PumpAudio();                 // flush whole chunks still in the ring buffer
            Microphone.End(null);
            _clip = null;
            Level = 0f;
            if (client != null) client.Send("stt.stop", new JObject { ["pet_id"] = petId });
            // FinalTranscript is raised when the hub answers stt.final.
        }

        void Update()
        {
            if (!IsListening && !IsMonitoring) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < sendInterval) return;
            _timer = 0f;
            PumpAudio();
        }

        /// <summary>Reads every complete ~100 ms chunk available in the mic ring buffer and sends it.</summary>
        void PumpAudio()
        {
            if (_clip == null || client == null) return;
            int pos = Microphone.GetPosition(null);
            if (pos < 0) return;
            int available = (pos - _readPos + _clipSamples) % _clipSamples;
            while (available >= _chunkIn)
            {
                _clip.GetData(_inBuf, _readPos);   // GetData wraps around the clip end
                int bytes = ConvertChunk(_clip.channels);
                if (IsListening) client.SendAudio(_outBuf, bytes);
                else
                {
                    Buffer.BlockCopy(_outBuf, 0, _preRoll[_preNext], 0, bytes);
                    _preNext = (_preNext + 1) % _preRoll.Length;
                    if (_preCount < _preRoll.Length) _preCount++;
                }
                _readPos = (_readPos + _chunkIn) % _clipSamples;
                available -= _chunkIn;
            }
        }

        /// <summary>Linear-resamples _inBuf (device rate, first channel) to 16 kHz PCM16 LE in _outBuf.</summary>
        int ConvertChunk(int channels)
        {
            int outCount = _outBuf.Length / 2;
            float step = outCount > 1 ? (_chunkIn - 1f) / (outCount - 1f) : 0f;
            float sum = 0f;
            for (int i = 0; i < outCount; i++)
            {
                float src = i * step;
                int i0 = (int)src;
                int i1 = i0 + 1 < _chunkIn ? i0 + 1 : i0;
                float t = src - i0;
                float f = _inBuf[i0 * channels] * (1f - t) + _inBuf[i1 * channels] * t;
                sum += f * f;
                int s = (int)(f * 32767f);
                if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                _outBuf[2 * i] = (byte)(s & 0xFF);
                _outBuf[2 * i + 1] = (byte)((s >> 8) & 0xFF);
            }
            Level = outCount > 0 ? Mathf.Sqrt(sum / outCount) : 0f;
            return outCount * 2;
        }

        void OnEvent(PetEvent e)
        {
            if (e == null || !_awaitingFinal) return;
            if (e.type == "stt.partial") PartialTranscript?.Invoke(e.GetString("text"));
            else if (e.type == "stt.final")
            {
                _awaitingFinal = false;
                FinalTranscript?.Invoke(e.GetString("text"));
            }
        }

        void OnDisable()
        {
            if (IsListening) StopListening();
            StopMonitoring();
        }

        void OnDestroy()
        {
            if (client != null && _subscribed) client.EventReceived -= OnEvent;
            _subscribed = false;
        }
    }
}
