namespace Forge.Pet
{
    /// <summary>Optional on an ISpeechToText: input loudness 0..1 while listening (PushToTalk hands-free endpointing).</summary>
    public interface IMicLevel
    {
        float Level { get; }
    }
}
