using System;
using System.Collections;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Silent TTS stub: raises SpeechStarted, waits text.Length * secondsPerChar, raises SpeechEnded.
    /// Lets the Answering animation / lipsync hook be tested without an audio backend.
    /// </summary>
    public class NullTextToSpeech : MonoBehaviour, ITextToSpeech
    {
        [SerializeField, Tooltip("Fake speaking time per character, in seconds.")]
        float secondsPerChar = 0.05f;

        public event Action SpeechStarted;
        public event Action SpeechEnded;

        public bool IsSpeaking { get; private set; }
        Coroutine _routine;

        public void Speak(string text)
        {
            Stop();
            if (string.IsNullOrEmpty(text)) return;
            _routine = StartCoroutine(SpeakRoutine(text.Length * secondsPerChar));
        }

        public void Stop()
        {
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
            if (IsSpeaking) { IsSpeaking = false; SpeechEnded?.Invoke(); }
        }

        IEnumerator SpeakRoutine(float seconds)
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke();
            yield return new WaitForSeconds(seconds);
            IsSpeaking = false;
            _routine = null;
            SpeechEnded?.Invoke();
        }

        void OnDisable() => Stop();
    }
}
