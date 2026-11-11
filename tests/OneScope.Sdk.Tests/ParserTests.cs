using System.Buffers.Binary;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class ParserTests
{
    private static byte[] Small() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-2d-600.ostm"));

    private static ScopeFormatException Rejects(byte[] data, string name = "hostile.ostm")
    {
        var ex = Assert.Throws<ScopeFormatException>(() => Fixtures.Module.Open(data, name));
        Assert.Equal(name, ex.Input);
        Assert.StartsWith(name + ": expected", ex.Message);
        return ex;
    }

    [Fact]
    public void TheCommittedFilesParse()
    {
        Assert.Equal(600, Fixtures.Module.Open(Small(), "small").Recording.StepCount);
        Assert.Equal(2, Fixtures.Module.Open(Small(), "small").Info.Dimensions);
        Assert.Equal(3, Fixtures.OpenLong().Info.Dimensions);
    }

    [Fact]
    public void EveryTruncationIsATypedError()
    {
        byte[] full = Small();
        for (int len = 0; len < full.Length; len++)
        {
            Rejects(full[..len]);
        }
    }

    [Fact]
    public void BadMagicVersionFlagsAndSetupAreNamed()
    {
        byte[] good = Small();
        byte[] bad = (byte[])good.Clone();
        bad[0] = (byte)'X';
        Assert.Contains("magic", Rejects(bad).Message);

        bad = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(4), 2);
        Assert.Contains("version 1", Rejects(bad).Message);

        bad = (byte[])good.Clone();
        bad[6] = 0x80;
        Assert.Contains("flags", Rejects(bad).Message);

        bad = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8), 0xFFFF_FFFF);
        Assert.Contains("setup length", Rejects(bad).Message);

        bad = (byte[])good.Clone();
        bad[12] = 5;
        Assert.Contains("dimensions", Rejects(bad).Message);

        bad = (byte[])good.Clone();
        bad[13] = 0;
        Assert.Contains("bodies", Rejects(bad).Message);

        Rejects([.. good, 0]);
    }

    [Fact]
    public void HostileLengthsFailBeforeAllocating()
    {
        byte[] good = Small();
        // Step count offset: 4+2+2+4+6 = 18.
        byte[] bad = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(18), uint.MaxValue);
        Assert.Contains("steps", Rejects(bad).Message);

        bad = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(18), TestFormat.MaxSteps);
        Assert.Contains("remain", Rejects(bad).Message);

        // An input length that runs past the end: header plus one step claiming 65535 bytes.
        byte[] tiny = [.. "OSTM"u8, 1, 0, 0, 0, 6, 0, 0, 0, 3, 4, 0, 0, 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 1, 2];
        Assert.Contains("65535", Rejects(tiny).Message);
    }

    [Fact]
    public void RandomGarbageNeverThrowsAnythingElse()
    {
        var rng = new Random(99);
        byte[] good = Small();
        for (int i = 0; i < 3000; i++)
        {
            byte[] data = (byte[])good.Clone();
            int flips = 1 + rng.Next(6);
            for (int f = 0; f < flips; f++)
            {
                data[rng.Next(Math.Min(data.Length, i % 2 == 0 ? 40 : data.Length))] = (byte)rng.Next(256);
            }

            try
            {
                IScopeSource source = Fixtures.Module.Open(data, "g");
                Assert.True(source.Recording.StepCount <= TestFormat.MaxSteps);
            }
            catch (ScopeFormatException)
            {
            }
        }

        var pure = new byte[200];
        for (int i = 0; i < 500; i++)
        {
            rng.NextBytes(pure);
            Assert.Throws<ScopeFormatException>(() => Fixtures.Module.Open(pure, "noise"));
        }
    }

    [Fact]
    public void AMalformedStepInputIsATypedErrorWhenRun()
    {
        IScopeWorld w = Fixtures.OpenLong().CreateWorld();
        Assert.Throws<ScopeFormatException>(() => w.Advance(new byte[] { 9 }, TimelineSink.None));
        Assert.Throws<ScopeFormatException>(() => w.Advance(new byte[] { 1, 0, 0 }, TimelineSink.None));
        Assert.Throws<ScopeFormatException>(() => w.Advance(new byte[] { 3, 200 }, TimelineSink.None));
    }
}
