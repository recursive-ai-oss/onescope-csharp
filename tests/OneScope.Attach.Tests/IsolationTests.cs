using System.Net.Sockets;
using OneScope.Attach;
using OneScope.Probe;
using Xunit;

namespace OneScope.Attach.Tests;

public class IsolationTests
{
    private const int Steps = 5000;

    [Fact]
    public async Task HostHashesAreIdenticalWithNoViewerAFastViewerAndOneThatLeavesMidRun()
    {
        string dir = Support.NewSocketDir();

        // 1. A probe with nobody attached.
        var none = new LiveHost();
        using (var p = ScopeProbe.Attach(Support.Info, none, new ProbeOptions { Directory = dir }))
        {
            none.Run(p, Steps);
        }

        // 2. A fast viewer attached from step 0, pumping the whole time.
        var fast = new LiveHost();
        using (var p = ScopeProbe.Attach(Support.Info, fast, new ProbeOptions { Directory = dir }))
        {
            LiveSession? viewer = null;
            Task connect = Task.Run(() => viewer = LiveSession.Connect(p.SocketPath!, [Support.Module]));
            Support.WaitFor(() =>
            {
                p.Poll();
                return p.IsConnected;
            }, "the fast viewer");
            fast.Run(p, Steps);
            await connect;
            viewer!.Dispose();
        }

        // 3. A viewer that disconnects at step 2000.
        var leaver = new LiveHost();
        using (var p = ScopeProbe.Attach(Support.Info, leaver, new ProbeOptions { Directory = dir }))
        {
            LiveSession? viewer = null;
            Task connect = Task.Run(() => viewer = LiveSession.Connect(p.SocketPath!, [Support.Module]));
            Support.WaitFor(() =>
            {
                p.Poll();
                return p.IsConnected;
            }, "the leaving viewer");
            await connect;
            leaver.Run(p, Steps, s =>
            {
                if (s == 2000)
                {
                    viewer!.Dispose();
                }
            });
        }

        Assert.Equal(none.Hashes, fast.Hashes);
        Assert.Equal(none.Hashes, leaver.Hashes);
        Support.Report("host isolation: world hash per host step, three runs of the same host");
        Support.Report("  step   no viewer         fast viewer       viewer leaves at 2000");
        for (int s = 0; s <= Steps; s += 500)
        {
            Support.Report($"  {s,5}  {none.Hashes[s]:x16}  {fast.Hashes[s]:x16}  {leaver.Hashes[s]:x16}");
        }

        Support.Report("host step time, indicative: no viewer: " + LiveHost.Stats(none.StepNanos, Steps));
        Support.Report("host step time, indicative: fast viewer: " + LiveHost.Stats(fast.StepNanos, Steps));
        Support.Report("host step time, indicative: viewer leaves: " + LiveHost.Stats(leaver.StepNanos, Steps));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void AViewerThatStopsReadingIsDroppedWithoutSlowingOrChangingTheHost()
    {
        string dir = Support.NewSocketDir();
        const int Big = 4096;
        byte[] pad = new byte[Big];

        var baseline = new LiveHost();
        baseline.Run(null, Steps);

        var host = new LiveHost();
        using var probe = ScopeProbe.Attach(Support.Info, host, new ProbeOptions { Directory = dir, MaxBufferedBytes = 256 * 1024, SendTimeoutMilliseconds = 60_000 });

        // A viewer that attaches properly, takes its snapshot, and then never reads again.
        using var stalled = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        stalled.Connect(new UnixDomainSocketEndPoint(probe.SocketPath!));
        stalled.ReceiveBufferSize = 4096;
        byte[] hello = new byte[ProbeWire.HandshakeLength(Support.Info)];
        int got = 0;
        while (got < hello.Length)
        {
            got += stalled.Receive(hello.AsSpan(got));
        }

        stalled.Send([ProbeWire.AttachCommand]);
        Support.WaitFor(() =>
        {
            probe.Poll();
            return probe.IsConnected;
        }, "the stalled viewer to be given its snapshot");

        host.Run(probe, Steps, padding: _ => pad);

        Assert.False(probe.IsConnected);
        Assert.Equal(1, probe.DroppedViewers);
        Assert.Contains("fell behind", probe.LastDropReason);
        Assert.Equal(baseline.Hashes, host.Hashes);
        Support.Report($"stalled viewer dropped after buffering at most 256 KiB: {probe.LastDropReason}");
        Support.Report("host step time, indicative, 4 KiB records, no probe: " + LiveHost.Stats(baseline.StepNanos, Steps));
        Support.Report("host step time, indicative, 4 KiB records, stalled viewer: " + LiveHost.Stats(host.StepNanos, Steps));
        Directory.Delete(dir, true);
    }
}
