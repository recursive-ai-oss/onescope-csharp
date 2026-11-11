using System.Globalization;
using System.Text.Json;
using OneScope.Probe;
using RecursiveAI.OneMath;
using RecursiveAI.OnePath;
using RecursiveAI.OnePath.Replay;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// A OnePath crowd stepping on its own thread, recorded by the library's CrowdRecorder into a probe, as a host game would have it.
/// The host has no reference to the module: it hands the probe the recorder's records and its snapshot.
/// </summary>
internal sealed class CrowdHostThread : IDisposable
{
    private const double Dt = 1.0 / 30.0;
    private readonly Thread _thread;
    private readonly ScopeProbe? _probe;
    private readonly CrowdRecorder _recorder;
    private readonly int _steps;
    private readonly int _delayMs;
    private volatile bool _stop;
    private ulong _rng = 9;

    public CrowdHostThread(int steps, string? dir, int delayMs = 2)
    {
        _steps = steps;
        _delayMs = delayMs;
        const int W = 40, H = 24;
        var cells = new bool[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                cells[(y * W) + x] = x != 20 || (y >= 10 && y < 14);
            }
        }

        NavMesh mesh = NavMesh.Build2D(cells, W, H, 1.0, new Vector2D(0, 0), NavSettings.Humanoid with { AgentRadius = 0.0, CellSize = 0.5, TileSize = 16 });
        _recorder = new CrowdRecorder(mesh.CreateCrowd(), Dt, keepSteps: false);
        if (dir is not null)
        {
            string libraryVersion = typeof(Crowd).Assembly.GetName().Version?.ToString(3) ?? "1";
            _probe = ScopeProbe.Attach(new ProbeInfo("onepath", libraryVersion, 1), new Snapshots(_recorder), new ProbeOptions { Directory = dir });
            _recorder.Record = _probe.Record;
        }

        _thread = new Thread(Run) { IsBackground = true };
    }

    /// <summary>Host world hash by the crowd's step count.</summary>
    public Dictionary<long, ulong> Hashes { get; } = [];

    public string SocketPath => _probe!.SocketPath!;

    private Vector3D Spot(int side)
    {
        _rng += 0x9E3779B97F4A7C15UL;
        ulong z = _rng;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return new Vector3D(side == 0 ? 1 + ((z % 1000) / 1000.0 * 17) : 22 + ((z % 1000) / 1000.0 * 17), 0, 1 + (((z >> 20) % 1000) / 1000.0 * 22));
    }

    private void Run()
    {
        var agents = new List<Agent>();
        for (int i = 0; i < 20; i++)
        {
            Agent a = _recorder.Add(Spot(0), Agent.Small);
            agents.Add(a);
            _recorder.SetDestination(a, Spot(1));
        }

        for (int i = 0; i < _steps && !_stop; i++)
        {
            if (i % 60 == 30)
            {
                for (int k = 0; k < 5; k++)
                {
                    _recorder.SetDestination(agents[(i + (k * 3)) % agents.Count], Spot((i / 60) % 2));
                }
            }

            if (i % 100 == 50)
            {
                _recorder.Remove(agents[1]);
                agents.RemoveAt(1);
                Agent a = _recorder.Add(Spot(0), Agent.Small);
                agents.Add(a);
                _recorder.SetDestination(a, Spot(1));
            }

            _recorder.Step();
            _probe?.Poll();
            lock (Hashes)
            {
                Hashes[_recorder.Crowd.StepCount] = _recorder.Crowd.StateHash();
            }

            if (_delayMs > 0)
            {
                Thread.Sleep(_delayMs);
            }
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

        _probe?.Dispose();
    }

    private sealed class Snapshots(CrowdRecorder recorder) : ISnapshotSource
    {
        public void WriteSnapshot(System.Buffers.IBufferWriter<byte> writer) => recorder.WriteSnapshot(writer);
    }
}

/// <summary>Live attach to a real OnePath crowd host: the shell's hashes equal the host's at the host's step numbers, and the host never notices the viewer.</summary>
[Collection("shell")]
public sealed class PathLiveTests
{
    private static string Hex(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);

