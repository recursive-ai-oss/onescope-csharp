namespace OneScope.Sdk;

/// <summary>How serious a finding is.</summary>
public enum Severity : byte
{
    /// <summary>For information.</summary>
    Info,
    /// <summary>Probably unintended.</summary>
    Warning,
    /// <summary>Wrong.</summary>
    Error,
}

/// <summary>A module's observation about a step, shown as a marker on the timeline and an entry in the event log.</summary>
/// <param name="Step">The step the finding is at (the step reached when it was raised).</param>
/// <param name="Code">A stable short code, such as "TM001".</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Entities">The entities involved; may be empty.</param>
/// <param name="Text">What was found.</param>
/// <param name="SuggestedChange">A change that might fix it, or null.</param>
public sealed record Finding(long Step, string Code, Severity Severity, IReadOnlyList<EntityId> Entities, string Text, string? SuggestedChange);

/// <summary>One point on a track.</summary>
/// <param name="Step">The step reached when the sample was recorded.</param>
/// <param name="Value">The value for series; zero otherwise.</param>
/// <param name="Entity">The entity involved, or <see cref="EntityId.None"/>.</param>
/// <param name="Text">A label for events and markers; null for series.</param>
public readonly record struct TrackSample(long Step, double Value, EntityId Entity, string? Text);

/// <summary>All samples of one track recorded so far.</summary>
public sealed class TrackData
{
    private readonly List<TrackSample> _samples = [];
    private readonly int _limit;

    internal TrackData(TrackInfo info, int limit)
    {
        Info = info;
        _limit = limit;
    }

    /// <summary>The track's declaration.</summary>
    public TrackInfo Info { get; }

    /// <summary>The samples, ordered by step.</summary>
    public IReadOnlyList<TrackSample> Samples => _samples;

    /// <summary>True when samples were dropped because the track reached its limit.</summary>
    public bool Truncated { get; private set; }

    internal void Add(in TrackSample sample)
    {
        if (_samples.Count >= _limit)
        {
            Truncated = true;
            return;
        }

        _samples.Add(sample);
    }

    internal void CopyFrom(TrackData other, long throughStep)
    {
        foreach (TrackSample s in other._samples)
        {
            if (s.Step > throughStep)
            {
                break;
            }

            _samples.Add(s);
        }

        Truncated = other.Truncated;
    }
}

/// <summary>
/// Where a world reports what a step produced. The history gives <see cref="IScopeWorld.Advance"/> a sink; it records the first
/// time a step is run and ignores later re-runs, so modules call it unconditionally. Calls with an undeclared track id
/// throw <see cref="ArgumentException"/>.
/// </summary>
public sealed class TimelineSink
{
    private readonly Dictionary<string, TrackData>? _tracks;
    private readonly List<Finding>? _findings;
    private readonly int _findingLimit;
    private bool _recording;
    private long _step;

    private TimelineSink()
    {
    }

    internal TimelineSink(Dictionary<string, TrackData> tracks, List<Finding> findings, int findingLimit)
    {
        _tracks = tracks;
        _findings = findings;
        _findingLimit = findingLimit;
    }

    /// <summary>A sink that records nothing and accepts any track id, for running a world outside a <see cref="History"/>.</summary>
    public static TimelineSink None { get; } = new();

    /// <summary>True when the current step is being recorded for the first time. Modules may skip building text when false.</summary>
    public bool IsRecording => _recording;

    /// <summary>The step reached by the step being run.</summary>
    public long Step => _step;

    internal void Begin(long stepReached, bool recording)
    {
        _step = stepReached;
        _recording = recording;
    }

    /// <summary>Records an event on an event track.</summary>
    public void Event(string track, EntityId entity, string text) => Add(track, TrackKind.Event, 0, entity, text);

    /// <summary>Records a marker on a marker track.</summary>
    public void Marker(string track, string label) => Add(track, TrackKind.Marker, 0, EntityId.None, label);

    /// <summary>Records one value on a series track. At most one per step per track is expected.</summary>
    public void Sample(string track, double value) => Add(track, TrackKind.Series, value, EntityId.None, null);

    /// <summary>Raises a finding at the current step.</summary>
    public void Finding(string code, Severity severity, IReadOnlyList<EntityId> entities, string text, string? suggestedChange = null)
    {
        if (_recording && _findings is not null && _findings.Count < _findingLimit)
        {
            _findings.Add(new Finding(_step, code, severity, entities, text, suggestedChange));
        }
    }

    private void Add(string track, TrackKind kind, double value, EntityId entity, string? text)
    {
        if (_tracks is null)
        {
            return;
        }

        if (!_tracks.TryGetValue(track, out TrackData? data) || data.Info.Kind != kind)
        {
            throw new ArgumentException($"Track '{track}' is not declared as a {kind} track.", nameof(track));
        }

        if (_recording)
        {
            data.Add(new TrackSample(_step, value, entity, text));
        }
    }
}
