using System.Buffers;
using System.Net.Sockets;
using System.Text.Json;
using OneGame.Control;
using OneScope.Probe;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>A program with a probe in miniature: the test module's world stepping on its own thread, as a host game would.</summary>
internal sealed class MiniHost : ISnapshotSource, IDisposable
{
    private readonly TestSource _source = (TestSource)new TestModule.TestModule().Open(File.ReadAllBytes(Harness.Data("test-10000.ostm")), "host");
    private readonly TestWorld _world;
    private readonly ScopeProbe _probe;
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _steps;

    public MiniHost(string dir, int stepMicros = 300)
    {
        _world = (TestWorld)_source.CreateWorld();
        Hashes = new ulong[_source.Recording.StepCount + 1];
        Hashes[0] = _world.Hash();
        _probe = ScopeProbe.Attach(new ProbeInfo(TestFormat.ModuleId, "1.0.0", TestFormat.Version), this, new ProbeOptions { Directory = dir });
        SocketPath = _probe.SocketPath!;
        _thread = new Thread(() =>
        {
            while (!_stop && _steps < Hashes.Length - 1)
            {
                _world.Advance(_source.Recording.InputAt(_steps).Span, TimelineSink.None);
                _probe.Record(_source.Recording.InputAt(_steps).Span);
                Hashes[_steps + 1] = _world.Hash();
                Interlocked.Increment(ref _steps);
                Thread.Sleep(TimeSpan.FromMicroseconds(stepMicros));
            }
        }) { IsBackground = true };
    }

    public string SocketPath { get; }

    /// <summary>Host world hash at each step it reached (index is the step); valid up to <see cref="Steps"/>.</summary>
    public ulong[] Hashes { get; }

    public int Steps => Volatile.Read(ref _steps);

    public void Start() => _thread.Start();

    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
        ReadOnlySpan<byte> setup = _source.Recording.Setup.Span;
        setup.CopyTo(writer.GetSpan(setup.Length));
        writer.Advance(setup.Length);
        _world.WriteSnapshot(writer);
    }

    public void Stop()
    {
        _stop = true;
        _thread.Join();
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _probe.Dispose();
    }
}

