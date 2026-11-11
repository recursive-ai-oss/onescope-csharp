using System.Buffers;
using System.Buffers.Binary;
using OneScope.Sdk;

namespace OneScope.TestModule;

/// <summary>
/// The test module's replay file (little endian):
/// magic "OSTM", u16 version (1), u16 flags (bit 0: hash rows present, bit 2: a link block follows the setup), u32 setup length (6, or 10 with a link block),
/// setup (u8 dimensions 2|3, u8 body count 1..64, u32 seed; with a link block also u8 role (1 sends a body, 2 receives it, 3 declares the scalar fields), u8 body, u16 transfer step), u32 step count,
/// then if flag bit 1 a u32 length and a start snapshot (the world's snapshot at the host step the stream began at; its first 8 bytes are that step, the recording's start step), then if flag bit 0 (step count + 1) u64 world hashes (row i is the hash at the start step + i),
/// then per step u16 input length and that many bytes.
/// </summary>
public static class TestFormat
{
    /// <summary>The module id.</summary>
    public const string ModuleId = "oscope.test";

    /// <summary>The format version this code reads and writes.</summary>
    public const ushort Version = 1;

    /// <summary>Setup length in bytes.</summary>
    public const int SetupLength = 6;

    /// <summary>Setup length in bytes when the setup carries a link block (a body handed from one test world to another).</summary>
    public const int LinkedSetupLength = 10;

    /// <summary>Most steps a file may declare.</summary>
    public const uint MaxSteps = 1 << 24;

    private static ReadOnlySpan<byte> Magic => "OSTM"u8;

    /// <summary>The parsed contents of a replay file.</summary>
    public sealed record Parsed(byte[] Setup, ReadOnlyMemory<byte>[] Inputs, ulong[]? Hashes, byte[]? BaseSnapshot);

    /// <summary>Reads a replay file. Every length is checked against the bytes that remain before anything is allocated.</summary>
    /// <exception cref="ScopeFormatException">The bytes are not a valid replay.</exception>
    public static Parsed Parse(ReadOnlyMemory<byte> data, string name)
    {
        ReadOnlySpan<byte> s = data.Span;
        int pos = 0;
        if (s.Length < 4 || !s[..4].SequenceEqual(Magic))
        {
            throw new ScopeFormatException(name, "expected the magic bytes 'OSTM' at offset 0.");
        }

        pos = 4;
        ushort version = ReadU16(s, ref pos, name, "version");
        if (version != Version)
        {
            throw new ScopeFormatException(name, $"expected format version {Version} at offset 4, found {version}.");
        }

        ushort flags = ReadU16(s, ref pos, name, "flags");
        if ((flags & ~7) != 0)
        {
            throw new ScopeFormatException(name, $"expected flags 0 to 7 at offset 6, found {flags}.");
        }

        uint setupLen = ReadU32(s, ref pos, name, "setup length");
        int expectedSetup = (flags & 4) != 0 ? LinkedSetupLength : SetupLength;
        if (setupLen != expectedSetup)
        {
            throw new ScopeFormatException(name, $"expected setup length {expectedSetup} at offset 8, found {setupLen}.");
        }

        byte[] setup = Take(s, ref pos, expectedSetup, name, "setup").ToArray();
        ValidateSetup(setup, name);
        uint count = ReadU32(s, ref pos, name, "step count");
        if (count > MaxSteps)
        {
            throw new ScopeFormatException(name, $"expected at most {MaxSteps} steps, found {count}.");
        }

        byte[]? baseSnapshot = null;
        if ((flags & 2) != 0)
        {
            uint snapLen = ReadU32(s, ref pos, name, "snapshot length");
            baseSnapshot = Take(s, ref pos, checked((int)Math.Min(snapLen, int.MaxValue)), name, "start snapshot").ToArray();
            if (baseSnapshot.Length < 8)
            {
                throw new ScopeFormatException(name, $"expected a start snapshot of at least 8 bytes, found {baseSnapshot.Length}.");
            }
        }

        ulong[]? hashes = null;
        if ((flags & 1) != 0)
        {
            long rowBytes = checked((count + 1L) * 8);
            if (rowBytes > s.Length - pos)
            {
                throw new ScopeFormatException(name, $"expected {rowBytes} bytes of hash rows at offset {pos}, only {s.Length - pos} remain.");
            }

            hashes = new ulong[count + 1];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(s[pos..]);
                pos += 8;
            }
        }

        if (checked(count * 2L) > s.Length - pos)
        {
            throw new ScopeFormatException(name, $"expected at least {count * 2L} bytes for {count} step inputs at offset {pos}, only {s.Length - pos} remain.");
        }

