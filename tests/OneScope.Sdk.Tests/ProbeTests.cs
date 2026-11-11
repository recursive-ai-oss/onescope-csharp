using System.Buffers;
using System.Diagnostics;
using OneScope.Probe;
using Xunit;

namespace OneScope.Sdk.Tests;

public class ProbeTests
{
    private sealed class NoSnapshot : ISnapshotSource
    {
        public void WriteSnapshot(IBufferWriter<byte> writer)
        {
        }
    }

    [Fact]
    public void WithNobodyConnectedRecordingCostsNothingAndAllocatesNothing()
    {
        using ScopeProbe probe = ScopeProbe.Attach(new ProbeInfo("test", "1.0", 1), new NoSnapshot());
        Assert.False(probe.IsConnected);
        byte[] record = new byte[64];
        for (int i = 0; i < 1000; i++)
        {
            probe.Record(record);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int calls = 50_000_000;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < calls; i++)
        {
            probe.Record(record);
        }

        double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / calls;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Fixtures.Report($"probe unconnected Record (indicative, shared host, Release): {ns:F2} ns/call over {calls} calls, {allocated} bytes allocated");
    }

    [Fact]
    public void AttachChecksArgumentsAndDisposeIsIdempotent()
    {
        Assert.Throws<ArgumentNullException>(() => ScopeProbe.Attach(new ProbeInfo("t", "1", 1), null!));
        Assert.Throws<ArgumentNullException>(() => ScopeProbe.Attach(new ProbeInfo(null!, "1", 1), new NoSnapshot()));
        ScopeProbe p = ScopeProbe.Attach(new ProbeInfo("t", "1", 1), new NoSnapshot());
        p.Dispose();
        p.Dispose();
        Assert.False(p.IsConnected);
    }
}
