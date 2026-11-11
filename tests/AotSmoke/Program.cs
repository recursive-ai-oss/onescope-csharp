using System.Buffers;
using System.Numerics;
using OneScope.Attach;
using OneScope.Probe;
using OneScope.Sdk;
using OneScope.TestModule;

// Exercises the SDK, the probe and the test module under NativeAOT: build a replay, seek by several paths,
// fork and poke, diff, draw and record through a probe. Exit code 0 means every check held.
byte[] bytes = TestReplayBuilder.Build(dimensions: 3, bodies: 16, seed: 3, steps: 2000);
var module = new TestModule();
var source = (TestSource)module.Open(bytes, "smoke");
var history = new History(source, new HistoryOptions { KeyframeInterval = 64 });
ulong[] expected = source.ExpectedHashes!;

history.Seek(1500);
history.Seek(10);
history.Seek(1999);
if (history.StateHash != expected[1999])
{
    Console.WriteLine("seek mismatch");
    return 1;
}

history.Seek(AxisIds.Time, 700);
history.Seek(TestModule.StageAxis, 2);
if (history.StateHash != expected[700])
{
    Console.WriteLine("axis changed the time hash");
    return 1;
}

Branch branch = history.Fork(700);
branch.Poke(new PokeCall("impulse", [ScopeValue.OfEntity(new EntityId(100)), ScopeValue.OfVector(new Vector3(300, 900, 0))]));
branch.Step();
Difference? diff = history.Diff(branch.History, 701);
if (diff is null || branch.DivergenceStep != 700)
{
    Console.WriteLine("fork did not diverge where expected");
    return 1;
}

var list = new DrawList();
history.Seek(100);
history.World.Draw(list, new DrawRequest(0b1111));

bool tagged = false;
foreach (DrawItem item in list.Items)
{
    tagged |= item.Entity.IsSome;
}

if (!tagged || source.Info.Pokes.Count(p => p.Role == PokeRole.DragToPoint) != 1)
{
    Console.WriteLine("draw items carry no entity, or the drag poke is missing");
    return 1;
}

var writer = new ArrayBufferWriter<byte>();
branch.History.Source.WriteReplay(writer);
IScopeSource reopened = module.Open(writer.WrittenMemory, "fork");

using ScopeProbe probe = ScopeProbe.Attach(new ProbeInfo(TestFormat.ModuleId, "0", 1), new NoSnapshot());
probe.Record(writer.WrittenSpan);

// Live attach over a real socket, then a session file round trip.
string socketDir = Path.Combine(Path.GetTempPath(), "osp-aot-" + Environment.ProcessId);
var live = new WorldSnapshots(source.Recording.Setup);
using (ScopeProbe hostProbe = ScopeProbe.Attach(new ProbeInfo(TestFormat.ModuleId, "1", TestFormat.Version), live, new ProbeOptions { Directory = socketDir }))
{
    if (!hostProbe.IsListening || ProbeFinder.List(socketDir).Running.Count != 1)
    {
        Console.WriteLine("probe not listed");
        return 1;
    }

    string socketPath = hostProbe.SocketPath!;
    LiveSession? session = null;
    Task connect = Task.Run(() => session = LiveSession.Connect(socketPath, [module]));
    while (!hostProbe.IsConnected)
    {
        hostProbe.Poll();
        Thread.Sleep(1);
    }

    for (int s = 0; s < 200; s++)
    {
        live.World.Advance(source.Recording.InputAt(s).Span, TimelineSink.None);
        hostProbe.Record(source.Recording.InputAt(s).Span);
    }

    connect.Wait();
    using LiveSession viewer = session!;
    while (viewer.History.StepCount < 200)
    {
        _ = viewer.Pump();
        Thread.Sleep(1);
    }

    viewer.History.Seek(200);
    if (viewer.History.StateHash != live.World.Hash())
    {
        Console.WriteLine("live hash mismatch");
        return 1;
    }
}

var sessionFile = new SessionFile { CursorStep = 5 };
sessionFile.Sources.Add(SessionSource.Embed(source));
var sessionBytes = new ArrayBufferWriter<byte>();
sessionFile.Write(sessionBytes);
var sessionAgain = new ArrayBufferWriter<byte>();
SessionFile.Parse(sessionBytes.WrittenMemory, "smoke.oscope").Write(sessionAgain);
if (!sessionAgain.WrittenSpan.SequenceEqual(sessionBytes.WrittenSpan))
{
    Console.WriteLine("session round trip differs");
    return 1;
}

Directory.Delete(socketDir, true);

history.Scan();
Console.WriteLine($"ok items={list.Items.Length} diff={diff.Value.Field} findings={history.Findings.Count} fork={reopened.Recording.StepCount} connected={probe.IsConnected} live-attach=ok session-roundtrip=ok");
return 0;

internal sealed class NoSnapshot : ISnapshotSource
{
    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
    }
}

internal sealed class WorldSnapshots : ISnapshotSource
{
    private readonly ReadOnlyMemory<byte> _setup;

    public WorldSnapshots(ReadOnlyMemory<byte> setup)
    {
        _setup = setup;
        World = new TestWorld(setup.Span);
    }

    public TestWorld World { get; }

    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
        _setup.Span.CopyTo(writer.GetSpan(_setup.Length));
        writer.Advance(_setup.Length);
        World.WriteSnapshot(writer);
    }
}
