// A host program with a probe, and a viewer for it, in one small tool.
//
//   probesample host <steps> <delay-ms> <hash-file> [--dir <socket-dir>]   run the test module's world, with a probe
//   probesample list [--dir <socket-dir>]                                  list running probes
//   probesample view <socket> <hash-file>                                  attach, re-simulate, write host-step/hash lines
//
// The host writes one "step hash" line per step when it finishes; the viewer writes the same for the steps it saw, so the two
// files can be compared line for line.
using System.Buffers;
using System.Globalization;
using System.Text;
using OneScope.Attach;
using OneScope.Probe;
using OneScope.Sdk;
using OneScope.TestModule;

string? dir = null;
var args2 = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--dir" && i + 1 < args.Length)
    {
        dir = args[++i];
    }
    else
    {
        args2.Add(args[i]);
    }
}

return args2.Count == 0 ? Usage() : args2[0] switch
{
    "host" when args2.Count == 4 => Host(int.Parse(args2[1], CultureInfo.InvariantCulture), int.Parse(args2[2], CultureInfo.InvariantCulture), args2[3], dir),
    "list" => List(dir),
    "view" when args2.Count == 3 => View(args2[1], args2[2]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: probesample host <steps> <delay-ms> <hash-file> | list | view <socket> <hash-file>   (add --dir <socket-dir>)");
    return 2;
}

static int Host(int steps, int delayMs, string hashFile, string? dir)
{
    var module = new TestModule();
    var source = (TestSource)module.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "test-10000.ostm")), "test-10000.ostm");
    steps = Math.Min(steps, (int)source.Recording.StepCount);
    IScopeWorld world = source.CreateWorld();
    var snapshots = new WorldSnapshots(source.Recording.Setup, world);
    using ScopeProbe probe = ScopeProbe.Attach(new ProbeInfo(module.Id, "1.0.0", TestFormat.Version), snapshots, new ProbeOptions { Directory = dir });
    Console.WriteLine($"pid {Environment.ProcessId} socket {probe.SocketPath}");
    Console.Out.Flush();
    var lines = new StringBuilder();
    lines.Append("0 ").Append(world.Hash().ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
    probe.Poll();
    for (int s = 0; s < steps; s++)
    {
        ReadOnlyMemory<byte> input = source.Recording.InputAt(s);
        world.Advance(input.Span, TimelineSink.None);
        probe.Record(input.Span);
        lines.Append(s + 1).Append(' ').Append(world.Hash().ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }
    }

    File.WriteAllText(hashFile, lines.ToString());
    return 0;
}

static int List(string? dir)
{
    ProbeListing listing = ProbeFinder.List(dir);
    foreach (ProbeEntry e in listing.Running)
    {
        Console.WriteLine($"running\tpid {e.ProcessId}\tmodule {e.Handshake.Info.ModuleId}\tlibrary {e.Handshake.Info.LibraryVersion}\tformat {e.Handshake.Info.FormatVersion}\t{e.SocketPath}");
    }

    foreach (string r in listing.Removed)
    {
        Console.WriteLine("removed stale\t" + r);
    }

    foreach (string u in listing.Unreadable)
    {
        Console.WriteLine("unreadable\t" + u);
    }

    return 0;
}

static int View(string socket, string hashFile)
{
    using LiveSession session = LiveSession.Connect(socket, [new TestModule()]);
    Console.WriteLine($"attached to pid {session.Handshake.ProcessId} at host step {session.History.StartStep}");
    while (true)
    {
        int n = session.Pump();
        if (n == 0 && session.State != ClientState.Streaming)
        {
            if (session.Pump() == 0)
            {
                break;
            }
        }
        else if (n == 0)
        {
            Thread.Sleep(1);
        }
    }

    var lines = new StringBuilder();
    for (long j = session.History.StartStep; j <= session.History.EndStep; j++)
    {
        session.History.Seek(j);
        lines.Append(j).Append(' ').Append(session.History.StateHash.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
    }

    File.WriteAllText(hashFile, lines.ToString());
    Console.WriteLine($"stream {session.State}; {session.History.StepCount} steps, host steps {session.History.StartStep} to {session.History.EndStep}");
    return 0;
}

internal sealed class WorldSnapshots(ReadOnlyMemory<byte> setup, IScopeWorld world) : ISnapshotSource
{
    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
        setup.Span.CopyTo(writer.GetSpan(setup.Length));
        writer.Advance(setup.Length);
        world.WriteSnapshot(writer);
    }
}
