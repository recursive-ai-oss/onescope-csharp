using System.Buffers;
using System.Numerics;
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

var writer = new ArrayBufferWriter<byte>();
branch.History.Source.WriteReplay(writer);
IScopeSource reopened = module.Open(writer.WrittenMemory, "fork");

using ScopeProbe probe = ScopeProbe.Attach(new ProbeInfo(TestFormat.ModuleId, "0", 1), new NoSnapshot());
probe.Record(writer.WrittenSpan);

history.Scan();
Console.WriteLine($"ok items={list.Items.Length} diff={diff.Value.Field} findings={history.Findings.Count} fork={reopened.Recording.StepCount} connected={probe.IsConnected}");
return 0;

internal sealed class NoSnapshot : ISnapshotSource
{
    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
    }
}
