using System.Text.Json;
using OneScope.Probe;
using RecursiveAI.OneMath;
using RecursiveAI.OnePhysics;
using RecursiveAI.OnePhysics.Physics2D;
using RecursiveAI.OnePhysics.Physics3D;
using RecursiveAI.OnePhysics.Scope;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>A OnePhysics world stepping on its own thread with the module's live feed behind a probe, as a host game would have it.</summary>
internal sealed class PhysicsHostThread : IDisposable
{
    private const float Dt = 1f / 60f;
    private readonly Thread _thread;
    private readonly ScopeProbe _probe;
    private volatile bool _stop;

    public PhysicsHostThread(bool three, int steps, string dir)
    {
        Hashes = new Dictionary<long, ulong>();
        if (three)
        {
            var world = new PhysicsWorld3D(new WorldOptions3D { WorldId = 5 });
            world.AddBox(new Vector3F(40, 1, 40), new Vector3D(0, -0.5, 0), type: BodyType.Static);
            for (int i = 0; i < 3; i++)
            {
                world.AddBox(new Vector3F(1, 1, 1), new Vector3D(0, 0.5 + (i * 1.02), 0));
            }

            var feed = new LiveFeedStream(world);
            _probe = Start(feed, dir, () => (world.StepCount, world.ComputeHash()));
            _thread = new Thread(() => Run(steps, i =>
            {
                if (i % 40 == 10)
                {
                    world.AddSphere(0.4f, new Vector3D(2 + (i % 3), 5, 0));
                }
            }, world.Step, () => (world.StepCount, world.ComputeHash()), () => world.StartRecording(feed))) { IsBackground = true };
        }
        else
        {
            var world = new PhysicsWorld2D(new WorldOptions2D { WorldId = 5 });
            world.AddBox(new Vector2F(40, 1), new Vector2D(0, -0.5), type: BodyType.Static);
            for (int i = 0; i < 3; i++)
            {
                world.AddBox(new Vector2F(1, 1), new Vector2D(0, 0.5 + (i * 1.02)));
            }

            var feed = new LiveFeedStream(world);
            _probe = Start(feed, dir, () => (world.StepCount, world.ComputeHash()));
            _thread = new Thread(() => Run(steps, i =>
            {
                if (i % 40 == 10)
                {
                    world.AddCircle(0.4f, new Vector2D(2 + (i % 3), 5));
                }
            }, world.Step, () => (world.StepCount, world.ComputeHash()), () => world.StartRecording(feed))) { IsBackground = true };
        }
    }

    /// <summary>Host world hash by the host's step count.</summary>
    public Dictionary<long, ulong> Hashes { get; }

    public string SocketPath => _probe.SocketPath!;

    /// <summary>
    /// The host's step count and hash when a viewer's snapshot was taken, or null before one was. The host adds bodies before some steps, and a
    /// snapshot is taken at the first recorded change after the viewer asks, so it can already hold that body: the state the viewer starts from is
    /// then not the one in <see cref="Hashes"/> for that step.
    /// </summary>
    public (long Step, ulong Hash)? Snapshot { get; private set; }

    /// <summary>The state a viewer should have at host step <paramref name="step"/>; see <see cref="Snapshot"/>.</summary>
    public ulong ExpectedAt(long step) => Snapshot is { } snap && snap.Step == step ? snap.Hash : Hashes[step];

    private ScopeProbe Start(LiveFeedStream feed, string dir, Func<(long Count, ulong Hash)> state)
    {
        ScopeProbe p = ScopeProbe.Attach(new ProbeInfo(new OnePhysicsModule().Id, "1.0.0", 2), new FeedSnapshots(this, feed, state), new ProbeOptions { Directory = dir });
        feed.Record = p.Record;
        return p;
    }

    private void Run(int steps, Action<int> mutate, Action<float> step, Func<(long Count, ulong Hash)> state, Func<IDisposable> record)
    {
        using IDisposable recording = record();
        for (int i = 0; i < steps && !_stop; i++)
        {
            mutate(i);
            step(Dt);
            _probe.Poll();
            (long count, ulong hash) = state();
            lock (Hashes)
            {
                Hashes[count] = hash;
            }

            Thread.Sleep(5);
        }
    }

    public void Start() => _thread.Start();

