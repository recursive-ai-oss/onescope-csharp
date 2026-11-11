using System.Net.Sockets;
using OneScope.Attach;
using OneScope.Probe;
using OneScope.Sdk;
using Xunit;

namespace OneScope.Attach.Tests;

public class LiveTests
{
    private const int Steps = 4000;

    private static LiveSession Connect(string socket) => LiveSession.Connect(socket, [Support.Module]);

    /// <summary>Pumps until the host has closed its probe and every record has been taken.</summary>
    private static void PumpToEnd(LiveSession session)
    {
        Support.WaitFor(() =>
        {
            int n = session.Pump();
            if (n == 0 && session.State != ClientState.Streaming)
            {
                return session.Pump() == 0;
            }

            return false;
        }, "the stream to end");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewerAttachedAtStartOrMidRunMatchesTheHostHashForHash(bool atStart)
    {
        string dir = Support.NewSocketDir();
        var host = new LiveHost();
        ProbeInfo info = Support.Info;
        var probe = ScopeProbe.Attach(info, host, new ProbeOptions { Directory = dir });
        Assert.True(probe.IsListening);
        LiveSession? session = null;
        Task connect;

        if (atStart)
        {
            connect = Task.Run(() => session = Connect(probe.SocketPath!));
            // Poll gives a waiting viewer the world as it is, before any step.
            Support.WaitFor(() =>
            {
                if (connect.IsFaulted)
                {
                    throw connect.Exception!;
                }

                probe.Poll();
                return probe.IsConnected;
            }, "the viewer to attach at step 0 (drop: " + probe.LastDropReason + ")");
            host.Run(probe, Steps);
        }
        else
        {
            // Run to step 1500 at full speed, then slowly until the viewer has its snapshot: a mid-run attach.
            host.Run(probe, 1500);
            connect = Task.Run(() => session = Connect(probe.SocketPath!));
            while (!probe.IsConnected)
            {
                host.Run(probe, host.Steps + 1);
                Thread.Sleep(1);
            }

            host.Run(probe, Steps);
        }

        probe.Dispose();
        await connect;
        using LiveSession viewer = session!;
        PumpToEnd(viewer);

        Assert.Equal(ClientState.Ended, viewer.State);
        long baseStep = viewer.BaseStep;
        if (atStart)
        {
            Assert.Equal(0, baseStep);
        }
        else
        {
            Assert.True(baseStep >= 1500, $"base step {baseStep}");
        }

        Assert.Equal(host.Steps - baseStep, viewer.History.StepCount);

        var lines = new List<string> { $"live attach {(atStart ? "at step 0" : "mid-run")}: base step {baseStep}, {viewer.History.StepCount} viewer steps; host hash vs viewer hash" };
        for (long j = 0; j <= viewer.History.StepCount; j++)
        {
            viewer.History.Seek(j);
            ulong local = viewer.History.StateHash;
            Assert.Equal(host.Hashes[baseStep + j], local);
            if (j % 500 == 0 || j == viewer.History.StepCount)
            {
                lines.Add($"  host step {baseStep + j,5}  host {host.Hashes[baseStep + j]:x16}  viewer step {j,5}  viewer {local:x16}");
            }
        }

        foreach (string l in lines)
        {
            Support.Report(l);
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ViewerCanScrubAndForkTheLiveStreamLocally()
    {
        string dir = Support.NewSocketDir();
        var host = new LiveHost();
        var probe = ScopeProbe.Attach(Support.Info, host, new ProbeOptions { Directory = dir });
        LiveSession? session = null;
        Task connect = Task.Run(() => session = Connect(probe.SocketPath!));
        Support.WaitFor(() =>
        {
            probe.Poll();
            return probe.IsConnected;
        }, "attach");
        host.Run(probe, 1000);
        probe.Dispose();
        await connect;
        using LiveSession viewer = session!;
        PumpToEnd(viewer);

        History h = viewer.History;
        h.Seek(900);
        Branch branch = h.Fork(500);
        Assert.Equal(host.Hashes[500], branch.History.StateHash);
        branch.Poke(new PokeCall("set_gravity", [ScopeValue.OfInteger(-9)]));
        Assert.True(branch.Step());
        Assert.NotEqual(host.Hashes[501], branch.History.StateHash);
        Assert.Equal(500L, branch.DivergenceStep);

        // A live source writes a replay that reopens to the same hashes (it starts from the stream's snapshot).
        byte[] replay = Support.Write(h.Source);
        IScopeSource reopened = Support.Module.Open(replay, "live.ostm");
        Assert.Equal(replay, Support.Write(reopened));
        var again = new History(reopened);
        again.Seek(700);
        Assert.Equal(host.Hashes[700], again.StateHash);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ReconnectingMidRunStartsFromAFreshSnapshot()
    {
        string dir = Support.NewSocketDir();
        var host = new LiveHost();
        var probe = ScopeProbe.Attach(Support.Info, host, new ProbeOptions { Directory = dir });
        LiveSession? first = null;
        Task c1 = Task.Run(() => first = Connect(probe.SocketPath!));
        Support.WaitFor(() =>
        {
            probe.Poll();
            return probe.IsConnected;
        }, "first attach");
        host.Run(probe, 1000);
        await c1;
        first!.Dispose();

        // The host notices the viewer is gone on a later record; run on, then attach again.
        host.Run(probe, 1500);
        Support.WaitFor(() =>
        {
            host.Run(probe, host.Steps + 1);
            return !probe.IsConnected;
        }, "the first viewer to be dropped");

        LiveSession? second = null;
        Task c2 = Task.Run(() => second = Connect(probe.SocketPath!));
        Support.WaitFor(() =>
        {
            host.Run(probe, host.Steps + 1);
            return probe.IsConnected;
        }, "second attach");
        host.Run(probe, 3000);
        probe.Dispose();
        await c2;
        using LiveSession viewer = second!;
        PumpToEnd(viewer);
        Assert.True(viewer.BaseStep > 1000);
        for (long j = 0; j <= viewer.History.StepCount; j += 7)
        {
            viewer.History.Seek(j);
            Assert.Equal(host.Hashes[viewer.BaseStep + j], viewer.History.StateHash);
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ABadlyBehavedViewerCannotChangeTheHostOrItsStream()
    {
        string dir = Support.NewSocketDir();
        var host = new LiveHost();
        var probe = ScopeProbe.Attach(Support.Info, host, new ProbeOptions { Directory = dir });

        // A client that sends the wrong command, and one that sends a flood after attaching.
        using (var wrong = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            wrong.Connect(new UnixDomainSocketEndPoint(probe.SocketPath!));
            wrong.Send([42]);
            byte[] buf = new byte[4096];
            int total = 0;
            int n;
            while ((n = wrong.Receive(buf)) > 0)
            {
                total += n;
            }

            Assert.Equal(ProbeWire.HandshakeLength(Support.Info), total);
        }

        using var flood = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        flood.Connect(new UnixDomainSocketEndPoint(probe.SocketPath!));
        byte[] junk = new byte[8192];
        Array.Fill(junk, (byte)0xAB);
        junk[0] = ProbeWire.AttachCommand;
        flood.Send(junk);
        Support.WaitFor(() =>
        {
            probe.Poll();
            return probe.IsConnected;
        }, "the flooding viewer to attach");
        host.Run(probe, 2000);
        probe.Dispose();

        // The host's hashes equal a run with no probe at all.
        var plain = new LiveHost();
        plain.Run(null, 2000);
        Assert.Equal(plain.Hashes[..2001], host.Hashes[..2001]);
        Directory.Delete(dir, true);
    }
}
