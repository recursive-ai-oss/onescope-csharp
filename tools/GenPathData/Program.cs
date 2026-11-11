using System.Buffers;
using System.Globalization;
using RecursiveAI.OneMath;
using RecursiveAI.OnePath;
using RecursiveAI.OnePath.Grids;
using RecursiveAI.OnePath.Replay;
using RecursiveAI.OnePath.Scope;
using RecursiveAI.OnePhysics;
using RecursiveAI.OnePhysics.Physics2D;

// Regenerates the committed OnePath test inputs: GenPathData <testdata directory>
//
// Crowd replays are recorded with the library's CrowdRecorder, and the crowd's hash before the first step and after every
// step is written next to each (one 16-digit hex value per line) so a viewer's hashes can be compared with the library's.
// The build input and the two grid runs are written by the module's own writers. The "combo" pair is one scenario run twice:
// a physics world and a crowd of the same discs in the same arena, both stepped 60 times at 60 steps a second, so they share a time axis.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: GenPathData <testdata directory>");
    return 2;
}

string dir = args[0];
Directory.CreateDirectory(dir);
const double Dt = 1.0 / 30.0;

Crowd2D(dir);
Crowd3D(dir);
Build(dir);
Grids(dir);
Combo(dir);
return 0;

static string Hex(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);

static NavMesh Arena2D()
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

static (double[] Vertices, int[] Indices) Terrain3D()
{
    var v = new List<double>();
    var t = new List<int>();
    void Quad(double x0, double y0, double z0, double x1, double y1, double z1, double x2, double y2, double z2, double x3, double y3, double z3)
    {
        int b = v.Count / 3;
        v.AddRange([x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3]);
        t.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }

    // A floor, a ramp up to a platform, and a block too tall to climb.
    Quad(0, 0, 0, 0, 0, 24, 24, 0, 24, 24, 0, 0);
    Quad(24, 0, 0, 24, 0, 24, 30, 2, 24, 30, 2, 0);
    Quad(30, 2, 0, 30, 2, 24, 36, 2, 24, 36, 2, 0);
    double x0 = 8, x1 = 12, z0 = 8, z1 = 14, h = 3;
    Quad(x0, h, z0, x0, h, z1, x1, h, z1, x1, h, z0);
    Quad(x0, 0, z0, x0, h, z0, x1, h, z0, x1, 0, z0);
    Quad(x1, 0, z1, x1, h, z1, x0, h, z1, x0, 0, z1);
    Quad(x0, 0, z1, x0, h, z1, x0, h, z0, x0, 0, z0);
    Quad(x1, 0, z0, x1, h, z0, x1, h, z1, x1, 0, z1);
    return ([.. v], [.. t]);
}

static NavSettings Settings3D() => NavSettings.Humanoid with { AgentRadius = 0.4, AgentHeight = 1.8, WalkableClimb = 0.6, CellSize = 0.5, CellHeight = 0.25, TileSize = 16, MinRegionArea = 8, MergeRegionArea = 20 };

static ulong Mix(ref ulong s)
{
    s += 0x9E3779B97F4A7C15UL;
    ulong z = s;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
    return z ^ (z >> 31);
}

// What a host does to its crowd: agents walk across, then join, leave, change target, stop and teleport.
static (byte[] Replay, ulong[] Hashes) RecordCrowd(NavMesh mesh, double dt, int steps, ulong seed, double y, double zMax, double leftMax, double rightMin, double rightMax, int agentCount)
{
    var rec = new CrowdRecorder(mesh.CreateCrowd(), dt, keepSteps: true);
    ulong s = seed;
    double R(double lo, double hi) => lo + ((Mix(ref s) % 1000) / 1000.0 * (hi - lo));
    Vector3D Left() => new(R(1, leftMax - 1), y, R(1, zMax - 1));
    Vector3D Right() => new(R(rightMin, rightMax), y, R(1, zMax - 1));
    var agents = new List<Agent>();

    // The replay's first hash is the crowd as the setup holds it, before the host's first orders.
    var hashes = new List<ulong> { rec.Crowd.StateHash() };
    for (int i = 0; i < agentCount; i++)
    {
        Agent a = rec.Add(Left(), Agent.Small);
        agents.Add(a);
        rec.SetDestination(a, Right());
    }

    for (int i = 0; i < steps; i++)
    {
        if (i == steps / 6)
        {
            Agent a = rec.Add(Left(), Agent.Small);
            agents.Add(a);
            rec.SetDestination(a, Right());
        }
        else if (i == steps / 3)
        {
            rec.Remove(agents[2]);
            agents.RemoveAt(2);
            for (int k = 0; k < 4; k++)
            {
                rec.SetDestination(agents[k], Left());
            }
        }
        else if (i == steps / 2)
        {
            rec.Stop(agents[1]);
            rec.Teleport(agents[3], Right());
        }

        rec.Step();
        hashes.Add(rec.Crowd.StateHash());
    }

    return (rec.ToReplay().ToBytes(), [.. hashes]);
}

static void Save(string dir, string name, byte[] replay, ulong[] hashes)
{
    File.WriteAllBytes(Path.Combine(dir, name + ".oprp"), replay);
    File.WriteAllLines(Path.Combine(dir, name + ".hashes"), hashes.Select(Hex));
}

void Crowd2D(string dir)
{
    (byte[] r, ulong[] h) = RecordCrowd(Arena2D(), Dt, 120, 11, 0, 24, 18, 22, 39, 12);
    Save(dir, "path-2d", r, h);
}