    public void Join() => _thread.Join();

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _probe.Dispose();
    }

    private sealed class FeedSnapshots(PhysicsHostThread host, LiveFeedStream feed, Func<(long Count, ulong Hash)> state) : ISnapshotSource
    {
        public void WriteSnapshot(System.Buffers.IBufferWriter<byte> writer)
        {
            feed.WriteSnapshot(writer);
            (long count, ulong hash) = state();
            host.Snapshot = (count, hash);
        }
    }
}

/// <summary>Live attach to a real OnePhysics host: the shell's hashes equal the host's at the host's step numbers.</summary>
[Collection("shell")]
public sealed class PhysicsLiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachingMidRunGivesTheHostsHashesAtTheHostsStepNumbersAndForksFromTheLiveHistory(bool three)
    {
        // A short path: Unix socket paths are limited to about a hundred bytes, and macOS temp paths are long.
        string dir = LiveAndSessionTests.NewDir();
        try
        {
            using var host = new PhysicsHostThread(three, 300, dir);
            using Harness h = new Harness(bundled: true);
            host.Start();
            // Wait for the host to be stepping, not for a fixed time: building a 3D world can take longer than that on a busy machine.
            LiveAndSessionTests.WaitFor(() => { lock (host.Hashes) { return host.Hashes.Count >= 20; } }, "the host to step");
            JsonElement attached = await Task.Run(() => h.Call("attach", JsonSerializer.Serialize(new { socket = host.SocketPath, wait = 10000 })), TestContext.Current.CancellationToken);
            long start = attached.GetProperty("startStep").GetInt64();
            Assert.True(start > 1, "the attach should land mid-run");
            Assert.Equal(three ? 3 : 2, attached.GetProperty("dimensions").GetInt32());
            await Task.Run(host.Join, TestContext.Current.CancellationToken);
            h.Call("sources");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            JsonElement info = h.Call("sources").GetProperty("sources")[0];
            long end = info.GetProperty("steps").GetInt64();
            Assert.Equal(300, end);

            int compared = 0;
            for (long s = start; s <= end; s++)
            {
                h.Call("seek", $$"""{"step":{{s}}}""");
                string shell = h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString()!;
                Assert.Equal(host.ExpectedAt(s).ToString("x16", System.Globalization.CultureInfo.InvariantCulture), shell);
                compared++;
            }

            // A fork from the middle of the live history reproduces itself from its saved replay.
            long mid = (start + end) / 2;
            h.Call("seek", $$"""{"step":{{mid}}}""");
            int branch = h.Call("fork").GetProperty("branch").GetInt32();
            long body = h.Call("entities").GetProperty("entities").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "body").Select(e => e.GetProperty("id").GetInt64()).ElementAt(2);
            h.Call("poke", $$"""{"source":{{branch}},"poke":"impulse","args":{"target":{{body}},"impulse":[0,5,0]},"step":true}""");
            h.Call("step", $$"""{"source":{{branch}},"delta":5}""");
            long forkEnd = h.Call("sources").GetProperty("sources")[branch].GetProperty("steps").GetInt64();
            var forkHashes = new List<string>();
            for (long s = start; s <= forkEnd; s++)
            {
                h.Call("seek", $$"""{"step":{{s}}}""");
                forkHashes.Add(h.Call("sources").GetProperty("sources")[branch].GetProperty("hash").GetString()!);
            }

            string path = h.Call("replay_save", $$"""{"source":{{branch}},"path":"live-fork.oppr"}""").GetProperty("path").GetString()!;
            int again = h.Call("open", JsonSerializer.Serialize(new { path })).GetProperty("source").GetInt32();
            for (long s = start; s <= forkEnd; s++)
            {
                h.Call("seek", $$"""{"step":{{s}}}""");
                Assert.Equal(forkHashes[(int)(s - start)], h.Call("sources").GetProperty("sources")[again].GetProperty("hash").GetString());
            }

            // The host's own hashes are untouched by anything the viewer did.
            Assert.Equal(300, host.Hashes.Count(kv => kv.Key >= 1));
            Harness.Report($"live {(three ? "3D" : "2D")}: attached at host step {start}, {compared} steps ({start}..{end}) equal the host's hashes; fork at {mid} poked, saved and reopened: {forkHashes.Count} hashes reproduced");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
