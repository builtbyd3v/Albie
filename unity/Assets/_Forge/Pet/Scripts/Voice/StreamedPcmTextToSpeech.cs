using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Plays the hub's streamed speech (voice mode: tts.start → binary PCM16 mono frames → tts.end / tts.stop)
    /// through OnAudioFilterRead. Frames are linearly resampled to AudioSettings.outputSampleRate on the main thread
    /// (AgentClient.BinaryReceived) into a preallocated single-producer / single-consumer float ring; the audio
    /// thread drains it, outputting silence on underrun. Playback starts after prebufferSeconds of audio (or at
    /// tts.end for very short replies) and SpeechEnded fires once the ring has drained after tts.end.
    ///
    /// Cartoon pitch: playback runs pitch× faster than the stream (resampling raises pitch and formants together, the
    /// classic cartoon-voice trick). Grok has no pitch control, so set GROK_VOICE_SPEED ≈ 1/pitch on the hub to keep
    /// the tempo natural; the pre-buffer absorbs the faster drain.
    ///
    /// Spatial trick: the AudioSource loops a constant 1.0 "carrier" clip and the filter multiplies it by our
    /// samples, so Unity's 3D panning/attenuation applies whether it runs before or after the filter.
    ///
    /// Speak(text) is a no-op while the hub has a voice session (AgentClient.VoiceActive: the hub alone decides what
    /// is spoken); otherwise it fakes a speaking period like NullTextToSpeech so the talk animation still runs.
    /// Needs its own GameObject (an AudioSource just for speech), e.g. a "Voice" child of the gator.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public class StreamedPcmTextToSpeech : MonoBehaviour, ITextToSpeech, ITalkLevel
    {
        const float MinRingSeconds = 120f;   // ~23 MB of floats at 48 kHz

        [SerializeField, Tooltip("Hub socket. Auto-found in parents / scene if empty (the bridge adds it at runtime).")]
        AgentClient client;
        [SerializeField, Tooltip("Ring buffer length (s). The hub can stream faster than real time, so keep a whole reply.")]
        float ringSeconds = 120f;
        [SerializeField, Tooltip("Audio buffered before playback starts (s): absorbs network jitter and the pitch-up drain.")]
        float prebufferSeconds = 0.2f;
        [SerializeField, Range(0.75f, 1.6f), Tooltip("Cartoon pitch: >1 = higher (and faster). Pair with GROK_VOICE_SPEED ≈ 1/pitch.")]
        float pitch = 1.2f;
        [SerializeField, Range(0f, 1f), Tooltip("0 = 2D, 1 = fully 3D from this object.")]
        float spatialBlend = 1f;
        [SerializeField, Range(0f, 2f)]
        float volume = 1f;
        [SerializeField, Tooltip("RMS → Level multiplier (speech RMS is ~0.05-0.25).")]
        float levelGain = 4f;
        [SerializeField, Tooltip("Level envelope attack / release (s).")]
        float levelAttack = 0.03f, levelRelease = 0.12f;
        [SerializeField, Tooltip("Without a hub voice session: fake speaking time per character (s).")]
        float fallbackSecondsPerChar = 0.05f;

        public event Action SpeechStarted;
        public event Action SpeechEnded;

        /// <summary>Smoothed loudness 0..1 of what is playing (synthetic envelope during fallback speech).</summary>
        public float Level { get; private set; }
        public bool IsTalking => _speaking;
        /// <summary>The hub speaks for the gator (voice.ready received on the current connection).</summary>
        public bool VoiceActive => client != null && client.VoiceActive;

        AudioSource _source;
        GatorAgentBridge _bridge;
        int _outRate, _prebuffer;
        float _nextResolve;
        bool _subscribed;

        // ring (producer = main thread, consumer = audio thread). Counters only grow; index = counter % _cap.
        float[] _ring;
        int _cap;
        long _write, _read, _flushTo = -1;
        volatile bool _gate;          // audio thread may consume
        volatile float _rms;          // RMS of the last audio block
        volatile float _volume = 1f;
        bool _warnedOverflow;

        // producer-side resampler
        int _inRate;
        bool _pitched;
        double _step, _t;
        float _prev;
        bool _havePrev;

        // main-thread state
        bool _streaming, _ended, _speaking, _fake;
        float _fakeUntil, _fakeStart;

        void Awake()
        {
            _outRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            // Grok sends a whole answer in a burst (a 40 s answer in ~4 s), so the ring must hold the longest answer.
            // Floor of MinRingSeconds also covers scenes that serialized the old 30 s default.
            _cap = Mathf.Max(_outRate, Mathf.RoundToInt(_outRate * Mathf.Max(MinRingSeconds, ringSeconds)));
            _ring = new float[_cap];
            _prebuffer = Mathf.Max(1, Mathf.RoundToInt(_outRate * Mathf.Max(0f, prebufferSeconds)));

            _source = GetComponent<AudioSource>();
            var carrier = AudioClip.Create("VoiceCarrier", _outRate, 1, _outRate, false);
            var ones = new float[_outRate];
            for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
            carrier.SetData(ones, 0);
            _source.clip = carrier;
            _source.loop = true;
            _source.playOnAwake = false;
            _source.spatialBlend = spatialBlend;
            _source.dopplerLevel = 0f;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = 1f;
            _source.maxDistance = 12f;

            if (_bridge == null) _bridge = GetComponentInParent<GatorAgentBridge>();
            if (_bridge == null) _bridge = FindAnyObjectByType<GatorAgentBridge>();
        }

        void OnEnable()
        {
            if (_source != null && !_source.isPlaying) _source.Play();
        }

        void OnDisable()
        {
            Flush();
            EndFake();
            if (_source != null) _source.Stop();
        }

        void OnDestroy()
        {
            if (client != null && _subscribed)
            {
                client.EventReceived -= OnEvent;
                client.BinaryReceived -= OnBinary;
            }
            _subscribed = false;
        }

        // ---------------------------------------------------------------- ITextToSpeech

        public void Speak(string text)
        {
            if (VoiceActive || string.IsNullOrEmpty(text)) return;   // the hub streams the real speech
            EndFake();
            _fake = true;
            _fakeStart = Time.time;
            _fakeUntil = Time.time + text.Length * fallbackSecondsPerChar;
            BeginSpeaking();
        }

        public void Stop()
        {
            bool wasStreaming = _streaming || _gate;
            Flush();
            EndFake();
            if (wasStreaming && VoiceActive && client != null)
                client.Send("tts.stop", new JObject { ["pet_id"] = PetId });
        }

        // ---------------------------------------------------------------- hub events (main thread)

        void OnEvent(PetEvent e)
        {
            if (e == null) return;
            switch (e.type)
            {
                case "tts.start":
                    EndFake();
                    SetInputRate(e.GetInt("sample_rate", _outRate));
                    _streaming = true;
                    _ended = false;
                    break;
                case "tts.end":
                    if (_streaming) _ended = true;
                    break;
                case "tts.stop":
                    Flush();
                    break;
            }
        }

        void OnBinary(byte[] data, int length)
        {
            if (!_streaming || _ended || data == null || length < 2) return;   // stale frames after a stop
            long w = _write;
            long r = Interlocked.Read(ref _read);
            int n = length / 2;
            for (int i = 0; i < n; i++)
            {
                float x = (short)(data[2 * i] | (data[2 * i + 1] << 8)) / 32768f;
                if (_inRate == _outRate && !_pitched) { Put(ref w, r, x); continue; }
                if (!_havePrev) { _prev = x; _havePrev = true; continue; }
                while (_t <= 1.0)
                {
                    Put(ref w, r, _prev + (x - _prev) * (float)_t);
                    _t += _step;
                }
                _t -= 1.0;
                _prev = x;
            }
            Interlocked.Exchange(ref _write, w);
        }

        void Put(ref long w, long r, float x)
        {
            if (w - r >= _cap)
            {
                if (!_warnedOverflow) { _warnedOverflow = true; Debug.LogWarning("[StreamedPcmTextToSpeech] Ring full; dropping audio (raise ringSeconds).", this); }
                return;
            }
            _ring[(int)(w % _cap)] = x;
            w++;
        }

        void SetInputRate(int rate)
        {
            if (rate <= 0) rate = _outRate;
            _inRate = rate;
            _step = (double)rate * Mathf.Clamp(pitch, 0.5f, 2f) / _outRate;   // input samples per output sample
            _pitched = !Mathf.Approximately(pitch, 1f);
            _t = 0.0;
            _havePrev = false;
        }

        void Flush()
        {
            Interlocked.Exchange(ref _flushTo, Interlocked.Read(ref _write));
            _gate = false;
            _streaming = false;
            _ended = false;
            if (!_fake) EndSpeaking();
        }

        // ---------------------------------------------------------------- main loop

        void Update()
        {
            ResolveClient();
            _volume = volume;

            long avail = Interlocked.Read(ref _write) - Interlocked.Read(ref _read);
            if (_streaming)
            {
                if (!_gate && (avail >= _prebuffer || (_ended && avail > 0)))
                {
                    _gate = true;
                    BeginSpeaking();
                }
                else if (_ended && avail <= 0)
                {
                    _gate = false;
                    _streaming = false;
                    _ended = false;
                    EndSpeaking();
                }
            }

            if (_fake && Time.time >= _fakeUntil) EndFake();

            float target;
            if (_fake) target = FakeEnvelope(Time.time - _fakeStart);
            else target = _gate ? Mathf.Clamp01(_rms * levelGain) : 0f;
            float tau = Mathf.Max(0.001f, target > Level ? levelAttack : levelRelease);
            Level = Mathf.Lerp(Level, target, 1f - Mathf.Exp(-Time.deltaTime / tau));
        }

        // Audio thread: drain the ring, multiply into the carrier.
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (_ring == null || channels <= 0) return;
            long r = Interlocked.Read(ref _read);
            long f = Interlocked.Exchange(ref _flushTo, -1);
            if (f > r) r = f;
            long w = Interlocked.Read(ref _write);
            bool gate = _gate;
            float vol = _volume;
            double sum = 0.0;
            int frames = data.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                float x = 0f;
                if (gate && r < w) { x = _ring[(int)(r % _cap)]; r++; }
                sum += x * x;
                x *= vol;
                int o = i * channels;
                for (int c = 0; c < channels; c++) data[o + c] *= x;
            }
            Interlocked.Exchange(ref _read, r);
            _rms = frames > 0 ? (float)Math.Sqrt(sum / frames) : 0f;
        }

        // ---------------------------------------------------------------- helpers

        void BeginSpeaking()
        {
            if (_speaking) return;
            _speaking = true;
            SpeechStarted?.Invoke();
        }

        void EndSpeaking()
        {
            if (!_speaking) return;
            _speaking = false;
            SpeechEnded?.Invoke();
        }

        void EndFake()
        {
            if (!_fake) return;
            _fake = false;
            EndSpeaking();
        }

        // ~3 syllables per second with a slow phrase swell; 0..1.
        static float FakeEnvelope(float t)
        {
            float syll = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3.1f));
            float phrase = 0.65f + 0.35f * Mathf.Sin(t * Mathf.PI * 0.7f);
            return Mathf.Clamp01(syll * phrase);
        }

        string PetId => _bridge != null && _bridge.Config != null ? _bridge.Config.petId : "bit";

        // The bridge adds AgentClient when the config loads, so keep looking until it exists (throttled).
        void ResolveClient()
        {
            if (_subscribed) return;
            if (Time.unscaledTime < _nextResolve) return;
            if (client == null && _bridge != null && _bridge.Config != null && !_bridge.Config.IsHub) return;   // mock mode: no hub
            _nextResolve = Time.unscaledTime + 0.5f;
            if (client == null) client = GetComponentInParent<AgentClient>();
            if (client == null) client = FindAnyObjectByType<AgentClient>();
            if (client == null) return;
            client.EventReceived += OnEvent;
            client.BinaryReceived += OnBinary;
            _subscribed = true;
        }
    }
}
