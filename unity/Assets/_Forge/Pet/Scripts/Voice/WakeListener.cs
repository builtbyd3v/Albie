using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Say a gator's name and it listens (docs/PET_AGENT_CONTRACT.md, Addendum E). The mic stays on; a small
    /// energy VAD (adaptive noise floor) cuts the room's speech into segments, and each segment goes to the hub as a
    /// wake turn: stt.start {wake: true} + 0.5 s of pre-roll (so the name itself isn't clipped) + PCM16 + stt.stop.
    /// The hub (agent/wake.py) only answers segments that name a gator ("Hey Bit ...", "Tester, run the tests");
    /// everything else is dropped without a caption. "Hey &lt;name&gt;" alone comes back as wake.listen:
    ///  * Bit  → PushToTalk.BeginHandsFree (a normal hands-free turn, then GatorFollowUp keeps the conversation going);
    ///  * crew → that gator turns to Listening and the next segment is sent for it (no name needed) for followSeconds.
    /// Pauses (and releases the mic) while push-to-talk / hands-free owns it (HubSpeechToText.MicNeeded), while Bit is
    /// speaking (so he can't wake his own crew by saying their names) and while the hub is offline.
    /// Hub mode only. No allocations per frame: buffers are allocated once per mic start.
    /// </summary>
    [DisallowMultipleComponent]
    public class WakeListener : MonoBehaviour
    {
        const int Rate = 16000;
        const float Chunk = 0.1f;                       // seconds per analysed / sent chunk
        const int ChunkOut = (int)(Rate * Chunk);       // 1600 samples

        [SerializeField, Tooltip("Listen for gator names all the time (hub mode).")]
        bool listenForNames = true;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        GatorAgentBridge bridge;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        PushToTalk pushToTalk;

        [Header("Voice activity detection")]
        [SerializeField, Tooltip("Speech must be at least this loud (mic RMS 0..1), whatever the room noise.")]
        float minLevel = 0.02f;
        [SerializeField, Tooltip("...and this many times the running noise floor.")]
        float noiseRatio = 3f;
        [SerializeField, Tooltip("Loud this long (s) before a segment opens.")]
        float startSeconds = 0.2f;
        [SerializeField, Tooltip("Quiet this long (s) closes the segment.")]
        float endSilence = 0.7f;
        [SerializeField, Tooltip("Audio kept from before the segment opened (s), so the first word survives.")]
        float prerollSeconds = 0.5f;
        [SerializeField, Tooltip("Segments with less speech than this (s) are cancelled (coughs, door slams).")]
        float minSpeech = 0.35f;
        [SerializeField, Tooltip("Longest segment (s).")]
        float maxSegment = 12f;

        [Header("After \"Hey <name>\"")]
        [SerializeField, Tooltip("How long (s) a named gator waits for the request.")]
        float followSeconds = 8f;
        [SerializeField, Tooltip("Wait this long (s) after Bit stops talking / a push-to-talk turn before listening again.")]
        float resumeDelay = 0.6f;

        /// <summary>Mic RMS of the last chunk (for debugging / UI).</summary>
        public float Level { get; private set; }
        public bool InSegment => _inSegment;
        public bool Listening => _clip != null;

        AgentClient _client;
        ITextToSpeech _tts;
        bool _ttsTalking, _subscribed;

        AudioClip _clip;
        int _clipSamples, _readPos, _chunkIn, _channels;
        float[] _inBuf;
        byte[][] _ring;                                 // pre-roll chunks (PCM16)
        byte[] _out;
        int _ringHead, _ringCount;

        bool _inSegment;
        float _noise = 0.01f, _pausedUntil;
        int _loudChunks, _quietChunks, _speechChunks, _segChunks;
        string _target;                                 // crew pet_id after "Hey <crew name>"
        float _targetUntil;

        void Awake()
        {
            if (bridge == null) bridge = GetComponent<GatorAgentBridge>();
            if (bridge == null) bridge = FindAnyObjectByType<GatorAgentBridge>();
            if (pushToTalk == null) pushToTalk = GetComponent<PushToTalk>();
            if (pushToTalk == null) pushToTalk = FindAnyObjectByType<PushToTalk>();
        }

        void Start()
        {
            _tts = GetComponentInChildren<ITextToSpeech>(true);
            if (_tts != null) { _tts.SpeechStarted += OnSpeechStarted; _tts.SpeechEnded += OnSpeechEnded; }
            HubSpeechToText.MicNeeded += ReleaseMic;
        }

        void OnDisable()
        {
            AbortSegment();
            StopMic();
        }

        void OnDestroy()
        {
            HubSpeechToText.MicNeeded -= ReleaseMic;
            if (_tts != null) { _tts.SpeechStarted -= OnSpeechStarted; _tts.SpeechEnded -= OnSpeechEnded; }
            if (_client != null && _subscribed) _client.EventReceived -= OnEvent;
        }

        void OnSpeechStarted() { _ttsTalking = true; ReleaseMic(); }
        void OnSpeechEnded() { _ttsTalking = false; _pausedUntil = Time.time + resumeDelay; }

        // Someone else needs the mic (push-to-talk / hands-free / follow-up): let go right now, resume later.
        void ReleaseMic()
        {
            AbortSegment();
            StopMic();
            _pausedUntil = Time.time + resumeDelay;
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            ResolveClient();
            if (_target != null && Time.time >= _targetUntil) DropTarget();

            // Push-to-talk / hands-free, or GatorFollowUp watching the mic (barge-in, look-to-talk): their mic, not ours.
            bool sessionBusy = (pushToTalk != null && pushToTalk.SessionActive)
                               || (pushToTalk != null && pushToTalk.Provider is HubSpeechToText hub && (hub.IsMonitoring || hub.IsListening));
            bool want = listenForNames && GatorAgentBridge.EventSourceIsHub && _client != null && _client.IsConnected
                        && !sessionBusy && !_ttsTalking && Time.time >= _pausedUntil && MicPermission.HasPermission;
            if (sessionBusy || _ttsTalking) _pausedUntil = Time.time + resumeDelay;
            if (!want)
            {
                if (_clip != null) { AbortSegment(); StopMic(); }
                return;
            }
            if (_clip == null && !StartMic()) { _pausedUntil = Time.time + 2f; return; }
            Pump();
        }

        void ResolveClient()
        {
            if (_subscribed) return;
            _client = bridge != null ? bridge.Source as AgentClient : null;
            if (_client == null) return;
            _client.EventReceived += OnEvent;
            _subscribed = true;
        }

        void Pump()
        {
            int pos = Microphone.GetPosition(null);
            if (pos < 0) return;
            int available = (pos - _readPos + _clipSamples) % _clipSamples;
            while (available >= _chunkIn && _clip != null)
            {
                _clip.GetData(_inBuf, _readPos);
                _readPos = (_readPos + _chunkIn) % _clipSamples;
                available -= _chunkIn;
                byte[] pcm = _inSegment ? _out : NextRingSlot();
                Level = Convert(pcm);
                OnChunk(pcm);
            }
        }

        void OnChunk(byte[] pcm)
        {
            float threshold = Mathf.Max(minLevel, _noise * noiseRatio);
            bool loud = Level >= threshold;
            if (!_inSegment)
            {
                if (!loud) _noise = Mathf.Lerp(_noise, Level, 0.05f);   // track the room only while nobody talks
                _loudChunks = loud ? _loudChunks + 1 : 0;
                if (_loudChunks * Chunk >= startSeconds) BeginSegment();
                return;
            }
            _client.SendAudio(pcm, pcm.Length);
            _segChunks++;
            if (loud) { _speechChunks++; _quietChunks = 0; }
            else _quietChunks++;
            if (_quietChunks * Chunk >= endSilence || _segChunks * Chunk >= maxSegment) EndSegment();
        }

        // ---------------------------------------------------------------- segments

        void BeginSegment()
        {
            var start = new JObject { ["pet_id"] = _target ?? LeadPetId, ["wake"] = true, ["sample_rate"] = Rate, ["encoding"] = "pcm16" };
            _client.Send("stt.start", start);
            // Pre-roll, oldest first (it includes the chunks that crossed the threshold).
            int first = (_ringHead - _ringCount + _ring.Length) % _ring.Length;
            for (int i = 0; i < _ringCount; i++) _client.SendAudio(_ring[(first + i) % _ring.Length], _out.Length);
            _ringCount = 0;
            _inSegment = true;
            _speechChunks = _loudChunks;
            _quietChunks = 0;
            _segChunks = 0;
            _loudChunks = 0;
        }

        void EndSegment()
        {
            if (!_inSegment) return;
            _inSegment = false;
            bool enough = _speechChunks * Chunk >= minSpeech;
            if (!enough) _client.Send("stt.cancel", new JObject { ["pet_id"] = LeadPetId });
            _client.Send("stt.stop", new JObject { ["pet_id"] = LeadPetId });
            if (enough && _target != null) { _target = null; }   // that was the request for the named crew gator
        }

        void AbortSegment()
        {
            if (!_inSegment) return;
            _inSegment = false;
            if (_client != null && _client.IsConnected)
            {
                _client.Send("stt.cancel", new JObject { ["pet_id"] = LeadPetId });
                _client.Send("stt.stop", new JObject { ["pet_id"] = LeadPetId });
            }
        }

        // ---------------------------------------------------------------- hub → wake.listen

        void OnEvent(PetEvent e)
        {
            if (e == null || e.type != "wake.listen") return;
            string pid = e.GetString("pet_id");
            if (string.IsNullOrEmpty(pid) || pid == LeadPetId)
            {
                if (pushToTalk != null) pushToTalk.BeginHandsFree(followSeconds);   // MicNeeded releases our mic
                return;
            }
            GatorCrewMember m = CrewMember(pid);
            if (m == null) return;
            _target = pid;
            _targetUntil = Time.time + followSeconds;
            if (m.Controller != null) m.Controller.SetState(GatorState.Listening);
            GatorCaption c = m.GetComponentInChildren<GatorCaption>(true);
            if (c != null) c.SetText("Yes?", false);
        }

        void DropTarget()
        {
            GatorCrewMember m = CrewMember(_target);
            _target = null;
            if (m != null && m.Controller != null && m.Controller.State == GatorState.Listening)
                m.Controller.SetState(GatorState.Wandering);
        }

        static GatorCrewMember CrewMember(string pid)
        {
            GatorCrew crew = GatorCrew.Instance;
            return crew != null && pid != null ? crew.Find(GatorAgentTypes.Find(pid)) : null;
        }

        string LeadPetId => bridge != null ? bridge.PetId : "bit";

        // ---------------------------------------------------------------- mic

        bool StartMic()
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0) return false;
            if (Microphone.IsRecording(null)) return false;            // someone else has it
            Microphone.GetDeviceCaps(null, out int minF, out int maxF);
            int rate = (minF == 0 && maxF == 0) ? Rate : Mathf.Clamp(Rate, minF, maxF);
            _clip = Microphone.Start(null, true, 2, rate);
            if (_clip == null) return false;
            _clipSamples = _clip.samples;
            _channels = _clip.channels;
            _chunkIn = Mathf.Max(1, Mathf.RoundToInt(_clip.frequency * Chunk));
            if (_inBuf == null || _inBuf.Length != _chunkIn * _channels) _inBuf = new float[_chunkIn * _channels];
            int slots = Mathf.Max(1, Mathf.CeilToInt(prerollSeconds / Chunk));
            if (_ring == null || _ring.Length != slots)
            {
                _ring = new byte[slots][];
                for (int i = 0; i < slots; i++) _ring[i] = new byte[ChunkOut * 2];
            }
            if (_out == null) _out = new byte[ChunkOut * 2];
            _readPos = Microphone.GetPosition(null);
            _ringHead = _ringCount = 0;
            _loudChunks = 0;
            return true;
        }

        void StopMic()
        {
            if (_clip == null) return;
            Microphone.End(null);
            _clip = null;
            Level = 0f;
        }

        byte[] NextRingSlot()
        {
            byte[] slot = _ring[_ringHead];
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            return slot;
        }

        // Linear-resample _inBuf (device rate, first channel) to 16 kHz PCM16 LE; returns the chunk's RMS.
        float Convert(byte[] dst)
        {
            float step = ChunkOut > 1 ? (_chunkIn - 1f) / (ChunkOut - 1f) : 0f;
            float sum = 0f;
            for (int i = 0; i < ChunkOut; i++)
            {
                float src = i * step;
                int i0 = (int)src;
                int i1 = i0 + 1 < _chunkIn ? i0 + 1 : i0;
                float t = src - i0;
                float f = _inBuf[i0 * _channels] * (1f - t) + _inBuf[i1 * _channels] * t;
                sum += f * f;
                int s = (int)(f * 32767f);
                if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                dst[2 * i] = (byte)(s & 0xFF);
                dst[2 * i + 1] = (byte)((s >> 8) & 0xFF);
            }
            return Mathf.Sqrt(sum / ChunkOut);
        }
    }
}