    [Fact]
    public async Task AttachingMidRunGivesTheHostsHashesAtTheHostsStepNumbersAndForksFromTheLiveHistory()
    {
        string dir = LiveAndSessionTests.NewDir();
        try
        {
            using var host = new CrowdHostThread(400, dir);
            using Harness h = new Harness(bundled: true);
            host.Start();
            await Task.Delay(300, TestContext.Current.CancellationToken);
            JsonElement attached = await Task.Run(() => h.Call("attach", JsonSerializer.Serialize(new { socket = host.SocketPath, wait = 10000 })), TestContext.Current.CancellationToken);
            long start = attached.GetProperty("startStep").GetInt64();
            Assert.True(start > 1, "the attach should land mid-run");
            Assert.Equal("onepath", attached.GetProperty("module").GetString());
            await Task.Run(host.Join, TestContext.Current.CancellationToken);
            h.Call("sources");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            long end = h.Call("sources").GetProperty("sources")[0].GetProperty("steps").GetInt64();
            Assert.Equal(400, end);

            int compared = 0;
            var mismatches = new List<string>();
            foreach (long s in Enumerable.Range((int)start, (int)(end - start) + 1))
            {
                h.Call("seek", $$"""{"step":{{s}}}""");
                string viewer = h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString()!;
                if (Hex(host.Hashes[s]) != viewer)
                {
                    mismatches.Add($"step {s}: host {Hex(host.Hashes[s])} viewer {viewer} (host hash at s-1 {(host.Hashes.TryGetValue(s - 1, out ulong p1) ? Hex(p1) : "-")}, s+1 {(host.Hashes.TryGetValue(s + 1, out ulong n1) ? Hex(n1) : "-")})");
                }

                compared++;
            }

            Assert.True(mismatches.Count == 0, $"attached at host step {start}, {mismatches.Count} of {compared} steps differ; first: {string.Join(" | ", mismatches.Take(4))}");

            // Backwards across the keyframes as well.
            foreach (long s in new[] { end, end - 64, end - 1, start, end - 100, start + 1 })
            {
                h.Call("seek", $$"""{"step":{{s}}}""");
                Assert.Equal(Hex(host.Hashes[s]), h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString());
            }

            // A fork from the middle of the live history reproduces itself from its saved replay.
            long mid = (start + end) / 2;
            h.Call("seek", $$"""{"step":{{mid}}}""");
            int branch = h.Call("fork").GetProperty("branch").GetInt32();
            ulong agent = h.Call("entities", """{"kind":"agent"}""").GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetUInt64()).ElementAt(2);
            h.Call("poke", $$"""{"source":{{branch}},"poke":"set_target","args":{"agent":{{agent}},"target":[3,3,0]},"step":true}""");
            h.Call("step", $$"""{"source":{{branch}},"delta":5}""");
            long forkEnd = h.Call("sources").GetProperty("sources")[branch].GetProperty("steps").GetInt64();
            var forkHashes = new List<string>();
            for (long s = start; s <= forkEnd; s++)
            {
                forkHashes.Add(PathTests.ShellHash(h, s, branch));
            }

            string path = h.Call("replay_save", $$"""{"source":{{branch}},"path":"live-fork.oprp"}""").GetProperty("path").GetString()!;
            int again = h.Call("open", JsonSerializer.Serialize(new { path })).GetProperty("source").GetInt32();
            for (long s = start; s <= forkEnd; s++)
            {
                Assert.Equal(forkHashes[(int)(s - start)], PathTests.ShellHash(h, s, again));
            }

            Harness.Report($"live crowd: attached at host step {start}, {compared} steps ({start}..{end}) equal the host's hashes, plus 6 backward landings; fork at {mid} poked, saved and reopened: {forkHashes.Count} hashes reproduced");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HostHashesAreIdenticalWithNoViewerAViewerAndAViewerThatLeaves()
    {
        const int Steps = 300;
        Dictionary<long, ulong> none;
        using (var host = new CrowdHostThread(Steps, null, 0))
        {
            host.Start();
            host.Join();
            none = host.Hashes;
        }

        string dir = LiveAndSessionTests.NewDir();
        try
        {
            Dictionary<long, ulong> watched;
            using (var host = new CrowdHostThread(Steps, dir))
            {
                using Harness h = new Harness(bundled: true);
                host.Start();
                await Task.Delay(100, TestContext.Current.CancellationToken);
                await Task.Run(() => h.Call("attach", JsonSerializer.Serialize(new { socket = host.SocketPath, wait = 10000 })), TestContext.Current.CancellationToken);
                host.Join();
                watched = host.Hashes;
            }

            Dictionary<long, ulong> leaving;
            using (var host = new CrowdHostThread(Steps, dir))
            {
                host.Start();
                await Task.Delay(100, TestContext.Current.CancellationToken);
                using (Harness h = new Harness(bundled: true))
                {
                    await Task.Run(() => h.Call("attach", JsonSerializer.Serialize(new { socket = host.SocketPath, wait = 10000 })), TestContext.Current.CancellationToken);
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }

                host.Join();
                leaving = host.Hashes;
            }

            Assert.Equal(Steps, none.Count);
            Assert.Equal(none, watched);
            Assert.Equal(none, leaving);
            Harness.Report($"crowd host: {Steps} step hashes identical with no viewer, a viewer attached to the end, and a viewer that left mid-run");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
