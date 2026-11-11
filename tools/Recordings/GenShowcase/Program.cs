using RecursiveAI.OneMath;
using RecursiveAI.OnePhysics;
using RecursiveAI.OnePhysics.Physics3D;
using B3 = RecursiveAI.OnePhysics.Physics3D.Body;

// Records the physics scene used by the 3D scrub recording: GenShowcase <output .oppr>
//
// A limited hinge (a door that a ball knocks open until its limit stops it), a limited slider (a block thrown along its rail),
// a cone-limited pendulum, and a small stack that a heavy ball knocks over. Everything that moves has joint limits or contacts
// to draw. The file is written by the library's recorder, so it opens in the shell like any replay.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: GenShowcase <output .oppr>");
    return 2;
}

const float Dt = 1f / 60f;
const int Steps = 150;

var world = new PhysicsWorld3D(new WorldOptions3D { WorldId = 21 });
world.AddBox(new Vector3F(40, 1, 40), new Vector3D(0, -0.5, 0), type: BodyType.Static);

// A door on a hinge that may swing between -0.6 and 0.9 radians, hit by a ball from the side.
B3 post = world.AddBox(new Vector3F(0.3f, 4, 0.3f), new Vector3D(-6, 2, 0), type: BodyType.Static);
B3 door = world.AddBox(new Vector3F(2.4f, 3, 0.2f), new Vector3D(-4.8, 2, 0));
var hinge = world.AddHinge(post, door, new Vector3D(-6, 2, 0), new Vector3F(0, 1, 0));
hinge.SetLimits(-0.6f, 0.9f);
hinge.EnableLimit = true;
B3 knocker = world.AddSphere(0.9f, new Vector3D(-4.6, 1.6, 7));
knocker.LinearVelocity = new Vector3F(0, 0.5f, -14);

// A block on a slider that may travel 2 metres either way along x; it starts moving, so it reaches the limit.
B3 rail = world.AddBox(new Vector3F(0.3f, 0.3f, 0.3f), new Vector3D(0, 4.5, 0), type: BodyType.Static);
B3 block = world.AddBox(new Vector3F(1, 1, 1), new Vector3D(0.5, 4.5, 0));
var slider = world.AddSlider(rail, block, new Vector3D(0, 4.5, 0), new Vector3F(1, 0, 0));
slider.SetLimits(-2f, 2f);
slider.EnableLimit = true;
block.LinearVelocity = new Vector3F(4, 0, 0);

// A pendulum on a cone joint, released away from its rest pose.
B3 hook = world.AddBox(new Vector3F(0.3f, 0.3f, 0.3f), new Vector3D(6, 7, 0), type: BodyType.Static);
B3 bob = world.AddBox(new Vector3F(0.6f, 1.6f, 0.6f), new Vector3D(7.4, 6.2, 0));
world.AddCone(hook, bob, new Vector3D(6, 7, 0), new Vector3F(0, -1, 0), 0.7f);

// A pyramid of boxes and the heavy ball that scatters it.
for (int row = 0; row < 3; row++)
{
    for (int i = 0; i < 3 - row; i++)
    {
        world.AddBox(new Vector3F(0.9f, 0.9f, 0.9f), new Vector3D(1.5 + (i * 0.95) + (row * 0.475), 0.45 + (row * 0.92), 4));
    }
}

B3 wrecker = world.AddSphere(0.9f, new Vector3D(-4, 0.9, 4));
wrecker.LinearVelocity = new Vector3F(22, 0, 0);

world.Step(Dt);
using var stream = new MemoryStream();
using (world.StartRecording(stream))
{
    for (int i = 0; i < Steps; i++)
    {
        world.Step(Dt);
    }
}

File.WriteAllBytes(args[0], stream.ToArray());
return 0;
