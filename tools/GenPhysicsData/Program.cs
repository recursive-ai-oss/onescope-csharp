using RecursiveAI.OneMath;
using RecursiveAI.OnePhysics;
using RecursiveAI.OnePhysics.Physics2D;
using RecursiveAI.OnePhysics.Physics3D;
using BodyDef3 = RecursiveAI.OnePhysics.Physics3D.BodyDef;
using ShapeDef2 = RecursiveAI.OnePhysics.Physics2D.ShapeDef;
using ShapeDef3 = RecursiveAI.OnePhysics.Physics3D.ShapeDef;
using RecursiveAI.OnePhysics.Scene;
using RecursiveAI.OnePhysics.Replay;
using B2 = RecursiveAI.OnePhysics.Physics2D.Body;
using B3 = RecursiveAI.OnePhysics.Physics3D.Body;

// Regenerates the committed OnePhysics test inputs: GenPhysicsData <testdata directory>
//
// Each replay is recorded by the library's own recorder, and the world hash before the first step and after every step is
// written next to it (one 16-digit hex value per line), so a viewer's hashes can be compared with the library's.
// The worlds hold contacts, a hinge or ball chain, a kinematic body, a sensor, a body put to sleep and mid-run mutations.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: GenPhysicsData <testdata directory>");
    return 2;
}

string dir = args[0];
Directory.CreateDirectory(dir);
const float Dt = 1f / 60f;
const int Steps = 64;

Record2D(dir);
Record3D(dir);
Record3DLimits(dir);
return 0;

static void WriteHashes(string path, ulong[] hashes) =>
    File.WriteAllLines(path, hashes.Select(h => h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));

void Record2D(string dir)
{
    var world = new PhysicsWorld2D(new WorldOptions2D { WorldId = 7 });
    world.AddBox(new Vector2F(40, 1), new Vector2D(0, -0.5), type: BodyType.Static);
    for (int i = 0; i < 4; i++)
    {
        world.AddBox(new Vector2F(1, 1), new Vector2D(0, 0.5 + i * 1.02));
    }

    for (int i = 0; i < 5; i++)
    {
        world.AddCircle(0.4f, new Vector2D(3 + (i % 3) * 0.9, 1 + (i / 3) * 0.9));
    }

    B2 anchor = world.AddBox(new Vector2F(0.4f, 0.4f), new Vector2D(-5, 8), type: BodyType.Static);
    B2 previous = anchor;
    for (int i = 0; i < 3; i++)
    {
        B2 link = world.AddBox(new Vector2F(0.5f, 0.9f), new Vector2D(-5, 7.0 - i * 1.0));
        world.AddHinge(previous, link, new Vector2D(-5, 7.5 - i * 1.0));
        previous = link;
    }

    // A kinematic platform that slides, and a sensor volume the falling circles pass through.
    B2 platform = world.CreateBody(BodyType.Kinematic, new Vector2D(-12, 1.5));
    world.CreateBox(platform, new Vector2F(4, 0.4f));
    platform.LinearVelocity = new Vector2F(0.5f, 0);
    B2 zone = world.CreateBody(BodyType.Static, new Vector2D(4, 2.5));
    world.CreateBox(zone, new Vector2F(3, 1.5f), default, 0, 0, new ShapeDef2 { IsSensor = true });
    B2 sleeper = world.AddCircle(0.5f, new Vector2D(12, 0.5));
    world.Step(Dt);
    sleeper.Sleep();

    using var stream = new MemoryStream();
    var hashes = new ulong[Steps + 1];
    using (world.StartRecording(stream))
    {
        hashes[0] = world.ComputeHash();
        for (int i = 0; i < Steps; i++)
        {
            if (i == Steps / 3)
            {
                world.AddCircle(0.5f, new Vector2D(3.5, 7));
            }

            if (i == Steps / 2)
            {
                foreach (B2 b in world.Bodies)
                {
                    if (b.Type == BodyType.Dynamic)
                    {
                        b.ApplyImpulse(new Vector2F(2, 3));
                        break;
                    }
                }
            }

            world.Step(Dt);
            hashes[i + 1] = world.ComputeHash();
        }
    }

    File.WriteAllBytes(Path.Combine(dir, "physics-2d.oppr"), stream.ToArray());
    WriteHashes(Path.Combine(dir, "physics-2d.hashes"), hashes);

    // A snapshot of the same world partway through, and the same state as a scene file.
    var player = new ReplayPlayer(new MemoryStream(stream.ToArray()));
    PhysicsWorld2D w = player.CreateWorld2D();
    for (int i = 0; i < 32; i++)
    {
        player.ReadNextStep(w);
    }

    byte[] snapshot = w.Snapshot();
    File.WriteAllBytes(Path.Combine(dir, "physics-2d.opsn"), snapshot);
    using var text = new StringWriter();
    Scene.FromSnapshot(2, 1, snapshot).Write(text);
    File.WriteAllText(Path.Combine(dir, "physics-2d.scene"), text.ToString());
}

