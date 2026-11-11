using System.Buffers;
using OneScope.Sdk;

namespace OneScope.TestModule;

/// <summary>
/// A small deterministic integer simulation used to exercise every part of the SDK and the shell: bodies bouncing in a box,
/// with its own replay format, pokes, overlays, tracks, findings, a length unit in millimetres and a second axis (the stages of a step).
/// </summary>
public sealed class TestModule : IScopeModule
{
    /// <summary>The id of the stage axis.</summary>
    public const string StageAxis = "stage";

    /// <summary>Parts of one step along the stage axis: apply input, gravity, move, bookkeeping.</summary>
    public const int StageCount = 4;

    /// <summary>First entity id used by bodies; body i is <c>FirstBodyId + i</c>.</summary>
    public const ulong FirstBodyId = 100;

    /// <summary>The root entity.</summary>
    public static readonly EntityId World = new(1);

    /// <summary>The static floor entity.</summary>
    public static readonly EntityId Floor = new(2);

    /// <summary>The sensor zone entity.</summary>
    public static readonly EntityId Zone = new(3);

    /// <inheritdoc />
    public string Id => TestFormat.ModuleId;

    /// <inheritdoc />
    public string Name => "Test module";

    /// <inheritdoc />
    public IScopeSource Open(ReadOnlyMemory<byte> data, string name)
    {
        TestFormat.Parsed p = TestFormat.Parse(data, name);
        return new TestSource(name, p.Setup, Recording.Create(p.Setup, p.Inputs), p.Hashes);
    }

    /// <summary>Makes a source from parts, for tests and generators.</summary>
    public static TestSource CreateSource(string name, byte[] setup, Recording recording, ulong[]? hashes = null) =>
        new(name, setup, recording, hashes);

    internal static SourceInfo MakeInfo(string name, int dimensions) => new()
    {
        ModuleId = TestFormat.ModuleId,
        Name = name,
        Dimensions = dimensions,
        Unit = LengthUnit.Millimetre,
        Axes =
        [
            new AxisInfo(AxisIds.Time, "Time", 0),
            new AxisInfo(StageAxis, "Stage", StageCount),
        ],
        EntityKinds =
        [
            new EntityKindInfo("world", "World"),
            new EntityKindInfo("body", "Body"),
            new EntityKindInfo("static", "Static"),
            new EntityKindInfo("sensor", "Sensor"),
        ],
        Overlays =
        [
            new OverlayInfo(0, "Bodies", true),
            new OverlayInfo(1, "Velocities", false),
            new OverlayInfo(2, "Bounds", false),
            new OverlayInfo(3, "Labels", false),
        ],
        Pokes =
        [
            new PokeInfo("impulse", "Impulse", [new ParamInfo("target", ValueKind.Entity), new ParamInfo("delta", ValueKind.Vector)]),
            new PokeInfo("teleport", "Teleport", [new ParamInfo("target", ValueKind.Entity), new ParamInfo("position", ValueKind.Vector)]) { Role = PokeRole.DragToPoint },
            new PokeInfo("remove", "Remove", [new ParamInfo("target", ValueKind.Entity)]),
            new PokeInfo("set_gravity", "Set gravity", [new ParamInfo("gravity", ValueKind.Integer)]),
        ],
        Tracks =
        [
            new TrackInfo("bounce", "Bounces", TrackKind.Event),
            new TrackInfo("phase", "Phase", TrackKind.Marker),
            new TrackInfo("energy", "Energy", TrackKind.Series),
            new TrackInfo("awake", "Awake bodies", TrackKind.Series),
        ],
    };
}

/// <summary>A test-module recording.</summary>
public sealed class TestSource : IScopeSource
{
    private readonly byte[] _setup;

    internal TestSource(string name, byte[] setup, Recording recording, ulong[]? hashes)
    {
        _setup = setup;
        Recording = recording;
        ExpectedHashes = hashes;
        Info = TestModule.MakeInfo(name, setup[0]);
    }

    /// <inheritdoc />
    public SourceInfo Info { get; }

    /// <inheritdoc />
    public Recording Recording { get; }

    /// <summary>The per-step world hashes stored in the file (row i is step i), or null when the file had none.</summary>
    public ulong[]? ExpectedHashes { get; }

    /// <inheritdoc />
    public IScopeWorld CreateWorld() => new TestWorld(_setup);

    /// <inheritdoc />
    public IScopeSource WithRecording(Recording recording) => new TestSource(Info.Name, _setup, recording, null);

    /// <inheritdoc />
    public void WriteReplay(IBufferWriter<byte> writer) => TestFormat.Write(writer, _setup, Recording, ExpectedHashes);
}
