// A small physics program with a probe: a OnePhysics world that streams its replay to a viewer.
//
//   physicshost <2d|3d> <steps> <delay-ms> <hash-file> [--dir <socket-dir>]
//
// The world records itself through the library's own recorder, into the module's live feed; the feed hands every record
// to the probe and answers the probe's snapshot request. With no viewer attached the probe costs one branch per step.
// The host writes one "step hash" line per step when it finishes, so a viewer's hashes can be compared line for line.
using System.Globalization;
using System.Text;
using OneScope.Probe;
using RecursiveAI.OneMath;
using RecursiveAI.OnePhysics;
using RecursiveAI.OnePhysics.Physics2D;
using RecursiveAI.OnePhysics.Physics3D;
using RecursiveAI.OnePhysics.Scope;
using Body2 = RecursiveAI.OnePhysics.Physics2D.Body;
using Body3 = RecursiveAI.OnePhysics.Physics3D.Body;

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

if (rest.Count != 4 || rest[0] is not ("2d" or "3d"))
{
    Console.Error.WriteLine("usage: physicshost <2d|3d> <steps> <delay-ms> <hash-file> [--dir <socket-dir>]");
    return 2;
}

bool three = rest[0] == "3d";
int steps = int.Parse(rest[1], CultureInfo.InvariantCulture);
int delayMs = int.Parse(rest[2], CultureInfo.InvariantCulture);
string hashFile = rest[3];
const float Dt = 1f / 60f;

var module = new OnePhysicsModule();
var info = new ProbeInfo(module.Id, "1.0.0", 2);
var lines = new StringBuilder();
if (three)
{
    var world = new PhysicsWorld3D(new WorldOptions3D { WorldId = 11 });
    BuildWorld3D(world);
    var feed = new LiveFeedStream(world);
    using ScopeProbe probe = StartProbe(feed);
    using (world.StartRecording(feed))
    {
        for (int i = 0; i < steps; i++)
        {
            Mutate3D(world, i);
            world.Step(Dt);
            probe.Poll();
            lines.Append(world.StepCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(world.ComputeHash().ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            Pause();
        }
    }
}
else
{
    var world = new PhysicsWorld2D(new WorldOptions2D { WorldId = 11 });
    BuildWorld2D(world);
    var feed = new LiveFeedStream(world);
    using ScopeProbe probe = StartProbe(feed);
    using (world.StartRecording(feed))
    {
        for (int i = 0; i < steps; i++)
        {
            Mutate2D(world, i);
            world.Step(Dt);
            probe.Poll();
            lines.Append(world.StepCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(world.ComputeHash().ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            Pause();
        }
    }
}

File.WriteAllText(hashFile, lines.ToString());
return 0;

ScopeProbe StartProbe(LiveFeedStream feed)
{
    ScopeProbe p = ScopeProbe.Attach(info, new FeedSnapshots(feed), new ProbeOptions { Directory = dir });
    feed.Record = p.Record;
    Console.WriteLine($"pid {Environment.ProcessId} socket {p.SocketPath}");
    Console.Out.Flush();
    return p;
}

void Pause()
{
    if (delayMs > 0)
    {
        Thread.Sleep(delayMs);
    }
}

static void BuildWorld2D(PhysicsWorld2D world)
{
    world.AddBox(new Vector2F(40, 1), new Vector2D(0, -0.5), type: BodyType.Static);
    for (int i = 0; i < 4; i++)
    {
        world.AddBox(new Vector2F(1, 1), new Vector2D(0, 0.5 + (i * 1.02)));
    }

    for (int i = 0; i < 5; i++)
    {
        world.AddCircle(0.4f, new Vector2D(3 + ((i % 3) * 0.9), 1 + ((i / 3) * 0.9)));
    }

    Body2 previous = world.AddBox(new Vector2F(0.4f, 0.4f), new Vector2D(-5, 8), type: BodyType.Static);
    for (int i = 0; i < 3; i++)
    {
        Body2 link = world.AddBox(new Vector2F(0.5f, 0.9f), new Vector2D(-5, 7.0 - i));
        world.AddHinge(previous, link, new Vector2D(-5, 7.5 - i));
        previous = link;
    }
}

// Mid-run changes the recorder must carry: a body added now and then, and a shove.
static void Mutate2D(PhysicsWorld2D world, int i)
{
    if (i % 50 == 20)
    {
        world.AddCircle(0.4f, new Vector2D(3.5 + ((i / 50) % 3), 7));
    }

    if (i % 80 == 40)
    {
        foreach (Body2 b in world.Bodies)
        {
            if (b.Type == BodyType.Dynamic)
            {
                b.ApplyImpulse(new Vector2F(2, 3));
                break;
            }
        }
    }
}

static void BuildWorld3D(PhysicsWorld3D world)
{
    world.AddBox(new Vector3F(40, 1, 40), new Vector3D(0, -0.5, 0), type: BodyType.Static);
    for (int i = 0; i < 3; i++)
    {
        world.AddBox(new Vector3F(1, 1, 1), new Vector3D(0, 0.5 + (i * 1.02), 0));
    }

    for (int i = 0; i < 4; i++)
    {
        world.AddSphere(0.4f, new Vector3D(3 + ((i % 2) * 0.9), 1 + ((i / 2) * 0.9), 1.5));
    }

    Body3 previous = world.AddBox(new Vector3F(0.4f, 0.4f, 0.4f), new Vector3D(-5, 8, 0), type: BodyType.Static);
    for (int i = 0; i < 3; i++)
    {
        Body3 link = world.AddBox(new Vector3F(0.5f, 0.9f, 0.5f), new Vector3D(-5, 7.0 - i, 0));
        world.AddPoint(previous, link, new Vector3D(-5, 7.5 - i, 0));
        previous = link;
    }
}

static void Mutate3D(PhysicsWorld3D world, int i)
{
    if (i % 50 == 20)
    {
        world.AddSphere(0.5f, new Vector3D(3.5 + ((i / 50) % 3), 7, 0.2));
    }

    if (i % 80 == 40)
    {
        foreach (Body3 b in world.Bodies)
        {
            if (b.Type == BodyType.Dynamic)
            {
                b.ApplyImpulse(new Vector3F(2, 3, 0));
                break;
            }
        }
    }
}

internal sealed class FeedSnapshots(LiveFeedStream feed) : ISnapshotSource
{
    public void WriteSnapshot(System.Buffers.IBufferWriter<byte> writer) => feed.WriteSnapshot(writer);
}
