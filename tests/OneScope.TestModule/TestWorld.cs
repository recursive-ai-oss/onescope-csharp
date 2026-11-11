using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using OneScope.Sdk;

namespace OneScope.TestModule;

/// <summary>The test module's world: integer bodies bouncing in a 10 m box.</summary>
public sealed class TestWorld : IScopeWorld
{
    private const int Size = 10000;
    private const int SleepAfter = 30;
    private const int BodyInts = 8;

    private static readonly Color32 AwakeColor = new(60, 200, 90);
    private static readonly Color32 AsleepColor = new(110, 130, 170);
    private static readonly Color32 StaticColor = new(90, 90, 90);
    private static readonly Color32 SensorColor = new(80, 160, 255, 80);
    private static readonly Color32 ArrowColor = new(240, 200, 40);
    private static readonly Color32 BoundsColor = new(200, 200, 200);

    private readonly int _dims;
    private readonly int _count;
    private readonly string[] _names;
    private readonly bool[] _alive;
    private readonly int[] _sleep;
    private readonly int[] _p;
    private readonly int[] _v;
    private int _gravity = -3;

    /// <summary>Creates the world from the 6-byte setup.</summary>
    public TestWorld(ReadOnlySpan<byte> setup)
    {
        TestFormat.ValidateSetup(setup, "setup");
        _dims = setup[0];
        _count = setup[1];
        uint seed = BinaryPrimitives.ReadUInt32LittleEndian(setup[2..]);
        _names = new string[_count];
        _alive = new bool[_count];
        _sleep = new int[_count];
        _p = new int[_count * 3];
        _v = new int[_count * 3];
        uint state = seed ^ 0x9E3779B9u;
        for (int i = 0; i < _count; i++)
        {
            _names[i] = "body-" + i.ToString(CultureInfo.InvariantCulture);
            _alive[i] = true;
            for (int a = 0; a < _dims; a++)
            {
                _p[i * 3 + a] = 500 + (int)(Next(ref state) % 9000);
                _v[i * 3 + a] = (int)(Next(ref state) % 121) - 60;
            }
        }
    }

    /// <inheritdoc />
    public long Step { get; private set; }

    private static uint Next(ref uint state)
    {
        state = unchecked(state * 1664525u + 1013904223u);
        return state >> 8;
    }

