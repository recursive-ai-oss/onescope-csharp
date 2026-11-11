using System.Buffers;
using OneScope.Probe;
using OneScope.Sdk;
using OneScope.TestModule;

// Runs the SDK and the probe where sockets do not exist: the history re-simulates exactly, and a probe made by the host is inert
// (it does not listen), costs one branch per record, and never throws into the host.
byte[] bytes = TestReplayBuilder.Build(dimensions: 2, bodies: 8, seed: 5, steps: 600);
var module = new TestModule();
var source = (TestSource)module.Open(bytes, "wasm-smoke");
var history = new History(source, new ScopeHistoryOptions { KeyframeInterval = 32 });
ulong[] expected = source.ExpectedHashes!;
history.Seek(500);
history.Seek(40);
history.Seek(600);
if (history.StateHash != expected[600])
{
    Console.WriteLine("seek mismatch");
    return 1;
}

using ScopeProbe probe = ScopeProbe.Attach(new ProbeInfo(TestFormat.ModuleId, "0", 1), new NoSnapshot());
for (int i = 0; i < 1000; i++)
{
    probe.Record(new byte[16]);
    probe.Poll();
}

Console.WriteLine($"wasm ok hash={history.StateHash:x16} listening={probe.IsListening} connected={probe.IsConnected}");
return 0;

internal sealed class NoSnapshot : ISnapshotSource
{
    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
    }
}
