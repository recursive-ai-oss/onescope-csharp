using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>
/// Opens a pose cache (see <see cref="PoseCache"/>) as a ghost source: a track of poses with no simulation behind it. It plays through the same
/// history as any source, so it scrubs on the shared time axis, but it has no pokes and the shell refuses to fork it.
/// </summary>
internal sealed class PoseCacheModule : IScopeModule
{
    /// <summary>The module id a pose-cache source reports.</summary>
    public const string ModuleId = "oscope.posecache";

    public string Id => ModuleId;

    public string Name => "Pose cache (ghost)";

    public IScopeSource Open(ReadOnlyMemory<byte> data, string name)
    {
        if (!PoseCache.HasMagic(data.Span))
        {
            throw new ScopeFormatException(name, "expected the pose-cache magic 'OSPC' at offset 0.");
        }

        return new GhostSource(name, PoseCache.Parse(data, name));
    }
}

internal sealed class GhostSource : IScopeSource
{
    public GhostSource(string name, PoseCache cache)
    {
        Cache = cache;
        PoseCacheInfo i = cache.Info;
        Info = new SourceInfo
        {
            ModuleId = PoseCacheModule.ModuleId,
            Name = name,
            Dimensions = i.Dimensions,
            Unit = i.Unit,
            Axes = [new AxisInfo(AxisIds.Time, "Time", 0)],
            EntityKinds = [new EntityKindInfo("ghost-root", "Ghost"), new EntityKindInfo("ghost", "Ghost pose")],
            Overlays = [new OverlayInfo(0, "Ghost poses", true)],
            Ghost = new GhostInfo(i.ModuleId, i.LibraryVersion, i.Scene, i.Replay),
        };

        // One step per frame after the first; the steps carry no input because nothing is simulated.
        Recording = Recording.Create(ReadOnlyMemory<byte>.Empty, new ReadOnlyMemory<byte>[cache.FrameCount - 1], i.StartStep);
    }

    public PoseCache Cache { get; }

    public SourceInfo Info { get; }

    public Recording Recording { get; }

    public IScopeWorld CreateWorld() => new GhostWorld(Cache);

    public IScopeSource WithRecording(Recording recording) =>
        throw new InvalidOperationException("A ghost source has no simulation and cannot be forked.");

    public void WriteReplay(IBufferWriter<byte> writer) => Cache.Write(writer);
}

/// <summary>The world of a ghost source: the poses of one frame. Its step is the real simulation's step number.</summary>
internal sealed class GhostWorld : IScopeWorld
{
    private static readonly Color32 GhostColor = new(210, 130, 255, 190);
    private static readonly EntityId RootId = new(ulong.MaxValue);

    private readonly PoseCache _cache;
    private long _step;

    public GhostWorld(PoseCache cache)
    {
        _cache = cache;
        _step = cache.StartStep;
    }

    public long Step => _step;

