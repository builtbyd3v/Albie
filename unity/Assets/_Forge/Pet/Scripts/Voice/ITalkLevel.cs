namespace Forge.Pet
{
    /// <summary>Loudness of whatever the gator is saying right now (drives GatorTalkAnimator).</summary>
    public interface ITalkLevel
    {
        /// <summary>Smoothed loudness 0..1 (0 when silent).</summary>
        float Level { get; }
        bool IsTalking { get; }
    }
}
