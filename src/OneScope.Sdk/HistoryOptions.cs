namespace OneScope.Sdk;

/// <summary>
/// The 0.1 name of <see cref="ScopeHistoryOptions"/>, kept so modules built against 0.1 still load. It collides with a
/// library type of the same name in some modules, so new code uses <see cref="ScopeHistoryOptions"/>.
/// </summary>
[Obsolete("Use ScopeHistoryOptions. This name is kept for modules built against SDK 0.1 and will be removed before 1.0.")]
public sealed class HistoryOptions : ScopeHistoryOptions
{
    /// <summary>The default keyframe interval in steps; the same value as <see cref="ScopeHistoryOptions.DefaultKeyframeInterval"/>.</summary>
    public new const int DefaultKeyframeInterval = ScopeHistoryOptions.DefaultKeyframeInterval;
}