void Record3D(string dir)
{
    var world = new PhysicsWorld3D(new WorldOptions3D { WorldId = 7 });
    world.AddBox(new Vector3F(40, 1, 40), new Vector3D(0, -0.5, 0), type: BodyType.Static);
    for (int i = 0; i < 3; i++)
    {
        world.AddBox(new Vector3F(1, 1, 1), new Vector3D(0, 0.5 + i * 1.02, 0));
    }

    for (int i = 0; i < 4; i++)
    {
        world.AddSphere(0.4f, new Vector3D(3 + (i % 2) * 0.9, 1 + (i / 2) * 0.9, 1.5));
    }

    B3 anchor = world.AddBox(new Vector3F(0.4f, 0.4f, 0.4f), new Vector3D(-5, 8, 0), type: BodyType.Static);
    B3 previous = anchor;
    for (int i = 0; i < 3; i++)
    {
        B3 link = world.AddBox(new Vector3F(0.5f, 0.9f, 0.5f), new Vector3D(-5, 7.0 - i * 1.0, 0));
        world.AddPoint(previous, link, new Vector3D(-5, 7.5 - i * 1.0, 0));
        previous = link;
    }

    B3 platform = world.CreateBody(new BodyDef3 { Type = BodyType.Kinematic, Position = new Vector3D(-12, 1.5, 0) });
    world.CreateBox(platform, new Vector3F(4, 0.4f, 3));
    platform.LinearVelocity = new Vector3F(0.5f, 0, 0);
    B3 zone = world.CreateBody(new BodyDef3 { Type = BodyType.Static, Position = new Vector3D(4, 2.5, 0) });
    world.CreateBox(zone, new Vector3F(3, 1.5f, 3), 0f, new ShapeDef3 { IsSensor = true });
    B3 sleeper = world.AddSphere(0.5f, new Vector3D(12, 0.5, 0));
    world.Step(Dt);
    sleeper.Sleep();

    using var stream = new MemoryStream();
    var hashes = new ulong[Steps + 1];
    using (world.StartRecording(stream))
    {
        hashes[0] = world.ComputeHash();
        for (int i = 0; i < Steps; i++)
        {
            if (i == Steps / 3)
            {
                world.AddSphere(0.5f, new Vector3D(3.5, 7, 0.2));
            }

            if (i == Steps / 2)
            {
                foreach (B3 b in world.Bodies)
                {
                    if (b.Type == BodyType.Dynamic)
                    {
                        b.ApplyImpulse(new Vector3F(2, 3, 0));
                        break;
                    }
                }
            }

            world.Step(Dt);
            hashes[i + 1] = world.ComputeHash();
        }
    }

    File.WriteAllBytes(Path.Combine(dir, "physics-3d.oppr"), stream.ToArray());
    WriteHashes(Path.Combine(dir, "physics-3d.hashes"), hashes);

    var player = new ReplayPlayer(new MemoryStream(stream.ToArray()));
    PhysicsWorld3D w = player.CreateWorld3D();
    for (int i = 0; i < 32; i++)
    {
        player.ReadNextStep(w);
    }

    byte[] snapshot = w.Snapshot();
    File.WriteAllBytes(Path.Combine(dir, "physics-3d.opsn"), snapshot);
    using var text = new StringWriter();
    Scene.FromSnapshot(3, 1, snapshot).Write(text);
    File.WriteAllText(Path.Combine(dir, "physics-3d.scene"), text.ToString());
}

// A 3D world with one limited joint of each kind that has a limit to draw: a hinge, a slider and a cone. It is its own file so the
// stacks above keep their recorded hashes.
void Record3DLimits(string dir)
{
    var world = new PhysicsWorld3D(new WorldOptions3D { WorldId = 8 });
    world.AddBox(new Vector3F(40, 1, 40), new Vector3D(0, -0.5, 0), type: BodyType.Static);

    // A door on a hinge that may swing between -0.6 and 0.9 radians.
    B3 post = world.AddBox(new Vector3F(0.3f, 4, 0.3f), new Vector3D(-6, 2, 0), type: BodyType.Static);
    B3 door = world.AddBox(new Vector3F(2.4f, 3, 0.2f), new Vector3D(-4.8, 2, 0));
    var hinge = world.AddHinge(post, door, new Vector3D(-6, 2, 0), new Vector3F(0, 1, 0));
    hinge.SetLimits(-0.6f, 0.9f);
    hinge.EnableLimit = true;

    // A block on a slider that may travel 2 metres either way along x.
    B3 rail = world.AddBox(new Vector3F(0.3f, 0.3f, 0.3f), new Vector3D(0, 4, 0), type: BodyType.Static);
    B3 block = world.AddBox(new Vector3F(1, 1, 1), new Vector3D(0.5, 4, 0));
    var slider = world.AddSlider(rail, block, new Vector3D(0, 4, 0), new Vector3F(1, 0, 0));
    slider.SetLimits(-2f, 2f);
    slider.EnableLimit = true;

    // A pendulum on a cone joint.
    B3 hook = world.AddBox(new Vector3F(0.3f, 0.3f, 0.3f), new Vector3D(6, 7, 0), type: BodyType.Static);
    B3 bob = world.AddBox(new Vector3F(0.6f, 1.6f, 0.6f), new Vector3D(6.4, 5.5, 0));
    world.AddCone(hook, bob, new Vector3D(6, 7, 0), new Vector3F(0, -1, 0), 0.7f);
    world.Step(Dt);

    using var stream = new MemoryStream();
    var hashes = new ulong[Steps + 1];
    using (world.StartRecording(stream))
    {
        hashes[0] = world.ComputeHash();
        for (int i = 0; i < Steps; i++)
        {
            world.Step(Dt);
            hashes[i + 1] = world.ComputeHash();
        }
    }

    File.WriteAllBytes(Path.Combine(dir, "physics-3d-limits.oppr"), stream.ToArray());
    WriteHashes(Path.Combine(dir, "physics-3d-limits.hashes"), hashes);
}
