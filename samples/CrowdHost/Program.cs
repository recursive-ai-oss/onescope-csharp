// A small navigation program with a probe: a OnePath crowd that streams its replay records to a viewer.
//
//   crowdhost <steps> <delay-ms> <hash-file> [--dir <socket-dir>]
//
// The crowd is driven through the library's CrowdRecorder, which hands every record to the probe and answers the probe's
// snapshot request. With no viewer attached the probe costs one branch per call. The host writes one "step hash" line
// per step when it finishes, so a viewer's hashes can be compared with the host's line for line.
using System.Globalization;
using System.Text;
using OneScope.Probe;
using RecursiveAI.OneMath;
using RecursiveAI.OnePath;
using RecursiveAI.OnePath.Replay;

string? dir = null;
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--dir" && i + 1 < args.Length)
    {
        dir = args[++i];
    }
    else
    {
        rest.Add(args[i]);
    }
}

if (rest.Count != 3)
{
    Console.Error.WriteLine("usage: crowdhost <steps> <delay-ms> <hash-file> [--dir <socket-dir>]");
    return 2;
}

int steps = int.Parse(rest[0], CultureInfo.InvariantCulture);
int delayMs = int.Parse(rest[1], CultureInfo.InvariantCulture);
string hashFile = rest[2];
const double Dt = 1.0 / 30.0;

NavMesh mesh = BuildArena();
// keepSteps: false, because a live host has no use for a growing in-memory replay.
var recorder = new CrowdRecorder(mesh.CreateCrowd(), Dt, keepSteps: false);
// The handshake names the module, the library version it was built with and the replay format version.
string libraryVersion = typeof(Crowd).Assembly.GetName().Version?.ToString(3) ?? "1";
using ScopeProbe probe = ScopeProbe.Attach(new ProbeInfo("onepath", libraryVersion, 1), new Snapshots(recorder), new ProbeOptions { Directory = dir });
recorder.Record = probe.Record;
Console.WriteLine($"pid {Environment.ProcessId} socket {probe.SocketPath}");
Console.Out.Flush();

var lines = new StringBuilder();
var agents = new List<Agent>();
ulong rng = 3;
for (int i = 0; i < 24; i++)
{
    Agent a = recorder.Add(Spot(0), Agent.Small);
    agents.Add(a);
    recorder.SetDestination(a, Spot(1));
}

for (int i = 0; i < steps; i++)
{
    // Orders change mid-run, so a viewer that attaches late still has orders to see.
    if (i % 90 == 45)
    {
        for (int k = 0; k < 6; k++)
        {
            recorder.SetDestination(agents[(i + (k * 3)) % agents.Count], Spot(i / 90 % 2));
        }
    }

    if (i % 120 == 60 && agents.Count > 4)
    {
        recorder.Remove(agents[1]);
        agents.RemoveAt(1);
        Agent a = recorder.Add(Spot(0), Agent.Small);
        agents.Add(a);
        recorder.SetDestination(a, Spot(1));
    }

    recorder.Step();
    probe.Poll();
    lines.Append(recorder.Crowd.StepCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(recorder.Crowd.StateHash().ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
    if (delayMs > 0)
    {
        Thread.Sleep(delayMs);
    }
}

File.WriteAllText(hashFile, lines.ToString());
return 0;

// A 40 x 24 arena split by a wall with a four-cell door.
static NavMesh BuildArena()
{
    const int W = 40, H = 24;
    var cells = new bool[W * H];
    for (int y = 0; y < H; y++)
    {
        for (int x = 0; x < W; x++)
        {
            cells[(y * W) + x] = x != 20 || (y >= 10 && y < 14);
        }
    }

    return NavMesh.Build2D(cells, W, H, 1.0, new Vector2D(0, 0), NavSettings.Humanoid with { AgentRadius = 0.0, CellSize = 0.5, TileSize = 16 });
}

Vector3D Spot(int side)
{
    rng += 0x9E3779B97F4A7C15UL;
    ulong z = rng;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    z ^= z >> 31;
    double u = (z % 1000) / 1000.0;
    double v = ((z >> 20) % 1000) / 1000.0;
    return new Vector3D(side == 0 ? 1 + (u * 17) : 22 + (u * 17), 0, 1 + (v * 22));
}

internal sealed class Snapshots(CrowdRecorder recorder) : ISnapshotSource
{
    public void WriteSnapshot(System.Buffers.IBufferWriter<byte> writer) => recorder.WriteSnapshot(writer);
}