        var inputs = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < inputs.Length; i++)
        {
            ushort len = ReadU16(s, ref pos, name, "input length");
            if (len > s.Length - pos)
            {
                throw new ScopeFormatException(name, $"expected {len} bytes for step {i}'s input at offset {pos}, only {s.Length - pos} remain.");
            }

            inputs[i] = len == 0 ? ReadOnlyMemory<byte>.Empty : data.Slice(pos, len).ToArray();
            pos += len;
        }

        if (pos != s.Length)
        {
            throw new ScopeFormatException(name, $"expected the file to end at offset {pos}, but {s.Length - pos} bytes follow.");
        }

        return new Parsed(setup, inputs, hashes, baseSnapshot);
    }

    /// <summary>Writes a replay file. Hash rows are written when <paramref name="hashes"/> is not null.</summary>
    public static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> setup, Recording recording, ulong[]? hashes, byte[]? baseSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        long count = recording.StepCount;
        long size = 4 + 2 + 2 + 4 + setup.Length + 4 + (baseSnapshot is null ? 0 : 4 + baseSnapshot.Length) + (hashes is null ? 0 : (count + 1) * 8) + count * 2;
        for (long i = 0; i < count; i++)
        {
            size += recording.InputAt(recording.StartStep + i).Length;
        }

        Span<byte> o = writer.GetSpan(checked((int)size));
        int pos = 0;
        Magic.CopyTo(o);
        pos = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(o[pos..], Version);
        pos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(o[pos..], (ushort)((hashes is null ? 0 : 1) | (baseSnapshot is null ? 0 : 2) | (setup.Length == LinkedSetupLength ? 4 : 0)));
        pos += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(o[pos..], (uint)setup.Length);
        pos += 4;
        setup.CopyTo(o[pos..]);
        pos += setup.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(o[pos..], (uint)count);
        pos += 4;
        if (baseSnapshot is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(o[pos..], (uint)baseSnapshot.Length);
            pos += 4;
            baseSnapshot.CopyTo(o[pos..]);
            pos += baseSnapshot.Length;
        }

        if (hashes is not null)
        {
            foreach (ulong h in hashes)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(o[pos..], h);
                pos += 8;
            }
        }

        for (long i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> input = recording.InputAt(recording.StartStep + i).Span;
            BinaryPrimitives.WriteUInt16LittleEndian(o[pos..], (ushort)input.Length);
            pos += 2;
            input.CopyTo(o[pos..]);
            pos += input.Length;
        }

        writer.Advance(pos);
    }

    /// <summary>Builds the 6-byte setup.</summary>
    public static byte[] MakeSetup(int dimensions, int bodies, uint seed)
    {
        var setup = new byte[SetupLength];
        setup[0] = (byte)dimensions;
        setup[1] = (byte)bodies;
        BinaryPrimitives.WriteUInt32LittleEndian(setup.AsSpan(2), seed);
        ValidateSetup(setup, "setup");
        return setup;
    }

    /// <summary>
    /// Builds a setup whose world takes part in a transfer: the sender (role 1) loses body <paramref name="body"/> at step <paramref name="step"/>,
    /// the receiver (role 2) gains it there. Both worlds use the same seed and body count, so the body has the same id in both.
    /// </summary>
    public static byte[] MakeLinkedSetup(int dimensions, int bodies, uint seed, int role, int body, int step)
    {
        var setup = new byte[LinkedSetupLength];
        MakeSetup(dimensions, bodies, seed).CopyTo(setup, 0);
        setup[6] = (byte)role;
        setup[7] = (byte)body;
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(8), (ushort)step);
        ValidateSetup(setup, "setup");
        return setup;
    }

    /// <summary>
    /// Builds a setup for a world that declares the two scalar fields and their overlays (a plain setup does not, so the shell's existing views are unchanged).
    /// </summary>
    public static byte[] MakeFieldSetup(int dimensions, int bodies, uint seed)
    {
        var setup = new byte[LinkedSetupLength];
        MakeSetup(dimensions, bodies, seed).CopyTo(setup, 0);
        setup[6] = 3;
        ValidateSetup(setup, "setup");
        return setup;
    }

    internal static void ValidateSetup(ReadOnlySpan<byte> setup, string name)
    {
        if (setup.Length == LinkedSetupLength)
        {
            if (setup[6] is not (1 or 2 or 3) || (setup[6] != 3 && setup[7] >= setup[1]))
            {
                throw new ScopeFormatException(name, $"expected a role 1, 2 or 3 and, for roles 1 and 2, a body below the body count at offset 6, found role {setup[6]}, body {setup[7]}.");
            }
        }
        else if (setup.Length != SetupLength)
        {
            throw new ScopeFormatException(name, $"expected a setup of {SetupLength} or {LinkedSetupLength} bytes, found {setup.Length}.");
        }

        if (setup[0] is not (2 or 3))
        {
            throw new ScopeFormatException(name, $"expected dimensions 2 or 3 in the setup, found {setup[0]}.");
        }

        if (setup[1] is < 1 or > 64)
        {
            throw new ScopeFormatException(name, $"expected 1 to 64 bodies in the setup, found {setup[1]}.");
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int pos, string name, string what)
    {
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(Take(s, ref pos, 2, name, what));
        return v;
    }

    private static uint ReadU32(ReadOnlySpan<byte> s, ref int pos, string name, string what) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Take(s, ref pos, 4, name, what));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> s, ref int pos, int n, string name, string what)
    {
        if (n > s.Length - pos)
        {
            throw new ScopeFormatException(name, $"expected {n} bytes for {what} at offset {pos}, only {s.Length - pos} remain.");
        }

        ReadOnlySpan<byte> r = s.Slice(pos, n);
        pos += n;
        return r;
    }
}