    /// <inheritdoc />
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, Step);
        Mix(ref h, _gravity);
        for (int i = 0; i < _count; i++)
        {
            Mix(ref h, _alive[i] ? 1 : 0);
            Mix(ref h, _sleep[i]);
            for (int a = 0; a < 3; a++)
            {
                Mix(ref h, _p[i * 3 + a]);
                Mix(ref h, _v[i * 3 + a]);
            }
        }

        return h;
    }

    private static void Mix(ref ulong h, long value)
    {
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(value >> (i * 8));
            h *= 1099511628211UL;
        }
    }

    /// <inheritdoc />
    public void Advance(ReadOnlySpan<byte> input, TimelineSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        long reached = Step + 1;
        RunStages(StageCountAll, input, sink, reached);
        Step = reached;
        if (sink.IsRecording)
        {
            long energy = 0;
            int awake = 0;
            for (int i = 0; i < _count; i++)
            {
                if (!_alive[i])
                {
                    continue;
                }

                for (int a = 0; a < 3; a++)
                {
                    energy += (long)_v[i * 3 + a] * _v[i * 3 + a];
                }

                awake += _sleep[i] < SleepAfter ? 1 : 0;
            }

            sink.Sample("energy", energy);
            sink.Sample("awake", awake);
            if (reached % 1000 == 0)
            {
                sink.Marker("phase", "step " + reached.ToString(CultureInfo.InvariantCulture));
            }

            if (reached % 2500 == 0)
            {
                int index = (int)(reached / 2500 % _count);
                EntityId who = _alive[index] ? new EntityId(TestModule.FirstBodyId + (ulong)index) : TestModule.World;
                Severity severity = reached / 2500 % 2 == 1 ? Severity.Warning : Severity.Error;
                sink.Finding("TM001", severity, [who], "Total kinetic energy is " + energy.ToString(CultureInfo.InvariantCulture), "Use set_gravity with a smaller magnitude.");
            }
        }
    }

    private const int StageCountAll = TestModule.StageCount;

    /// <inheritdoc />
    public void AdvanceAxis(string axis, int position, ReadOnlySpan<byte> input)
    {
        if (axis != TestModule.StageAxis)
        {
            throw new ArgumentException($"Axis '{axis}' is not declared.", nameof(axis));
        }

        if (position is < 0 or > TestModule.StageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Positions are 0 to {TestModule.StageCount}.");
        }

        RunStages(position, input, TimelineSink.None, Step + 1);
    }

    private void RunStages(int stages, ReadOnlySpan<byte> input, TimelineSink sink, long reached)
    {
        for (int stage = 0; stage < stages; stage++)
        {
            switch (stage)
            {
                case 0:
                    ApplyInput(input);
                    break;
                case 1:
                    for (int i = 0; i < _count; i++)
                    {
                        if (_alive[i] && _sleep[i] < SleepAfter)
                        {
                            _v[i * 3 + 1] += _gravity;
                        }
                    }

                    break;
                case 2:
                    Move(sink);
                    break;
                default:
                    for (int i = 0; i < _count; i++)
                    {
                        if (!_alive[i])
                        {
                            continue;
                        }

                        int speed = Math.Abs(_v[i * 3]) + Math.Abs(_v[i * 3 + 1]) + Math.Abs(_v[i * 3 + 2]);
                        _sleep[i] = speed <= 6 && _p[i * 3 + 1] <= 8 ? Math.Min(_sleep[i] + 1, SleepAfter) : 0;
                    }

                    break;
            }
        }

        _ = reached;
    }

    private void Move(TimelineSink sink)
    {
        for (int i = 0; i < _count; i++)
        {
            if (!_alive[i] || _sleep[i] >= SleepAfter)
            {
                continue;
            }

            for (int a = 0; a < _dims; a++)
            {
                int idx = i * 3 + a;
                int p = _p[idx] + _v[idx];
                int v = _v[idx];
                bool bounced = false;
                if (p < 0)
                {
                    p = -p;
                    v = Math.Abs((v * 3) >> 2);
                    bounced = true;
                }
                else if (p > Size)
                {
                    p = 2 * Size - p;
                    v = -Math.Abs((v * 3) >> 2);
                    bounced = true;
                }

                p = Math.Clamp(p, 0, Size);
                _p[idx] = p;
                _v[idx] = v;
                if (bounced)
                {
                    if (a == 1 && v >= 0)
                    {
                        _v[i * 3] = _v[i * 3] * 7 / 8;
                        _v[i * 3 + 2] = _v[i * 3 + 2] * 7 / 8;
                    }

                    if (sink.IsRecording)
                    {
                        sink.Event("bounce", new EntityId(TestModule.FirstBodyId + (ulong)i), "xyz"[a] + (v >= 0 ? "-" : "+"));
                    }
                }
            }
        }
    }

    private void ApplyInput(ReadOnlySpan<byte> input)
    {
        int pos = 0;
        while (pos < input.Length)
        {
            byte op = input[pos];
            int need = op switch { 1 or 2 => 14, 3 => 2, 4 => 5, _ => 0 };
            if (need == 0)
            {
                throw new ScopeFormatException("step input", $"expected an operation 1 to 4 at offset {pos}, found {op}.");
            }

            if (input.Length - pos < need)
            {
                throw new ScopeFormatException("step input", $"expected {need} bytes for operation {op} at offset {pos}, only {input.Length - pos} remain.");
            }

            if (op == 4)
            {
                _gravity = BinaryPrimitives.ReadInt32LittleEndian(input[(pos + 1)..]);
            }
            else
            {
                int i = input[pos + 1];
                if (i >= _count)
                {
                    throw new ScopeFormatException("step input", $"expected a body index below {_count} at offset {pos + 1}, found {i}.");
                }

                if (op == 3)
                {
                    _alive[i] = false;
                }
                else if (_alive[i])
                {
                    int x = BinaryPrimitives.ReadInt32LittleEndian(input[(pos + 2)..]);
                    int y = BinaryPrimitives.ReadInt32LittleEndian(input[(pos + 6)..]);
                    int z = BinaryPrimitives.ReadInt32LittleEndian(input[(pos + 10)..]);
                    if (_dims == 2)
                    {
                        z = 0;
                    }

                    if (op == 1)
                    {
                        _v[i * 3] = Math.Clamp(_v[i * 3] + x, -1_000_000, 1_000_000);
                        _v[i * 3 + 1] = Math.Clamp(_v[i * 3 + 1] + y, -1_000_000, 1_000_000);
                        _v[i * 3 + 2] = Math.Clamp(_v[i * 3 + 2] + z, -1_000_000, 1_000_000);
                    }
                    else
                    {
                        _p[i * 3] = Math.Clamp(x, 0, Size);
                        _p[i * 3 + 1] = Math.Clamp(y, 0, Size);
                        _p[i * 3 + 2] = Math.Clamp(z, 0, Size);
                        _v[i * 3] = _v[i * 3 + 1] = _v[i * 3 + 2] = 0;
                    }

                    _sleep[i] = 0;
                }
            }

            pos += need;
        }
    }

    /// <inheritdoc />
    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> o = writer.GetSpan(12 + _count * BodyInts * 4);
        int pos = 0;
        BinaryPrimitives.WriteInt64LittleEndian(o, Step);
        BinaryPrimitives.WriteInt32LittleEndian(o[8..], _gravity);
        pos = 12;
        for (int i = 0; i < _count; i++)
        {
            Put(o, ref pos, _alive[i] ? 1 : 0);
            Put(o, ref pos, _sleep[i]);
            for (int a = 0; a < 3; a++)
            {
                Put(o, ref pos, _p[i * 3 + a]);
            }

            for (int a = 0; a < 3; a++)
            {
                Put(o, ref pos, _v[i * 3 + a]);
            }
        }

        writer.Advance(pos);
    }

    private static void Put(Span<byte> o, ref int pos, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(o[pos..], value);
        pos += 4;
    }

    /// <inheritdoc />
    public void Restore(ReadOnlySpan<byte> snapshot)
    {
        int expected = 12 + _count * BodyInts * 4;
        if (snapshot.Length != expected)
        {
            throw new ScopeFormatException("snapshot", $"expected {expected} bytes, found {snapshot.Length}.");
        }

        long step = BinaryPrimitives.ReadInt64LittleEndian(snapshot);
        if (step < 0)
        {
            throw new ScopeFormatException("snapshot", $"expected a step of 0 or more at offset 0, found {step}.");
        }

        int pos;
        for (int i = 0; i < _count; i++)
        {
            pos = 12 + i * BodyInts * 4;
            int alive = BinaryPrimitives.ReadInt32LittleEndian(snapshot[pos..]);
            if (alive is not (0 or 1))
            {
                throw new ScopeFormatException("snapshot", $"expected alive flag 0 or 1 at offset {pos}, found {alive}.");
            }
        }

        Step = step;
        _gravity = BinaryPrimitives.ReadInt32LittleEndian(snapshot[8..]);
        pos = 12;
        for (int i = 0; i < _count; i++)
        {
            _alive[i] = Get(snapshot, ref pos) == 1;
            _sleep[i] = Get(snapshot, ref pos);
            for (int a = 0; a < 3; a++)
            {
                _p[i * 3 + a] = Get(snapshot, ref pos);
            }

            for (int a = 0; a < 3; a++)
            {
                _v[i * 3 + a] = Get(snapshot, ref pos);
            }
        }
    }

    private static int Get(ReadOnlySpan<byte> s, ref int pos)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(s[pos..]);
        pos += 4;
        return v;
    }

    /// <inheritdoc />
    public void CollectEntities(List<EntityInfo> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        into.Add(new EntityInfo(TestModule.World, EntityId.None, "world", "World"));
        into.Add(new EntityInfo(TestModule.Floor, TestModule.World, "static", "Floor"));
        into.Add(new EntityInfo(TestModule.Zone, TestModule.World, "sensor", "Zone"));
        for (int i = 0; i < _count; i++)
        {
            if (_alive[i])
            {
                into.Add(new EntityInfo(new EntityId(TestModule.FirstBodyId + (ulong)i), TestModule.World, "body", _names[i]));
            }
        }
    }

    /// <inheritdoc />
    public bool Inspect(EntityId entity, PropertyList into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (entity == TestModule.World)
        {
            into.Add("World", "step", ScopeValue.OfInteger(Step));
            into.Add("World", "gravity", ScopeValue.OfInteger(_gravity));
            into.Add("World", "bodies", ScopeValue.OfInteger(AliveCount()));
            return true;
        }

        if (entity == TestModule.Floor || entity == TestModule.Zone)
        {
            into.Add("Shape", "extent", ScopeValue.OfVector(new Vector3(Size, 0, Size)));
            return true;
        }

        if (!TryBody(entity, out int i))
        {
            return false;
        }

        into.Add("Transform", "position", ScopeValue.OfVector(new Vector3(_p[i * 3], _p[i * 3 + 1], _p[i * 3 + 2])));
        into.Add("Motion", "velocity", ScopeValue.OfVector(new Vector3(_v[i * 3], _v[i * 3 + 1], _v[i * 3 + 2])));
        into.Add("Motion", "asleep", ScopeValue.OfBool(_sleep[i] >= SleepAfter));
        into.Add("Motion", "still_steps", ScopeValue.OfInteger(_sleep[i]));
        return true;
    }

    private int AliveCount()
    {
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            n += _alive[i] ? 1 : 0;
        }

        return n;
    }

    private bool TryBody(EntityId entity, out int index)
    {
        ulong rel = entity.Value - TestModule.FirstBodyId;
        index = rel < (ulong)_count ? (int)rel : -1;
        return index >= 0 && _alive[index];
    }

    /// <inheritdoc />
    public void Draw(DrawList into, in DrawRequest request)
    {
        ArgumentNullException.ThrowIfNull(into);
        bool is3D = _dims == 3;
        if (request.IsOn(0))
        {
            var floor = new DrawStyle(StaticColor, 0, 0);
            if (is3D)
            {
                into.Box(new Vector3(Size / 2f, -10, Size / 2f), new Vector3(Size / 2f, 10, Size / 2f), Quaternion.Identity, floor);
            }
            else
            {
                into.Line(new Vector3(0, 0, 0), new Vector3(Size, 0, 0), floor);
            }

            into.Box(new Vector3(Size / 2f, Size / 2f, 0), new Vector3(1000, Size / 2f, is3D ? 1000 : 0), Quaternion.Identity, new DrawStyle(SensorColor, 0, -1, DrawFlags.Wireframe));
        }

        Span<char> text = stackalloc char[16];
        for (int i = 0; i < _count; i++)
        {
            if (!_alive[i])
            {
                continue;
            }

            var pos = new Vector3(_p[i * 3], _p[i * 3 + 1], _p[i * 3 + 2]);
            Color32 color = _sleep[i] >= SleepAfter ? AsleepColor : AwakeColor;
            if (request.IsOn(0))
            {
                var style = new DrawStyle(color, 0, 1);
                if (is3D)
                {
                    switch (i % 4)
                    {
                        case 0:
                            into.Sphere(pos, 150, style);
                            break;
                        case 1:
                            into.Box(pos, new Vector3(120, 120, 120), Quaternion.Identity, style);
                            break;
                        case 2:
                            into.Capsule(pos, pos + new Vector3(0, 200, 0), 100, style);
                            break;
                        default:
                            into.Cylinder(pos, pos + new Vector3(0, 200, 0), 100, style);
                            break;
                    }
                }
                else
                {
                    into.Triangle(pos + new Vector3(-150, -150, 0), pos + new Vector3(150, -150, 0), pos + new Vector3(0, 150, 0), style);
                    into.Point(pos, 4, style);
                }
            }

            if (request.IsOn(1))
            {
                var v = new Vector3(_v[i * 3], _v[i * 3 + 1], _v[i * 3 + 2]);
                into.Arrow(pos, pos + v * 10, 60, new DrawStyle(ArrowColor, 1, 2));
            }

            if (request.IsOn(3))
            {
                "b".CopyTo(text);
                i.TryFormat(text[1..], out int written, default, CultureInfo.InvariantCulture);
                into.Label(pos + new Vector3(0, 250, 0), text[..(1 + written)], new DrawStyle(Color32.White, 3, 3));
            }
        }

        if (request.IsOn(2))
        {
            var b = new DrawStyle(BoundsColor, 2, 0);
            float z = is3D ? Size : 0;
            Vector3 a0 = new(0, 0, 0), a1 = new(Size, 0, 0), a2 = new(Size, Size, 0), a3 = new(0, Size, 0);
            into.Line(a0, a1, b);
            into.Line(a1, a2, b);
            into.Line(a2, a3, b);
            into.Line(a3, a0, b);
            if (is3D)
            {
                Vector3 c = new(0, 0, z);
                Vector3 d = new(Size, 0, z), e = new(Size, Size, z), f = new(0, Size, z);
                into.Line(c, d, b);
                into.Line(d, e, b);
                into.Line(e, f, b);
                into.Line(f, c, b);
                into.Line(a0, c, b);
                into.Line(a1, d, b);
                into.Line(a2, e, b);
                into.Line(a3, f, b);
            }
        }
    }

    /// <inheritdoc />
    public void EncodePoke(in PokeCall call, IBufferWriter<byte> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(call.Args);
        switch (call.Poke)
        {
            case "impulse":
            case "teleport":
                {
                    Expect(call, ValueKind.Entity, ValueKind.Vector);
                    int i = RequireBody(call.Args[0].GetEntity());
                    Vector3 v = call.Args[1].GetVector();
                    Span<byte> o = into.GetSpan(14);
                    o[0] = (byte)(call.Poke == "impulse" ? 1 : 2);
                    o[1] = (byte)i;
                    BinaryPrimitives.WriteInt32LittleEndian(o[2..], ToInt(v.X));
                    BinaryPrimitives.WriteInt32LittleEndian(o[6..], ToInt(v.Y));
                    BinaryPrimitives.WriteInt32LittleEndian(o[10..], ToInt(v.Z));
                    into.Advance(14);
                    break;
                }

            case "remove":
                {
                    Expect(call, ValueKind.Entity);
                    int i = RequireBody(call.Args[0].GetEntity());
                    Span<byte> o = into.GetSpan(2);
                    o[0] = 3;
                    o[1] = (byte)i;
                    into.Advance(2);
                    break;
                }

            case "set_gravity":
                {
                    Expect(call, ValueKind.Integer);
                    long g = call.Args[0].GetInteger();
                    if (g is < -1000 or > 1000)
                    {
                        throw new ArgumentException("gravity must be -1000 to 1000.", nameof(call));
                    }

                    Span<byte> o = into.GetSpan(5);
                    o[0] = 4;
                    BinaryPrimitives.WriteInt32LittleEndian(o[1..], (int)g);
                    into.Advance(5);
                    break;
                }

            default:
                throw new ArgumentException($"Unknown poke '{call.Poke}'.", nameof(call));
        }
    }

    private static int ToInt(float value) => (int)Math.Clamp(MathF.Round(value), -1_000_000f, 1_000_000f);

    private static void Expect(in PokeCall call, params ValueKind[] kinds)
    {
        if (call.Args.Length != kinds.Length)
        {
            throw new ArgumentException($"Poke '{call.Poke}' takes {kinds.Length} arguments, got {call.Args.Length}.", nameof(call));
        }

        for (int i = 0; i < kinds.Length; i++)
        {
            if (call.Args[i].Kind != kinds[i])
            {
                throw new ArgumentException($"Poke '{call.Poke}' argument {i} must be {kinds[i]}, got {call.Args[i].Kind}.", nameof(call));
            }
        }
    }

    private int RequireBody(EntityId id)
    {
        if (!TryBody(id, out int i))
        {
            throw new ArgumentException($"Entity {id} is not a live body.", nameof(id));
        }

        return i;
    }

    /// <inheritdoc />
    public Difference? FirstDifference(IScopeWorld other)
    {
        if (other is not TestWorld o)
        {
            throw new ArgumentException("The other world is from a different module.", nameof(other));
        }

        if (Step != o.Step)
        {
            return new Difference(TestModule.World, "step", Fmt(Step), Fmt(o.Step));
        }

        if (_count != o._count)
        {
            return new Difference(TestModule.World, "bodies", Fmt(_count), Fmt(o._count));
        }

        if (_gravity != o._gravity)
        {
            return new Difference(TestModule.World, "gravity", Fmt(_gravity), Fmt(o._gravity));
        }

        for (int i = 0; i < _count; i++)
        {
            var id = new EntityId(TestModule.FirstBodyId + (ulong)i);
            if (_alive[i] != o._alive[i])
            {
                return new Difference(id, "alive", _alive[i].ToString(), o._alive[i].ToString());
            }

            for (int a = 0; a < 3; a++)
            {
                if (_p[i * 3 + a] != o._p[i * 3 + a])
                {
                    return new Difference(id, "position." + "xyz"[a], Fmt(_p[i * 3 + a]), Fmt(o._p[i * 3 + a]));
                }
            }

            for (int a = 0; a < 3; a++)
            {
                if (_v[i * 3 + a] != o._v[i * 3 + a])
                {
                    return new Difference(id, "velocity." + "xyz"[a], Fmt(_v[i * 3 + a]), Fmt(o._v[i * 3 + a]));
                }
            }

            if (_sleep[i] != o._sleep[i])
            {
                return new Difference(id, "still_steps", Fmt(_sleep[i]), Fmt(o._sleep[i]));
            }
        }

        return null;
    }

    private static string Fmt(long v) => v.ToString(CultureInfo.InvariantCulture);
}