/// <summary>Attach, follow, fork from a live source, and sessions, through the shell and its operations.</summary>
[Collection("shell")]
public sealed class LiveAndSessionTests
{
    internal static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "osl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void WaitFor(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            Thread.Sleep(2);
        }
    }

    [Fact]
    public async Task AttachMidRunFollowsScrubsForksAndSavesTheReceivedHistory()
    {
        string dir = NewDir();
        using var host = new MiniHost(dir);
        using Harness h = new();
        h.Call("attach", $$"""{"dir":{{JsonSerializer.Serialize(dir)}}}""");
        host.Start();
        WaitFor(() => host.Steps >= 800, "the host to run");

        JsonElement listing = h.Call("attach", $$"""{"dir":{{JsonSerializer.Serialize(dir)}}}""");
        JsonElement probe = Assert.Single(listing.GetProperty("probes").EnumerateArray());
        Assert.Equal(Environment.ProcessId, probe.GetProperty("pid").GetInt32());
        Assert.Equal("oscope.test", probe.GetProperty("module").GetString());

        // Attach by pid; the call waits for the host's snapshot, so run it where the host's thread can answer.
        JsonElement attached = await Task.Run(() => h.Call("attach", $$"""{"pid":{{Environment.ProcessId}},"wait":10000}"""));
        long start = attached.GetProperty("startStep").GetInt64();
        Assert.True(start >= 800, $"start {start}");
        Assert.Equal(start, attached.GetProperty("step").GetInt64());
        Assert.Equal("streaming", attached.GetProperty("live").GetProperty("state").GetString());

        // The cursor follows the host's newest step as records arrive.
        WaitFor(() => { h.Shell.PumpLive(); return h.Shell.Sources[0].History.EndStep >= start + 200; }, "records");
        SourceSession live = h.Shell.Sources[0];
        Assert.True(live.Follow);
        Assert.Equal(live.History.EndStep, h.Shell.Step);
        Assert.Equal(start, live.History.StartStep);

        // Scrubbing back stops following; the history keeps growing under the cursor, which stays put.
        long back = start + 100;
        h.Call("seek", $$"""{"step":{{back}}}""");
        Assert.False(live.Follow);
        long endBefore = live.History.EndStep;
        WaitFor(() => { h.Shell.PumpLive(); return live.History.EndStep > endBefore + 50; }, "more records");
        Assert.Equal(back, h.Shell.Step);
        Assert.Equal(back, live.History.CurrentStep);

        // Fork from inside the live history, poke it, step it.
        JsonElement fork = h.Call("fork", $$"""{"step":{{back}},"view":false}""");
        Assert.Equal(back, fork.GetProperty("fork").GetProperty("forkStep").GetInt64());
        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":100,"delta":[900,400,0]}}""");
        h.Call("step", """{"source":1,"delta":10}""");
        JsonElement diff = h.Call("diff", $$"""{"source":1,"other":0,"step":{{back + 10}}}""");
        Assert.False(diff.GetProperty("equal").GetBoolean());
        Assert.Equal(back, diff.GetProperty("divergence").GetInt64());

        // Following again jumps to the newest step; turning it off keeps the cursor.
        JsonElement on = h.Call("follow", """{"source":0,"on":true}""");
        Assert.True(on.GetProperty("live").GetProperty("follow").GetBoolean());
        Assert.Equal(live.History.EndStep, h.Shell.Step);

        // Let the host finish its steps, then compare every step the viewer has with the host's.
        host.Stop();
        WaitFor(() => { h.Shell.PumpLive(); return live.LiveEnded is not null || live.History.EndStep >= host.Steps; }, "the stream to drain");
        h.Shell.PumpLive();
        Assert.Equal(host.Steps, live.History.EndStep);
        for (long s = start; s <= live.History.EndStep; s += 3)
        {
            h.Call("seek", $$"""{"step":{{s}}}""");
            Assert.Equal(host.Hashes[s], live.History.StateHash);
        }

        // A session holds the received history as an embedded source, and a fresh shell reproduces its hashes and the fork's.
        JsonElement saved = h.Call("session_save", """{"path":"live.oscope"}""");
        Assert.Equal("embedded", saved.GetProperty("sources")[0].GetProperty("kind").GetString());
        Assert.True(saved.GetProperty("sources")[0].GetProperty("live").GetBoolean());
        h.Call("seek", $$"""{"step":{{back + 10}}}""");
        string forkHash = h.Shell.Sources[1].History.StateHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        host.Dispose();

        using Harness h2 = new();
        File.Copy(Path.Combine(h.Output, "live.oscope"), Path.Combine(h2.Output, "live.oscope"));
        h2.Call("session_open", JsonSerializer.Serialize(new { path = Path.Combine(h2.Output, "live.oscope") }));
        SourceSession reopened = h2.Shell.Sources[0];
        Assert.Null(reopened.Live);
        Assert.Equal(start, reopened.History.StartStep);
        for (long s = start; s <= reopened.History.EndStep; s += 11)
        {
            h2.Call("seek", $$"""{"step":{{s}}}""");
            Assert.Equal(host.Hashes[s], reopened.History.StateHash);
        }

        h2.Call("seek", $$"""{"step":{{back + 10}}}""");
        Assert.Equal(forkHash, h2.Shell.Sources[1].History.StateHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task AHostThatExitsEndsTheSourceAndKeepsWhatWasReceived()
    {
        string dir = NewDir();
        var host = new MiniHost(dir);
        using Harness h = new();
        host.Start();
        WaitFor(() => host.Steps >= 300, "the host to run");
        await Task.Run(() => h.Call("attach", JsonSerializer.Serialize(new { socket = host.SocketPath, wait = 10000 })));
        host.Dispose();
        SourceSession live = h.Shell.Sources[0];
        WaitFor(() => { h.Shell.PumpLive(); return live.LiveEnded is not null; }, "the stream to end");
        JsonElement sources = h.Call("sources");
        JsonElement state = sources.GetProperty("sources")[0].GetProperty("live");
        Assert.Equal("ended", state.GetProperty("state").GetString());
        Assert.Contains("closed its probe", state.GetProperty("endedBecause").GetString());
        long end = live.History.EndStep;
        Assert.True(end > live.History.StartStep);
        h.Call("seek", $$"""{"step":{{live.History.StartStep}}}""");
        h.Call("seek", $$"""{"step":{{end}}}""");
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task AttachToSomethingThatIsNotAProbeGivesTypedErrors()
    {
        // A wait long enough that the failure comes back from the call itself; with the default wait it would be reported under attaching.
        string dir = NewDir();
        using Harness h = new();

        ControlException none = Assert.Throws<ControlException>(() => h.Call("attach", JsonSerializer.Serialize(new { socket = Path.Combine(dir, "nothing.sock"), wait = 10000 })));
        Assert.Equal("not_found", none.Code);
        Assert.Contains("nothing.sock", none.Message);

        string regular = Path.Combine(dir, "plain.sock");
        File.WriteAllText(regular, "not a socket");
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => h.Call("attach", JsonSerializer.Serialize(new { socket = regular, wait = 10000 }))).Code);

        // A socket whose program died: the file is left behind and nobody listens.
        string dead = Path.Combine(dir, "dead.sock");
        using (var l = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            l.Bind(new UnixDomainSocketEndPoint(dead));
            l.Listen(1);
        }

        ControlException gone = Assert.Throws<ControlException>(() => h.Call("attach", JsonSerializer.Serialize(new { socket = dead, wait = 10000 })));
        Assert.Equal("not_found", gone.Code);
        Assert.Contains("dead.sock", gone.Message);

        // A live socket that speaks something else.
        string liar = Path.Combine(dir, "liar.sock");
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(liar));
        server.Listen(4);
        var accepter = Task.Run(() =>
        {
            using Socket c = server.Accept();
            c.Send("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
            Thread.Sleep(200);
        }, TestContext.Current.CancellationToken);
        ControlException bad = Assert.Throws<ControlException>(() => h.Call("attach", JsonSerializer.Serialize(new { socket = liar, wait = 10000 })));
        Assert.Equal("bad_stream", bad.Code);
        Assert.Contains("liar.sock", bad.Message);
        await accepter;

        // A pid with no probe.
        ControlException pid = Assert.Throws<ControlException>(() => h.Call("attach", $$"""{"pid":1,"wait":10000,"dir":{{JsonSerializer.Serialize(dir)}}}"""));
        Assert.Equal("not_found", pid.Code);
        Assert.Empty(h.Shell.Sources);
        Directory.Delete(dir, true);
    }

    private static Harness ForkedSession(out string sessionPath, out Dictionary<long, string> hashes)
    {
        Harness h = new Harness().Open();
        h.Call("seek", """{"step":100}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":100,"delta":[900,400,0]}}""");
        h.Call("step", """{"source":1,"delta":25}""");
        h.Call("inspect", """{"entity":101,"source":1}""");
        h.Call("view", """{"index":0,"yaw":70,"pitch":15}""");
        h.Call("seek", """{"step":112}""");
        h.Call("session_save", """{"path":"s.oscope"}""");
        sessionPath = Path.Combine(h.Output, "s.oscope");
        hashes = [];
        foreach (long s in new long[] { 0, 99, 100, 101, 112, 125, 126, 5000 })
        {
            h.Call("seek", $$"""{"step":{{s}}}""");
            JsonElement src = h.Call("sources").GetProperty("sources");
            hashes[s] = src[0].GetProperty("hash").GetString() + "/" + src[1].GetProperty("hash").GetString();
        }

        h.Call("seek", """{"step":112}""");
        return h;
    }

    [Fact]
    public void ASessionReopensWithTheSameHashesDivergenceAndPicture()
    {
        using Harness h = ForkedSession(out string path, out Dictionary<long, string> hashes);
        string before = h.Capture("before.png", """{"path":"before.png"}""");

        using Harness h2 = new();
        string copy = Path.Combine(h2.Output, "s.oscope");
        File.Copy(path, copy);
        JsonElement opened = h2.Call("session_open", JsonSerializer.Serialize(new { path = copy }));
        Assert.Equal(2, opened.GetProperty("sources").GetArrayLength());
        Assert.Equal(100, opened.GetProperty("sources")[1].GetProperty("divergence").GetInt64());
        foreach ((long s, string expected) in hashes)
        {
            h2.Call("seek", $$"""{"step":{{s}}}""");
            JsonElement src = h2.Call("sources").GetProperty("sources");
            Assert.Equal(expected, src[0].GetProperty("hash").GetString() + "/" + src[1].GetProperty("hash").GetString());
        }

        h2.Call("seek", """{"step":112}""");
        Assert.Equal(before, h2.Capture("after.png", """{"path":"after.png"}"""));
        Harness.Report($"session reopen: {hashes.Count} step probes equal on original and fork; divergence 100; capture sha256 {before} equal before and after");

        // The reopened fork is a working fork: poke it and step it, and the original stays put.
        h2.Call("seek", """{"step":125}""");
        h2.Call("poke", """{"source":1,"poke":"impulse","args":{"target":101,"delta":[10,0,0]}}""");
        h2.Call("step", """{"source":1,"delta":3}""");
        Assert.Equal(128, h2.Shell.Sources[1].History.CurrentStep);
        Assert.Equal(10_000, h2.Shell.Sources[0].History.EndStep);
    }

    [Fact]
    public void HostileSessionFilesGiveTypedErrorsAndLeaveTheShellAlone()
    {
        using Harness h = ForkedSession(out string path, out _);
        byte[] good = File.ReadAllBytes(path);
        using Harness target = new Harness().Open();
        string file = Path.Combine(target.Output, "bad.oscope");

        void Expect(byte[] bytes, string what)
        {
            File.WriteAllBytes(file, bytes);
            try
            {
                target.Call("session_open", JsonSerializer.Serialize(new { path = file }));
            }
            catch (ControlException ex)
            {
                Assert.Equal("bad_session", ex.Code);
                Assert.Contains("bad.oscope", ex.Message);
                return;
            }

            Assert.Fail($"{what}: expected a bad_session error");
        }

        Expect([], "empty");
        Expect("this is not a session"u8.ToArray(), "garbage");
        Expect(good[..10], "short header");
        byte[] version = (byte[])good.Clone();
        version[4] = 9;
        Expect(version, "wrong version");
        byte[] count = (byte[])good.Clone();
        count[16] = 0xFF;
        count[17] = 0xFF;
        count[18] = 0xFF;
        count[19] = 0x7F;
        Expect(count, "a huge source count");
        for (int len = 24; len < good.Length; len += 61)
        {
            Expect(good[..len], $"truncated at {len}");
        }

        // Layout that is not JSON, or JSON that points outside the session.
        foreach (string layout in new[] { "not json", "{\"version\":2}", "{\"version\":1,\"activeSource\":99}", "{\"version\":1,\"views\":[{\"source\":7}]}", "{\"version\":1,\"views\":[{\"source\":0,\"halfHeight\":-1}]}", "[[[[[[[[[[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]]]]]]]]]]" })
        {
            Expect(WithLayout(good, layout), "layout " + layout);
        }

        // A one-byte change anywhere either opens (the bytes were free) or fails with a typed error; nothing else escapes.
        int opened = 0;
        int flips = 0;
        for (int i = 0; i < good.Length; i += i < 300 ? 1 : 9)
        {
            flips++;
            byte[] flipped = (byte[])good.Clone();
            flipped[i] ^= 0xA5;
            File.WriteAllBytes(file, flipped);
            try
            {
                target.Call("session_open", JsonSerializer.Serialize(new { path = file }));
                opened++;
                target.Call("session_open", JsonSerializer.Serialize(new { path = path }));
            }
            catch (ControlException ex)
            {
                Assert.Equal("bad_session", ex.Code);
            }
        }

        Harness.Report($"hostile sessions: {flips} single-byte flips plus truncations and bad layouts, only typed bad_session errors ({opened} flips opened as harmless)");

        // A missing file is not_found, and after all of that the shell still holds what it had.
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => target.Call("session_open", JsonSerializer.Serialize(new { path = Path.Combine(target.Output, "missing.oscope") }))).Code);
        target.Call("session_open", JsonSerializer.Serialize(new { path }));
        Assert.Equal(2, target.Shell.Sources.Count);
    }

    private static byte[] WithLayout(byte[] session, string layout)
    {
        uint oldLen = BitConverter.ToUInt32(session, 20);
        byte[] bytes = [.. session[..(session.Length - (int)oldLen)], .. System.Text.Encoding.UTF8.GetBytes(layout)];
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (uint)System.Text.Encoding.UTF8.GetByteCount(layout));
        return bytes;
    }
}
