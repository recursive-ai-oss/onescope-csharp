using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace OneScope.Sdk;

/// <summary>The shape a pose-cache entity is drawn as.</summary>
public enum PoseShape : byte
{
    /// <summary>A point.</summary>
    Point = 0,

    /// <summary>A sphere; <see cref="PoseEntity.Size"/>.X is the radius.</summary>
    Sphere = 1,

    /// <summary>A box; <see cref="PoseEntity.Size"/> holds the half extents along the local axes.</summary>
    Box = 2,

    /// <summary>A capsule along the local Y axis; <see cref="PoseEntity.Size"/>.X is the radius and .Y the half length of the segment.</summary>
    Capsule = 3,
}

/// <summary>One thing that has a pose in every frame of a <see cref="PoseCache"/>.</summary>
/// <param name="Id">The entity id, unique in the cache and not <see cref="EntityId.None"/>. Use the id the real simulation gives the same thing.</param>
/// <param name="Name">Display name.</param>
/// <param name="Shape">How to draw it.</param>
/// <param name="Size">Shape extent, in the cache's length unit; see <see cref="PoseShape"/>.</param>
public readonly record struct PoseEntity(EntityId Id, string Name, PoseShape Shape, Vector3 Size);

/// <summary>
/// A neutral pose cache: the positions and orientations of a fixed set of entities over consecutive steps, with no simulation behind them.
/// A reference engine's dump or a baked clip is one. The shell opens it as a ghost source that can be scrubbed and drawn over a real source.
/// The file layout is in <c>docs/pose-cache.md</c>. Frame k is the world at step <see cref="StartStep"/> + k.
/// </summary>
public sealed class PoseCache
{
    /// <summary>The magic bytes at the start of a pose cache.</summary>
    public static ReadOnlySpan<byte> Magic => "OSPC"u8;

    /// <summary>The format version this code reads and writes.</summary>
    public const ushort Version = 1;

    /// <summary>The largest cache <see cref="Parse"/> accepts, in bytes.</summary>
    public const int MaxBytes = 256 << 20;

    /// <summary>The most entities a cache holds.</summary>
    public const int MaxEntities = 1 << 16;

    /// <summary>The most frames a cache holds.</summary>
    public const int MaxFrames = 1 << 22;

    /// <summary>The longest text field in bytes.</summary>
    public const int MaxTextBytes = 1024;

    private const int HeaderBytes = 36;
    private const int FloatsPerPose = 7;
    private const int BytesPerPose = FloatsPerPose * 4;
    private static readonly UTF8Encoding Strict = new(false, true);

    private readonly float[] _poses;

    private PoseCache(PoseCacheInfo info, PoseEntity[] entities, float[] poses, int frames)
    {
        Info = info;
        Entities = entities;
        _poses = poses;
        FrameCount = frames;
    }

    /// <summary>The cache's header: dimensions, unit, first step and the origin of its poses.</summary>
    public PoseCacheInfo Info { get; }

    /// <summary>The entities, in the order <see cref="TryGetPose"/> indexes them.</summary>
    public IReadOnlyList<PoseEntity> Entities { get; }

    /// <summary>The number of frames (1 or more).</summary>
    public int FrameCount { get; }

    /// <summary>The step of frame 0.</summary>
    public long StartStep => Info.StartStep;

