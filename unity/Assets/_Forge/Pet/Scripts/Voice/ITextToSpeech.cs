using System;

namespace Forge.Pet
{
    /// <summary>Text-to-speech hook (see docs/PET_AGENT_CONTRACT.md "Text-to-speech hook").</summary>
    public interface ITextToSpeech
    {
        void Speak(string text);
        void Stop();
        event Action SpeechStarted;
        event Action SpeechEnded;
    }
}