void Crowd3D(string dir)
{
    (double[] v, int[] i) = Terrain3D();
    NavMesh mesh = NavMesh.Build(v, i, Settings3D());
    (byte[] r, ulong[] h) = RecordCrowd(mesh, Dt, 120, 12, 0, 24, 7, 14, 23, 10);
    Save(dir, "path-3d", r, h);
}

void Build(string dir)
{
    (double[] v, int[] i) = Terrain3D();
    var w = new ArrayBufferWriter<byte>();
    BuildInput.Write(v, i, Settings3D(), w);
    File.WriteAllBytes(Path.Combine(dir, "path-build.opbi"), w.WrittenSpan.ToArray());
}

static Grid Maze()
{
    var g = new Grid(48, 32);
    for (int y = 0; y < 32; y++)
    {
        if (y is not (6 or 7 or 22))
        {
            g.SetWalkable(24, y, false);
        }
    }

    for (int i = 0; i < 40; i++)
    {
        g.SetWalkable(3 + ((i * 7) % 18), 2 + ((i * 11) % 28), false);
        g.SetWalkable(28 + ((i * 5) % 18), 1 + ((i * 13) % 30), false);
    }

    foreach (Cell c in new[] { new Cell(2, 16), new Cell(45, 16), new Cell(24, 6), new Cell(24, 7), new Cell(24, 22) })
    {
        g.SetWalkable(c.X, c.Y, true);
    }

    return g;
}

static List<IReadOnlyList<GridRunOp>> Edits(int steps, bool search)
{
    var list = new List<IReadOnlyList<GridRunOp>>();
    for (int i = 0; i < steps; i++)
    {
        GridRunOp[] ops = i switch
        {
            10 => [GridRunOp.SetArea(24, 6, 255), GridRunOp.SetArea(24, 7, 255)],
            25 => [GridRunOp.SetGoal(44, 3)],
            40 => [GridRunOp.SetArea(24, 6, 0)],
            55 when search => [GridRunOp.SetStart(3, 28)],
            _ => [],
        };
        list.Add(ops);
    }

    return list;
}

void Grids(string dir)
{
    var w = new ArrayBufferWriter<byte>();
    GridRun.WriteSearch(Maze(), new GridOptions { Algorithm = GridAlgorithm.AStar }, new Cell(2, 16), new Cell(45, 16), 24, Edits(80, true), w);
    File.WriteAllBytes(Path.Combine(dir, "path-search.opgr"), w.WrittenSpan.ToArray());
    w = new ArrayBufferWriter<byte>();
    GridRun.WriteFlow(Maze(), null, [new Cell(45, 16), new Cell(2, 30)], 60, Edits(60, false), w);
    File.WriteAllBytes(Path.Combine(dir, "path-flow.opgr"), w.WrittenSpan.ToArray());
}

// Discs in a walled arena. The physics world has no gravity and the discs are pushed towards the door in the wall, the same
// discs the crowd's agents start from, so the two sources show one scenario from two libraries.
void Combo(string dir)
{
    const int Steps = 60;
    const double Step = 1.0 / 60.0;
    var world = new PhysicsWorld2D(new WorldOptions2D { WorldId = 21, Gravity = new Vector2F(0, 0) });
    world.AddBox(new Vector2F(1, 10), new Vector2D(20.5, 5), type: BodyType.Static);
    world.AddBox(new Vector2F(1, 10), new Vector2D(20.5, 19), type: BodyType.Static);
    world.AddBox(new Vector2F(42, 1), new Vector2D(20, -0.5), type: BodyType.Static);
    world.AddBox(new Vector2F(42, 1), new Vector2D(20, 24.5), type: BodyType.Static);
    world.AddBox(new Vector2F(1, 26), new Vector2D(-0.5, 12), type: BodyType.Static);
    world.AddBox(new Vector2F(1, 26), new Vector2D(40.5, 12), type: BodyType.Static);
    ulong s = 5;
    var discs = new List<(Vector2D Position, RecursiveAI.OnePhysics.Physics2D.Body Body)>();
    for (int i = 0; i < 10; i++)
    {
        var p = new Vector2D(2 + ((Mix(ref s) % 1000) / 1000.0 * 15), 2 + ((Mix(ref s) % 1000) / 1000.0 * 20));
        discs.Add((p, world.AddCircle(0.4f, p)));
    }

    using var stream = new MemoryStream();
    var physicsHashes = new List<ulong>();
    using (world.StartRecording(stream))
    {
        physicsHashes.Add(world.ComputeHash());
        for (int i = 0; i < Steps; i++)
        {
            if (i == 0)
            {
                foreach ((Vector2D p, var body) in discs)
                {
                    body.ApplyImpulse(new Vector2F((float)(20.5 - p.X) * 0.4f, (float)(12 - p.Y) * 0.4f));
                }
            }

            world.Step((float)Step);
            physicsHashes.Add(world.ComputeHash());
        }
    }

    File.WriteAllBytes(Path.Combine(dir, "combo-physics.oppr"), stream.ToArray());
    File.WriteAllLines(Path.Combine(dir, "combo-physics.hashes"), physicsHashes.Select(Hex));

    var rec = new CrowdRecorder(Arena2D().CreateCrowd(), Step, keepSteps: true);
    var crowdHashes = new List<ulong> { rec.Crowd.StateHash() };
    foreach ((Vector2D p, _) in discs)
    {
        Agent a = rec.Add(new Vector3D(p.X, 0, p.Y), Agent.Small);
        rec.SetDestination(a, new Vector3D(34, 0, 12));
    }

    for (int i = 0; i < Steps; i++)
    {
        rec.Step();
        crowdHashes.Add(rec.Crowd.StateHash());
    }

    Save(dir, "combo-crowd", rec.ToReplay().ToBytes(), [.. crowdHashes]);
}