    /// <summary>Builds a cache from its parts. <paramref name="poses"/> holds seven floats per entity per frame (x y z, then the rotation x y z w), entity by entity within each frame.</summary>
    /// <exception cref="ArgumentException">A limit is exceeded, an id repeats or is zero, a size is not finite, or <paramref name="poses"/> is not a whole number of frames.</exception>
    public static PoseCache Create(PoseCacheInfo info, IReadOnlyList<PoseEntity> entities, float[] poses)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(poses);
        try
        {
            ValidateInfo(info, "pose cache");
            var copy = new PoseEntity[entities.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = entities[i];
            }

            ValidateEntities(copy, "pose cache");
            if (copy.Length == 0 || poses.Length % (copy.Length * FloatsPerPose) != 0 || poses.Length == 0)
            {
                throw new ScopeFormatException("pose cache", $"expected a whole number of frames of {copy.Length} entities x {FloatsPerPose} floats, found {poses.Length} floats.");
            }

            int frames = poses.Length / (copy.Length * FloatsPerPose);
            if (frames > MaxFrames)
            {
                throw new ScopeFormatException("pose cache", $"expected at most {MaxFrames} frames, found {frames}.");
            }

            return new PoseCache(info, copy, (float[])poses.Clone(), frames);
        }
        catch (ScopeFormatException ex)
        {
            throw new ArgumentException(ex.Message, nameof(info), ex);
        }
    }

    /// <summary>The pose of an entity in a frame.</summary>
    /// <param name="frame">0 to <see cref="FrameCount"/> - 1.</param>
    /// <param name="entity">An index into <see cref="Entities"/>.</param>
    /// <param name="position">The position, in the cache's length unit.</param>
    /// <param name="rotation">The orientation (identity when the stored quaternion is zero).</param>
    /// <returns>False when the indexes are out of range or the entity is absent in that frame (a NaN position).</returns>
    public bool TryGetPose(int frame, int entity, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        if ((uint)frame >= (uint)FrameCount || (uint)entity >= (uint)Entities.Count)
        {
            return false;
        }

        int at = ((frame * Entities.Count) + entity) * FloatsPerPose;
        if (float.IsNaN(_poses[at]))
        {
            return false;
        }

        position = new Vector3(_poses[at], _poses[at + 1], _poses[at + 2]);
        var q = new Quaternion(_poses[at + 3], _poses[at + 4], _poses[at + 5], _poses[at + 6]);
        rotation = q.LengthSquared() > 0 ? Quaternion.Normalize(q) : Quaternion.Identity;
        return true;
    }

    /// <summary>True when the bytes start with the pose-cache magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    /// <summary>Reads a pose cache. Every count is checked against the bytes present before anything is allocated.</summary>
    /// <param name="data">The file's bytes. Untrusted.</param>
    /// <param name="name">What to call the input in error messages.</param>
    /// <exception cref="ScopeFormatException">The bytes are not a valid cache; the message says what was expected and at which offset.</exception>
    public static PoseCache Parse(ReadOnlyMemory<byte> data, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (data.Length > MaxBytes)
        {
            throw new ScopeFormatException(name, $"expected a pose cache of at most {MaxBytes} bytes, found {data.Length}.");
        }

        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < HeaderBytes)
        {
            throw new ScopeFormatException(name, $"expected a {HeaderBytes}-byte header, found {s.Length} bytes.");
        }

        if (!HasMagic(s))
        {
            throw new ScopeFormatException(name, "expected the pose-cache magic 'OSPC' at offset 0.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        if (version != Version)
        {
            throw new ScopeFormatException(name, $"expected pose-cache version {Version} at offset 4, found {version}.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        if (flags != 0)
        {
            throw new ScopeFormatException(name, $"expected flags 0 at offset 6, found {flags}.");
        }

        if (s[9] != 0 || s[10] != 0 || s[11] != 0)
        {
            throw new ScopeFormatException(name, $"expected zero reserved bytes at offsets 9 to 11, found {s[9]}, {s[10]}, {s[11]}.");
        }

        int dimensions = s[8];
        uint entityCount = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
        long startStep = BinaryPrimitives.ReadInt64LittleEndian(s[20..]);
        double metres = BinaryPrimitives.ReadDoubleLittleEndian(s[28..]);
        if (entityCount is 0 or > MaxEntities)
        {
            throw new ScopeFormatException(name, $"expected 1 to {MaxEntities} entities at offset 12, found {entityCount}.");
        }

        if (frameCount is 0 or > MaxFrames)
        {
            throw new ScopeFormatException(name, $"expected 1 to {MaxFrames} frames at offset 16, found {frameCount}.");
        }

        int pos = HeaderBytes;
        string unit = ReadText(s, ref pos, name, "unit name");
        string moduleId = ReadText(s, ref pos, name, "module id");
        string libraryVersion = ReadText(s, ref pos, name, "library version");
        string scene = ReadText(s, ref pos, name, "scene");
        string replay = ReadText(s, ref pos, name, "replay");
        string label = ReadText(s, ref pos, name, "label");
        var info = new PoseCacheInfo(dimensions, new LengthUnit(unit, metres), startStep, moduleId, libraryVersion, scene, replay, label);
        ValidateInfo(info, name);

        // Each entity takes at least 26 bytes, so the table cannot be larger than the input says it is.
        if (checked((long)entityCount * 26) > s.Length - pos)
        {
            throw new ScopeFormatException(name, $"expected {entityCount} entity records of at least 26 bytes at offset {pos}, only {s.Length - pos} bytes remain.");
        }

        var entities = new PoseEntity[entityCount];
        for (int i = 0; i < entities.Length; i++)
        {
            if (s.Length - pos < 24)
            {
                throw new ScopeFormatException(name, $"expected an entity record of 24 bytes at offset {pos}, only {s.Length - pos} remain.");
            }

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(s[pos..]);
            byte shape = s[pos + 8];
            var size = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(s[(pos + 12)..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[(pos + 16)..]),
                BinaryPrimitives.ReadSingleLittleEndian(s[(pos + 20)..]));
            if (shape > (byte)PoseShape.Capsule || s[pos + 9] != 0 || s[pos + 10] != 0 || s[pos + 11] != 0)
            {
                throw new ScopeFormatException(name, $"expected a shape 0 to 3 and zero padding in the entity record at offset {pos}, found shape {shape}.");
            }

            pos += 24;
            string entityName = ReadText(s, ref pos, name, "entity name");
            entities[i] = new PoseEntity(new EntityId(id), entityName, (PoseShape)shape, size);
        }

        ValidateEntities(entities, name);

        long posesBytes = checked((long)entityCount * frameCount * BytesPerPose);
        if (posesBytes != s.Length - pos)
        {
            throw new ScopeFormatException(name, $"expected {posesBytes} bytes of poses ({frameCount} frames x {entityCount} entities x {BytesPerPose}) at offset {pos}, found {s.Length - pos}.");
        }

        var poses = new float[checked((int)(posesBytes / 4))];
        for (int i = 0; i < poses.Length; i++)
        {
            poses[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(pos + (i * 4))..]);
        }

        for (int p = 0; p < poses.Length; p += FloatsPerPose)
        {
            if (float.IsNaN(poses[p]))
            {
                continue;
            }

            for (int k = 0; k < FloatsPerPose; k++)
            {
                if (!float.IsFinite(poses[p + k]))
                {
                    long offset = pos + ((long)(p + k) * 4);
                    throw new ScopeFormatException(name, $"expected a finite pose value at offset {offset} (a NaN position marks an absent entity), found {poses[p + k]}.");
                }
            }
        }

        return new PoseCache(info, entities, poses, (int)frameCount);
    }

    /// <summary>Writes the cache in the layout <see cref="Parse"/> reads. Reading the bytes back and writing again gives the same bytes.</summary>
    public void Write(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        PoseCacheInfo i = Info;
        Span<byte> h = writer.GetSpan(HeaderBytes);
        h[..HeaderBytes].Clear();
        Magic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], Version);
        h[8] = (byte)i.Dimensions;
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)Entities.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)FrameCount);
        BinaryPrimitives.WriteInt64LittleEndian(h[20..], i.StartStep);
        BinaryPrimitives.WriteDoubleLittleEndian(h[28..], i.Unit.Metres);
        writer.Advance(HeaderBytes);
        WriteText(writer, i.Unit.Name);
        WriteText(writer, i.ModuleId);
        WriteText(writer, i.LibraryVersion);
        WriteText(writer, i.Scene);
        WriteText(writer, i.Replay);
        WriteText(writer, i.Label);
        foreach (PoseEntity e in Entities)
        {
            Span<byte> o = writer.GetSpan(24);
            o[..24].Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(o, e.Id.Value);
            o[8] = (byte)e.Shape;
            BinaryPrimitives.WriteSingleLittleEndian(o[12..], e.Size.X);
            BinaryPrimitives.WriteSingleLittleEndian(o[16..], e.Size.Y);
            BinaryPrimitives.WriteSingleLittleEndian(o[20..], e.Size.Z);
            writer.Advance(24);
            WriteText(writer, e.Name);
        }

        int perFrame = Entities.Count * FloatsPerPose;
        for (int frame = 0; frame < FrameCount; frame++)
        {
            Span<byte> o = writer.GetSpan(perFrame * 4);
            for (int k = 0; k < perFrame; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(o[(k * 4)..], _poses[(frame * perFrame) + k]);
            }

            writer.Advance(perFrame * 4);
        }
    }

    private static void WriteText(IBufferWriter<byte> writer, string text)
    {
        int n = Strict.GetByteCount(text);
        Span<byte> o = writer.GetSpan(2 + n);
        BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)n);
        Strict.GetBytes(text, o[2..]);
        writer.Advance(2 + n);
    }

    private static string ReadText(ReadOnlySpan<byte> s, ref int pos, string name, string what)
    {
        if (s.Length - pos < 2)
        {
            throw new ScopeFormatException(name, $"expected a 2-byte length for the {what} at offset {pos}, only {s.Length - pos} bytes remain.");
        }

        int n = BinaryPrimitives.ReadUInt16LittleEndian(s[pos..]);
        if (n > MaxTextBytes)
        {
            throw new ScopeFormatException(name, $"expected the {what} at offset {pos} to be at most {MaxTextBytes} bytes, found {n}.");
        }

        if (n > s.Length - pos - 2)
        {
            throw new ScopeFormatException(name, $"expected {n} bytes of {what} at offset {pos + 2}, only {s.Length - pos - 2} remain.");
        }

        try
        {
            string text = Strict.GetString(s.Slice(pos + 2, n));
            pos += 2 + n;
            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new ScopeFormatException(name, $"expected valid UTF-8 for the {what} at offset {pos + 2}.");
        }
    }

    private static void ValidateInfo(PoseCacheInfo info, string name)
    {
        if (info.Dimensions is not (2 or 3))
        {
            throw new ScopeFormatException(name, $"expected dimensions 2 or 3, found {info.Dimensions}.");
        }

        if (info.StartStep < 0)
        {
            throw new ScopeFormatException(name, $"expected a start step of 0 or more, found {info.StartStep}.");
        }

        if (!double.IsFinite(info.Unit.Metres) || info.Unit.Metres < 0)
        {
            throw new ScopeFormatException(name, $"expected metres per unit to be 0 or a positive finite number, found {info.Unit.Metres}.");
        }

        foreach (string t in new[] { info.Unit.Name, info.ModuleId, info.LibraryVersion, info.Scene, info.Replay, info.Label })
        {
            if (t is null || Strict.GetByteCount(t) > MaxTextBytes)
            {
                throw new ScopeFormatException(name, $"expected every text field to be set and at most {MaxTextBytes} bytes.");
            }
        }
    }

    private static void ValidateEntities(PoseEntity[] entities, string name)
    {
        if (entities.Length is 0 or > MaxEntities)
        {
            throw new ScopeFormatException(name, $"expected 1 to {MaxEntities} entities, found {entities.Length}.");
        }

        var seen = new HashSet<ulong>();
        foreach (PoseEntity e in entities)
        {
            if (!e.Id.IsSome || !seen.Add(e.Id.Value))
            {
                throw new ScopeFormatException(name, $"expected unique non-zero entity ids, found {e.Id.Value} ('{e.Name}').");
            }

            if (!float.IsFinite(e.Size.X) || !float.IsFinite(e.Size.Y) || !float.IsFinite(e.Size.Z) || e.Name is null || Strict.GetByteCount(e.Name) > MaxTextBytes)
            {
                throw new ScopeFormatException(name, $"expected a finite size and a name of at most {MaxTextBytes} bytes for entity {e.Id.Value}.");
            }

            if (!Enum.IsDefined(e.Shape))
            {
                throw new ScopeFormatException(name, $"expected a shape 0 to 3 for entity {e.Id.Value}, found {(int)e.Shape}.");
            }
        }
    }
}

/// <summary>The header of a <see cref="PoseCache"/>.</summary>
/// <param name="Dimensions">2 or 3.</param>
/// <param name="Unit">The length unit of every position and size.</param>
/// <param name="StartStep">The step of frame 0 (0 or more).</param>
/// <param name="ModuleId">The module that opens the real simulation these poses came from, or empty.</param>
/// <param name="LibraryVersion">The library version that produced them, or empty.</param>
/// <param name="Scene">The scene they were recorded from, or empty.</param>
/// <param name="Replay">The replay that produced them (a file name looked up next to the cache), or empty.</param>
/// <param name="Label">Free text such as the engine's name, or empty.</param>
public readonly record struct PoseCacheInfo(int Dimensions, LengthUnit Unit, long StartStep, string ModuleId, string LibraryVersion, string Scene, string Replay, string Label);
