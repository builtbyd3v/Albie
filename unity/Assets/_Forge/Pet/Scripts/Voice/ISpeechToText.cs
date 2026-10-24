using System;

namespace Forge.Pet
{
    /// <summary>Speech-to-text provider (see docs/PET_AGENT_CONTRACT.md "Speech-to-text").</summary>
    public interface ISpeechToText
    {
        bool IsListening { get; }
        event Action<string> PartialTranscript;
        event Action<string> FinalTranscript;
        event Action<string> Error;
        void StartListening();
        void StopListening();     // emits FinalTranscript
    }
}
