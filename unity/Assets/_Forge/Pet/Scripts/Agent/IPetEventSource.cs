using System;

namespace Forge.Pet
{
    /// <summary>Where pet events come from: live hub socket (AgentClient) or scripted mock (MockEventSource).
    /// EventReceived is always raised on the Unity main thread.</summary>
    public interface IPetEventSource
    {
        event Action<PetEvent> EventReceived;
        bool IsConnected { get; }
        void Connect(PetConfig cfg);
        void Disconnect();
        void Send(string type, object payload);
    }
}