    private int Frame => (int)Math.Clamp(_step - _cache.StartStep, 0, _cache.FrameCount - 1);

    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, (ulong)_step);
        int frame = Frame;
        for (int e = 0; e < _cache.Entities.Count; e++)
        {
            if (_cache.TryGetPose(frame, e, out Vector3 p, out Quaternion q))
            {
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(p.X));
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(p.Y));
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(p.Z));
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(q.X));
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(q.Y));
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(q.Z));
                Mix(ref h, (uint)BitConverter.SingleToInt32Bits(q.W));
            }
            else
            {
                Mix(ref h, ulong.MaxValue);
            }
        }

        return h;
    }

    private static void Mix(ref ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(value >> (i * 8));
            h *= 1099511628211UL;
        }
    }

    public void Advance(ReadOnlySpan<byte> input, TimelineSink sink) => _step++;

    public void AdvanceAxis(string axis, int position, ReadOnlySpan<byte> input) =>
        throw new ArgumentException($"A ghost source has only the time axis, not '{axis}'.", nameof(axis));

    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), _step);
        writer.Advance(8);
    }

    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length != 8)
        {
            throw new ScopeFormatException("ghost snapshot", $"expected 8 bytes, found {snapshot.Length}.");
        }

        long step = BinaryPrimitives.ReadInt64LittleEndian(snapshot);
        if (step < _cache.StartStep)
        {
            throw new ScopeFormatException("ghost snapshot", $"expected a step of {_cache.StartStep} or more at offset 0, found {step}.");
        }

        _step = step;
    }

    public void CollectEntities(List<EntityInfo> into)
    {
        into.Clear();
        into.Add(new EntityInfo(RootId, EntityId.None, "ghost-root", "Ghost: " + _cache.Info.Label));
        int frame = Frame;
        for (int e = 0; e < _cache.Entities.Count; e++)
        {
            if (_cache.TryGetPose(frame, e, out _, out _))
            {
                PoseEntity pe = _cache.Entities[e];
                into.Add(new EntityInfo(pe.Id, RootId, "ghost", pe.Name));
            }
        }
    }

    public bool Inspect(EntityId entity, PropertyList into)
    {
        into.Clear();
        if (entity == RootId)
        {
            PoseCacheInfo i = _cache.Info;
            into.Add("Ghost", "step", ScopeValue.OfInteger(_step));
            into.Add("Ghost", "frame", ScopeValue.OfInteger(Frame));
            into.Add("Ghost", "frames", ScopeValue.OfInteger(_cache.FrameCount));
            into.Add("Origin", "module", ScopeValue.OfText(i.ModuleId));
            into.Add("Origin", "library version", ScopeValue.OfText(i.LibraryVersion));
            into.Add("Origin", "scene", ScopeValue.OfText(i.Scene));
            into.Add("Origin", "replay", ScopeValue.OfText(i.Replay));
            return true;
        }

        for (int e = 0; e < _cache.Entities.Count; e++)
        {
            PoseEntity pe = _cache.Entities[e];
            if (pe.Id != entity || !_cache.TryGetPose(Frame, e, out Vector3 p, out Quaternion q))
            {
                continue;
            }

            into.Add("Transform", "position", ScopeValue.OfVector(p));
            into.Add("Transform", "rotation", ScopeValue.OfVector(new Vector3(q.X, q.Y, q.Z)));
            into.Add("Transform", "rotation w", ScopeValue.OfNumber(q.W));
            into.Add("Shape", "shape", ScopeValue.OfText(pe.Shape.ToString().ToLowerInvariant()));
            into.Add("Shape", "size", ScopeValue.OfVector(pe.Size));
            return true;
        }

        return false;
    }

    public void Draw(DrawList into, in DrawRequest request)
    {
        if (!request.IsOn(0))
        {
            return;
        }

        bool flat = _cache.Info.Dimensions == 2;
        var style = new DrawStyle(GhostColor, 0, 5, DrawFlags.Wireframe);
        int frame = Frame;
        for (int e = 0; e < _cache.Entities.Count; e++)
        {
            if (!_cache.TryGetPose(frame, e, out Vector3 p, out Quaternion q))
            {
                continue;
            }

            if (flat)
            {
                p.Z = 0;
            }

            PoseEntity pe = _cache.Entities[e];
            switch (pe.Shape)
            {
                case PoseShape.Sphere:
                    into.Sphere(p, pe.Size.X, style);
                    break;
                case PoseShape.Box:
                    into.Box(p, pe.Size, q, style);
                    break;
                case PoseShape.Capsule:
                    {
                        Vector3 axis = Vector3.Transform(new Vector3(0, pe.Size.Y, 0), q);
                        into.Capsule(p - axis, p + axis, pe.Size.X, style);
                        break;
                    }

                default:
                    into.Point(p, 6, style);
                    break;
            }
        }
    }

    public void EncodePoke(in PokeCall call, IBufferWriter<byte> into) =>
        throw new ArgumentException("A ghost source takes no pokes.");

    public Difference? FirstDifference(IScopeWorld other)
    {
        if (other is not GhostWorld o)
        {
            throw new ArgumentException("The other world is from a different module.", nameof(other));
        }

        if (_step != o._step)
        {
            return new Difference(RootId, "step", _step.ToString(CultureInfo.InvariantCulture), o._step.ToString(CultureInfo.InvariantCulture));
        }

        return Hash() == o.Hash() ? null : new Difference(RootId, "poses", "differ", "differ");
    }
}
