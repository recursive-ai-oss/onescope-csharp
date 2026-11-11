using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class PoseCacheTests
{
    private static byte[] Ghost() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-2d-ghost.ospc"));

    private static ScopeFormatException Rejects(byte[] data, string name = "hostile.ospc")
    {
        var ex = Assert.Throws<ScopeFormatException>(() => PoseCache.Parse(data, name));
        Assert.StartsWith(name + ": expected", ex.Message);
        return ex;
    }

    private static byte[] Write(PoseCache cache)
    {
        var w = new ArrayBufferWriter<byte>();
        cache.Write(w);
        return w.WrittenSpan.ToArray();
    }

    private static PoseCache Tiny()
    {
        var info = new PoseCacheInfo(3, LengthUnit.Metre, 7, "mod", "1.2", "scene", "replay.bin", "label");
        PoseEntity[] entities =
        [
            new(new EntityId(5), "a", PoseShape.Box, new Vector3(1, 2, 3)),
            new(new EntityId(9), "bé", PoseShape.Capsule, new Vector3(0.5f, 1, 0)),
        ];
        float[] poses =
        [
            1, 2, 3, 0, 0, 0, 1, 4, 5, 6, 0, 0, 0, 1,
            float.NaN, 0, 0, 0, 0, 0, 0, 7, 8, 9, 0, 0, 0, 1,
        ];
        return PoseCache.Create(info, entities, poses);
    }

    [Fact]
    public void ACacheRoundTripsToTheSameBytesAndKeepsAbsentPoses()
    {
        PoseCache c = Tiny();
        byte[] bytes = Write(c);
        PoseCache back = PoseCache.Parse(bytes, "t");
        Assert.Equal(bytes, Write(back));
        Assert.Equal(2, back.FrameCount);
        Assert.Equal(7, back.StartStep);
        Assert.Equal("bé", back.Entities[1].Name);
        Assert.True(back.TryGetPose(1, 1, out Vector3 p, out Quaternion q));
        Assert.Equal(new Vector3(7, 8, 9), p);
        Assert.Equal(Quaternion.Identity, q);
        Assert.False(back.TryGetPose(1, 0, out _, out _));
        Assert.False(back.TryGetPose(2, 0, out _, out _));
        Assert.Equal("replay.bin", back.Info.Replay);
        Assert.Equal(Write(back), Write(PoseCache.Parse(Write(back), "again")));
    }

    [Fact]
    public void TheCommittedGhostParsesAndMatchesItsReplay()
    {
        PoseCache c = PoseCache.Parse(Ghost(), "ghost");
        Assert.Equal(40, c.FrameCount);
        Assert.Equal(200, c.StartStep);
        Assert.Equal(12, c.Entities.Count);
        Assert.Equal("test-2d-600.ostm", c.Info.Replay);
        Assert.Equal(TestFormat.ModuleId, c.Info.ModuleId);
    }

    [Fact]
    public void EveryTruncationIsATypedError()
    {
        byte[] full = Write(Tiny());
        for (int len = 0; len < full.Length; len++)
        {
            Rejects(full[..len]);
        }

        byte[] ghost = Ghost();
        for (int len = 0; len < 600; len++)
        {
            Rejects(ghost[..len]);
        }

        Rejects(ghost[..^1]);
        Rejects([.. ghost, 0]);
    }

    [Fact]
    public void HostileHeadersAreNamed()
    {
        byte[] good = Write(Tiny());
        byte[] Patched(Action<byte[]> edit)
        {
            byte[] b = (byte[])good.Clone();
            edit(b);
            return b;
        }

        Assert.Contains("magic", Rejects(Patched(b => b[0] = (byte)'X')).Message);
        Assert.Contains("version", Rejects(Patched(b => b[4] = 9)).Message);
        Assert.Contains("flags", Rejects(Patched(b => b[6] = 1)).Message);
        Assert.Contains("dimensions", Rejects(Patched(b => b[8] = 4)).Message);
        Assert.Contains("entities", Rejects(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), uint.MaxValue))).Message);
        Assert.Contains("entities", Rejects(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 0))).Message);
        Assert.Contains("frames", Rejects(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), uint.MaxValue))).Message);
        Assert.Contains("start step", Rejects(Patched(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(20), -1))).Message);
        Assert.Contains("metres", Rejects(Patched(b => BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(28), double.NaN))).Message);
        Assert.Contains("metres", Rejects(Patched(b => BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(28), -1))).Message);
        // A frame count the bytes cannot hold is caught before any pose array exists.
        Assert.Contains("poses", Rejects(Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), PoseCache.MaxFrames))).Message);
        // The unit name's length claims more than remains.
        Assert.Contains("unit name", Rejects(Patched(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(36), 0xFFFF))).Message);
        Assert.Contains("UTF-8", Rejects(Patched(b => { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(36), 1); b[38] = 0xFF; })).Message);
    }

    [Fact]
    public void BadEntitiesAndPosesAreNamed()
    {
        PoseCache c = Tiny();
        byte[] good = Write(c);
        int firstEntity = IndexOfEntityTable(good);

        byte[] dup = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(dup.AsSpan(firstEntity), 9); // the first entity takes the second's id
        Assert.Contains("unique", Rejects(dup).Message);

        byte[] zero = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(zero.AsSpan(firstEntity), 0);
        Assert.Contains("unique", Rejects(zero).Message);

        byte[] shape = (byte[])good.Clone();
        shape[firstEntity + 8] = 7;
        Assert.Contains("shape", Rejects(shape).Message);

        byte[] size = (byte[])good.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(size.AsSpan(firstEntity + 12), float.PositiveInfinity);
        Assert.Contains("finite", Rejects(size).Message);

        byte[] inf = (byte[])good.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(inf.AsSpan(inf.Length - (4 * 28)), float.PositiveInfinity);
        Assert.Contains("finite", Rejects(inf).Message);

        byte[] nanRotation = (byte[])good.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(nanRotation.AsSpan(nanRotation.Length - (4 * 28) + 12), float.NaN);
        Assert.Contains("finite", Rejects(nanRotation).Message);
    }

    private static int IndexOfEntityTable(byte[] bytes)
    {
        // Header 36 bytes, then six strings: "m" (unit), "mod", "1.2", "scene", "replay.bin", "label", each with a 2-byte length.
        int pos = 36;
        foreach (string s in new[] { "m", "mod", "1.2", "scene", "replay.bin", "label" })
        {
            pos += 2 + s.Length;
        }

        return pos;
    }

    [Fact]
    public void CreateRejectsWhatParseWouldAndNeverTrustsTheCaller()
    {
        var info = new PoseCacheInfo(2, LengthUnit.Metre, 0, "", "", "", "", "");
        PoseEntity[] one = [new(new EntityId(1), "a", PoseShape.Point, default)];
        Assert.Throws<ArgumentException>(() => PoseCache.Create(info, one, new float[6]));
        Assert.Throws<ArgumentException>(() => PoseCache.Create(info, one, []));
        Assert.Throws<ArgumentException>(() => PoseCache.Create(info, [], new float[7]));
        Assert.Throws<ArgumentException>(() => PoseCache.Create(info with { Dimensions = 5 }, one, new float[7]));
        Assert.Throws<ArgumentException>(() => PoseCache.Create(info, [one[0], one[0]], new float[14]));
        PoseCache ok = PoseCache.Create(info, one, new float[14]);
        Assert.Equal(2, ok.FrameCount);
    }

    [Fact]
    public void GarbageNeverThrowsAnythingButATypedError()
    {
        var rng = new Random(12345);
        byte[] seed = Ghost();
        for (int i = 0; i < 3000; i++)
        {
            byte[] b = (byte[])seed.Clone();
            int edits = 1 + rng.Next(4);
            for (int e = 0; e < edits; e++)
            {
                b[rng.Next(Math.Min(b.Length, 400))] = (byte)rng.Next(256);
            }

            try
            {
                PoseCache.Parse(b, "garbage");
            }
            catch (ScopeFormatException)
            {
            }
        }

        for (int i = 0; i < 500; i++)
        {
            var b = new byte[rng.Next(0, 200)];
            rng.NextBytes(b);
            if (b.Length > 4)
            {
                "OSPC"u8.CopyTo(b);
            }

            try
            {
                PoseCache.Parse(b, "garbage");
            }
            catch (ScopeFormatException)
            {
            }
        }
    }
}
