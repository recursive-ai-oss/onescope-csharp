using System.Buffers.Binary;
using System.Net.Sockets;
using OneScope.Attach;
using OneScope.Probe;
using Xunit;

namespace OneScope.Attach.Tests;

public class WireTests
{
    private static byte[] Hello(ProbeInfo info, int pid = 4242)
    {
        byte[] b = new byte[ProbeWire.HandshakeLength(info)];
        ProbeWire.WriteHandshake(b, info, pid);
        return b;
    }

    [Fact]
    public void HandshakeRoundTrips()
    {
        var info = new ProbeInfo("onephysics", "1.0.0-rc.1", 7);
        byte[] bytes = Hello(info);
        ProbeHandshake parsed = ProbeWire.ParseHandshake(bytes, "test");
        Assert.Equal(info, parsed.Info);
        Assert.Equal(4242, parsed.ProcessId);
        Assert.Equal(20 + 10 + 10, bytes.Length);
    }

    [Fact]
    public void HandshakeRejectsEveryTruncationAndTrailingBytes()
    {
        byte[] bytes = Hello(new ProbeInfo("onephysics", "1.0.0", 7));
        for (int n = 0; n < bytes.Length; n++)
        {
            ProbeFormatException e = Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(bytes.AsSpan(0, n), "sock"));
            Assert.StartsWith("sock: ", e.Message);
        }

        Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake([.. bytes, 0], "sock"));
    }

    [Fact]
    public void HandshakeRejectsBadMagicVersionFlagsLengthsAndText()
    {
        byte[] good = Hello(new ProbeInfo("m", "1", 1));
        byte[] Mutate(Action<byte[]> edit)
        {
            byte[] c = (byte[])good.Clone();
            edit(c);
            return c;
        }

        Assert.Contains("magic", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => b[0] = (byte)'X'), "s")).Message);
        Assert.Contains("version 1", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => b[4] = 2), "s")).Message);
        Assert.Contains("flags", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => b[6] = 1), "s")).Message);
        Assert.Contains("module id", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => b[16] = 0), "s")).Message);
        Assert.Contains("module id", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 65535)), "s")).Message);
        Assert.Contains("library version", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), 4000)), "s")).Message);
        Assert.Contains("UTF-8", Assert.Throws<ProbeFormatException>(() => ProbeWire.ParseHandshake(Mutate(b => b[20] = 0xFF), "s")).Message);
        Assert.Throws<ArgumentException>(() => ProbeWire.HandshakeLength(new ProbeInfo(new string('a', 257), "1", 1)));
    }

    [Fact]
    public void FrameHeadersAreCheckedAgainstTheLimitsBeforeAnythingIsAllocated()
    {
        var limits = new ProbeLimits(1000, 100);
        byte[] h = new byte[5];
        ProbeWire.WriteFrameHeader(h, ProbeFrameKind.Snapshot, 1000);
        Assert.Equal((ProbeFrameKind.Snapshot, 1000), ProbeWire.ReadFrameHeader(h, limits, true, "s"));
        ProbeWire.WriteFrameHeader(h, ProbeFrameKind.Snapshot, 1001);
        Assert.Contains("at most 1000", Assert.Throws<ProbeFormatException>(() => ProbeWire.ReadFrameHeader(h, limits, true, "s")).Message);
        ProbeWire.WriteFrameHeader(h, ProbeFrameKind.Record, int.MaxValue);
        Assert.Contains("at most 100", Assert.Throws<ProbeFormatException>(() => ProbeWire.ReadFrameHeader(h, limits, false, "s")).Message);
        Assert.Contains("snapshot frame", Assert.Throws<ProbeFormatException>(() => ProbeWire.ReadFrameHeader(h, limits, true, "s")).Message);
        ProbeWire.WriteFrameHeader(h, ProbeFrameKind.End, 5);
        Assert.Contains("length 0", Assert.Throws<ProbeFormatException>(() => ProbeWire.ReadFrameHeader(h, limits, false, "s")).Message);
        h[0] = 9;
        Assert.Contains("kind 9", Assert.Throws<ProbeFormatException>(() => ProbeWire.ReadFrameHeader(h, limits, false, "s")).Message);
        Assert.Throws<ProbeFormatException>(() => ProbeWire.ReadFrameHeader(h.AsSpan(0, 4), limits, false, "s"));
    }

    [Fact]
    public void RandomBytesOnlyEverGiveTypedErrors()
    {
        var rng = new Random(12345);
        byte[] seed = Hello(new ProbeInfo("onephysics", "1.0.0", 7));
        for (int i = 0; i < 20000; i++)
        {
            byte[] b = i % 2 == 0 ? rng.GetItems(seed, rng.Next(0, 80)) : (byte[])seed.Clone();
            if (i % 2 == 1)
            {
                b[rng.Next(b.Length)] = (byte)rng.Next(256);
            }

            try
            {
                _ = ProbeWire.ParseHandshake(b, "fuzz");
            }
            catch (ProbeFormatException)
            {
                // Expected.
            }

            try
            {
                _ = ProbeWire.ReadFrameHeader(b.AsSpan(0, Math.Min(b.Length, 5)), ProbeLimits.Default, i % 3 == 0, "fuzz");
            }
            catch (ProbeFormatException)
            {
                // Expected.
            }
        }
    }

    /// <summary>A fake probe: accepts one connection, sends the given bytes after the handshake, closes.</summary>
    private static string Serve(string dir, byte[] handshake, byte[]? afterAttach, out Task done)
    {
        string path = Path.Combine(dir, "fake.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        done = Task.Run(() =>
        {
            using Socket c = listener.Accept();
            c.Send(handshake);
            if (afterAttach is not null)
            {
                c.Receive(new byte[1]);
                c.Send(afterAttach);
            }

            Thread.Sleep(50);
            listener.Dispose();
        });
        return path;
    }

    [Fact]
    public async Task ClientRejectsMalformedPeersWithTypedErrors()
    {
        string dir = Support.NewSocketDir();
        byte[] good = Hello(new ProbeInfo("m", "1", 1));

        string p1 = Serve(dir, "garbage garbage garbage garbage"u8.ToArray(), null, out Task t1);
        Assert.Contains("magic", Assert.Throws<ProbeFormatException>(() => ProbeClient.Connect(p1, null, 2000)).Message);
        await t1;
        File.Delete(p1);

        string p2 = Serve(dir, good[..12], null, out Task t2);
        Assert.Contains("closed", Assert.Throws<ProbeFormatException>(() => ProbeClient.Connect(p2, null, 2000)).Message);
        await t2;
        File.Delete(p2);

        byte[] huge = new byte[5];
        ProbeWire.WriteFrameHeader(huge, ProbeFrameKind.Snapshot, int.MaxValue);
        string p3 = Serve(dir, good, huge, out Task t3);
        Assert.Contains("at most 67108864", Assert.Throws<ProbeFormatException>(() => ProbeClient.Connect(p3, null, 2000)).Message);
        await t3;
        File.Delete(p3);

        byte[] shortSnap = new byte[5 + 3];
        ProbeWire.WriteFrameHeader(shortSnap, ProbeFrameKind.Snapshot, 100);
        string p4 = Serve(dir, good, shortSnap, out Task t4);
        Assert.Contains("snapshot", Assert.Throws<ProbeFormatException>(() => ProbeClient.Connect(p4, null, 2000)).Message);
        await t4;
        File.Delete(p4);

        // A bad frame after a good snapshot ends the stream as Failed; the records before it are still delivered.
        byte[] stream = new byte[5 + 2 + 5 + 1 + 5];
        ProbeWire.WriteFrameHeader(stream, ProbeFrameKind.Snapshot, 2);
        ProbeWire.WriteFrameHeader(stream.AsSpan(7), ProbeFrameKind.Record, 1);
        stream[12] = 9;
        ProbeWire.WriteFrameHeader(stream.AsSpan(13), ProbeFrameKind.Record, 1 << 30);
        string p5 = Serve(dir, good, stream, out Task t5);
        using ProbeClient client = ProbeClient.Connect(p5, null, 2000);
        Support.WaitFor(() => client.State != ClientState.Streaming, "the stream to fail");
        Assert.Equal(ClientState.Failed, client.State);
        Assert.Contains("at most 16777216", client.Error!.Message);
        var records = new List<byte[]>();
        Assert.Equal(1, client.Drain(records));
        Assert.Equal(new byte[] { 9 }, records[0]);
        await t5;
        Directory.Delete(dir, true);
    }
}
