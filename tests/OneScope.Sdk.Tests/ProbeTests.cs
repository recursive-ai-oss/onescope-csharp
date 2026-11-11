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

        // A real allocation on the path shows in every round (24 bytes per call or more); see Fixtures.SmallestAllocation.
        const int calls = 2_000_000;
        double ns = 0;
        long smallest = Fixtures.SmallestAllocation(() =>
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++)
            {
                probe.Record(record);
            }

            ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / calls;
        });

        Assert.Equal(0, smallest);
        Fixtures.Report($"probe unconnected Record (indicative, shared host): {ns:F2} ns/call over {calls} calls, {smallest} bytes allocated");
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
