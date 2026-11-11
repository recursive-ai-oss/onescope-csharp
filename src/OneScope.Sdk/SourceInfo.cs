namespace OneScope.Sdk;

/// <summary>The id of the time axis. Every source has it; it is stepped one simulation step at a time.</summary>
public static class AxisIds
{
    /// <summary>"time": the simulation step index.</summary>
    public const string Time = "time";
}

/// <summary>An axis a source can be stepped along.</summary>
/// <param name="Id">Stable id, unique within the source. The time axis uses <see cref="AxisIds.Time"/>.</param>
/// <param name="Name">Display name.</param>
/// <param name="PositionCount">
/// For the time axis: zero (its length is the recording's step count).
/// For any other axis: the number of positions inside one step, so valid positions are 0 to <paramref name="PositionCount"/>.
/// Position 0 is the world as it is at the step; position <paramref name="PositionCount"/> is the step's work all done.
/// </param>
public readonly record struct AxisInfo(string Id, string Name, int PositionCount);

/// <summary>A kind of entity a source holds, such as "body" or "agent".</summary>
/// <param name="Id">Stable id used in <see cref="EntityInfo.Kind"/>.</param>
/// <param name="Name">Display name.</param>
public readonly record struct EntityKindInfo(string Id, string Name);

/// <summary>A named toggle that shows or hides a group of draw items.</summary>
/// <param name="Id">The overlay number, 0 to 63, unique within the source; draw items carry it in <see cref="DrawStyle.Overlay"/>.</param>
/// <param name="Name">Display name.</param>
/// <param name="DefaultOn">Whether a new view starts with it on.</param>
public readonly record struct OverlayInfo(int Id, string Name, bool DefaultOn);

/// <summary>One parameter of a poke.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Kind">The kind of <see cref="ScopeValue"/> expected.</param>
public readonly record struct ParamInfo(string Name, ValueKind Kind);

/// <summary>An operation a user or agent may apply to a fork.</summary>
/// <param name="Id">Stable id, unique within the source.</param>
/// <param name="Name">Display name.</param>
/// <param name="Params">The parameters, in call order.</param>
public readonly record struct PokeInfo(string Id, string Name, ParamInfo[] Params);

/// <summary>How a timeline track is drawn and what its samples mean.</summary>
public enum TrackKind : byte
{
    /// <summary>Things that happen at a step (contact begin, joint break). Each sample may name an entity and carry text.</summary>
    Event,
    /// <summary>A labelled position on the timeline (a phase change). Samples carry text.</summary>
    Marker,
    /// <summary>A number per step (energy, constraint error). Samples carry a value.</summary>
    Series,
}

/// <summary>A timeline track a source declares.</summary>
/// <param name="Id">Stable id, unique within the source.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">What its samples mean.</param>
public readonly record struct TrackInfo(string Id, string Name, TrackKind Kind);

/// <summary>Everything a source declares about itself as data.</summary>
public sealed class SourceInfo
{
    /// <summary>The id of the module that opened this source.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Display name, such as a file name.</summary>
    public required string Name { get; init; }

    /// <summary>2 or 3. 2D sources keep Z at zero in every position they report.</summary>
    public required int Dimensions { get; init; }

    /// <summary>The unit all lengths are in.</summary>
    public required LengthUnit Unit { get; init; }

    /// <summary>The axes the source can be stepped along. Must include <see cref="AxisIds.Time"/>.</summary>
    public required IReadOnlyList<AxisInfo> Axes { get; init; }

    /// <summary>The kinds of entity the source holds.</summary>
    public IReadOnlyList<EntityKindInfo> EntityKinds { get; init; } = [];

    /// <summary>The overlays the source can draw.</summary>
    public IReadOnlyList<OverlayInfo> Overlays { get; init; } = [];

    /// <summary>The pokes the source accepts on a fork.</summary>
    public IReadOnlyList<PokeInfo> Pokes { get; init; } = [];

    /// <summary>The timeline tracks the source fills.</summary>
    public IReadOnlyList<TrackInfo> Tracks { get; init; } = [];
}
