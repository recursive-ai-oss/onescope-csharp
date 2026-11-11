namespace OneScope.Sdk;

/// <summary>Limits and spacing for a <see cref="History"/>.</summary>
public class ScopeHistoryOptions
{
    /// <summary>The default keyframe interval in steps.</summary>
    public const int DefaultKeyframeInterval = 64;

    /// <summary>
    /// A snapshot is kept every this many steps (steps 0, N, 2N, ...). Smaller means faster seeks and more memory.
    /// A seek costs at most one restore plus N - 1 steps when the target lies in a stretch the history has already played.
    /// Must be at least 1.
    /// </summary>
    public int KeyframeInterval { get; init; } = DefaultKeyframeInterval;

    /// <summary>
    /// The most keyframes held. When a new one would exceed this, the keyframe whose removal leaves the smallest gap is
    /// dropped (step 0 and the newest are never dropped), so memory is bounded and seeks stay correct, only slower. At least 2.
    /// </summary>
    public int MaxKeyframes { get; init; } = 1024;

    /// <summary>The most samples one track holds; later samples are dropped and <see cref="TrackData.Truncated"/> is set. At least 1.</summary>
    public int MaxTrackSamples { get; init; } = 1 << 20;

    /// <summary>The most findings held; later ones are dropped. At least 1.</summary>
    public int MaxFindings { get; init; } = 1 << 16;
}
