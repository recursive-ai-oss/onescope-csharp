using System.Buffers;
using OneScope.Sdk;

namespace OneScope.TestModule;

/// <summary>
/// A small deterministic integer simulation used to exercise every part of the SDK and the shell: bodies bouncing in a box,
/// with its own replay format, pokes, overlays, tracks, findings, a length unit in millimetres and a second axis (the stages of a step).
/// </summary>
public sealed class TestModule : ILiveModule
{
    /// <summary>The id of the stage axis.</summary>
    public const string StageAxis = "stage";

    /// <summary>Parts of one step along the stage axis: apply input, gravity, move, bookkeeping.</summary>
    public const int StageCount = 4;

    /// <summary>First entity id used by bodies; body i is <c>FirstBodyId + i</c>.</summary>
    public const ulong FirstBodyId = 100;

    /// <summary>Overlay that paints <see cref="DensityField"/> over a grid on the floor.</summary>
    public const int DensityOverlay = 4;

    /// <summary>Overlay that paints <see cref="OccupancyField"/> over the same grid, one value per triangle.</summary>
    public const int OccupancyOverlay = 5;

    /// <summary>The link key of the world that gives a body away in a linked pair.</summary>
    public const string LeftKey = "tm-left";

    /// <summary>The link key of the world that receives the body.</summary>
    public const string RightKey = "tm-right";

    /// <summary>How close the bodies are, smoothly, from 0 (none within 3 m) up. One value per grid vertex.</summary>
    public static readonly FieldInfo DensityField = new("density", "Body density", ColorMap.Viridis, 0, 2, "bodies");

    /// <summary>How many live bodies are in each grid cell. One value per triangle.</summary>
    public static readonly FieldInfo OccupancyField = new("occupancy", "Bodies per cell", ColorMap.Heat, 0, 4, "bodies");

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
        long start = p.BaseSnapshot is null ? 0 : System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(p.BaseSnapshot);
        if (start < 0)
        {
            throw new ScopeFormatException(name, $"expected a start step of 0 or more at the start snapshot's offset 0, found {start}.");
        }

        return new TestSource(name, p.Setup, Recording.Create(p.Setup, p.Inputs, start, p.Hashes ?? default), p.Hashes, p.BaseSnapshot);
    }

    /// <summary>
    /// Opens a live stream. The host's snapshot is the 6-byte setup followed by the world snapshot; each record is one step's input.
    /// The recording starts at the host's step at the snapshot.
    /// </summary>
    public ILiveSource OpenLive(string libraryVersion, int formatVersion, ReadOnlyMemory<byte> snapshot, string name)
    {
        if (formatVersion != TestFormat.Version)
        {
            throw new ScopeFormatException(name, $"expected replay format version {TestFormat.Version}, the host announced {formatVersion}.");
        }

        ReadOnlySpan<byte> s = snapshot.Span;
        if (s.Length < TestFormat.SetupLength + 8)
        {
            throw new ScopeFormatException(name, $"expected at least {TestFormat.SetupLength + 8} bytes of stream snapshot, found {s.Length}.");
        }

        byte[] setup = s[..TestFormat.SetupLength].ToArray();
        TestFormat.ValidateSetup(setup, name);
        byte[] world = s[TestFormat.SetupLength..].ToArray();
        long baseStep = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(world);
        if (baseStep < 0)
        {
            throw new ScopeFormatException(name, $"expected a host step of 0 or more at the snapshot's offset {TestFormat.SetupLength}, found {baseStep}.");
        }

        var recording = Recording.Create(setup, [], baseStep).Fork(baseStep);
        var source = new TestSource(name, setup, recording, null, world);
        return new TestLive(source);
    }

    /// <summary>Makes a source from parts, for tests and generators.</summary>
    public static TestSource CreateSource(string name, byte[] setup, Recording recording, ulong[]? hashes = null) =>
        new(name, setup, recording, hashes, null);

    internal static SourceInfo MakeInfo(string name, ReadOnlySpan<byte> setup) => MakeInfo(name, setup[0], setup.Length == TestFormat.LinkedSetupLength ? setup[6] : 0, setup.Length == TestFormat.LinkedSetupLength);

    internal static SourceInfo MakeInfo(string name, int dimensions, int linkRole, bool fields = false) => new()
    {
        LinkKey = linkRole switch { 1 => LeftKey, 2 => RightKey, _ => null },
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
            .. fields ? (OverlayInfo[])[new OverlayInfo(DensityOverlay, "Density field", false), new OverlayInfo(OccupancyOverlay, "Occupancy field", false)] : [],
        ],
        Fields = fields ? [DensityField, OccupancyField] : [],
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
    private readonly byte[]? _base;

    internal TestSource(string name, byte[] setup, Recording recording, ulong[]? hashes, byte[]? baseSnapshot)
    {
        _setup = setup;
        _base = baseSnapshot;
        Recording = recording;
        ExpectedHashes = hashes;
        Info = TestModule.MakeInfo(name, setup);
    }

    /// <inheritdoc />
    public SourceInfo Info { get; }

    /// <inheritdoc />
    public Recording Recording { get; }

    /// <summary>The per-step world hashes stored in the file (row i is step i), or null when the file had none.</summary>
    public ulong[]? ExpectedHashes { get; }

    /// <inheritdoc />
    public IScopeWorld CreateWorld() => _base is null ? new TestWorld(_setup) : TestWorld.FromStart(_setup, _base);

    /// <inheritdoc />
    public IScopeSource WithRecording(Recording recording) => new TestSource(Info.Name, _setup, recording, null, _base);

    /// <inheritdoc />
    public void WriteReplay(IBufferWriter<byte> writer) => TestFormat.Write(writer, _setup, Recording, ExpectedHashes, _base);
}

/// <summary>A live test source: records are step inputs appended to the source's growing recording.</summary>
internal sealed class TestLive(TestSource source) : ILiveSource
{
    /// <inheritdoc />
    public IScopeSource Source { get; } = source;

    /// <inheritdoc />
    public void AppendRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length > Recording.MaxInputBytes)
        {
            throw new ScopeFormatException(Source.Info.Name, $"expected a record of at most {Recording.MaxInputBytes} bytes, found {record.Length}.");
        }

        Source.Recording.Append(record);
    }
}
