using OneScope.TestModule;

// Regenerates the committed test replays: GenTestData <testdata directory>
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: GenTestData <testdata directory>");
    return 2;
}

Directory.CreateDirectory(args[0]);
File.WriteAllBytes(Path.Combine(args[0], "test-10000.ostm"), TestReplayBuilder.Build(dimensions: 3, bodies: 24, seed: 7, steps: 10000));
File.WriteAllBytes(Path.Combine(args[0], "test-2d-600.ostm"), TestReplayBuilder.Build(dimensions: 2, bodies: 12, seed: 11, steps: 600));

// A body handed from one world to another at step 150: the left world loses body 3, the right world gains it.
File.WriteAllBytes(Path.Combine(args[0], "test-link-left.ostm"), TestReplayBuilder.Build(TestFormat.MakeLinkedSetup(2, 8, 5, role: 1, body: 3, step: 150), seed: 5, steps: 400));
File.WriteAllBytes(Path.Combine(args[0], "test-link-right.ostm"), TestReplayBuilder.Build(TestFormat.MakeLinkedSetup(2, 8, 5, role: 2, body: 3, step: 150), seed: 5, steps: 400));

// The same two worlds with the scalar fields declared, for the field views and the field operation.
File.WriteAllBytes(Path.Combine(args[0], "test-field-2d.ostm"), TestReplayBuilder.Build(TestFormat.MakeFieldSetup(2, 12, 11), seed: 11, steps: 600));
File.WriteAllBytes(Path.Combine(args[0], "test-field-3d.ostm"), TestReplayBuilder.Build(TestFormat.MakeFieldSetup(3, 24, 7), seed: 7, steps: 600));

// A reference-dump stand-in for test-2d-600: the same bodies, 40 frames from step 200, drawn 400 mm to the right so it is visibly apart.
var module = new TestModule();
var source = (TestSource)module.Open(File.ReadAllBytes(Path.Combine(args[0], "test-2d-600.ostm")), "test-2d-600.ostm");
File.WriteAllBytes(Path.Combine(args[0], "test-2d-ghost.ospc"), TestGhost.ToBytes(TestGhost.Record(source, 200, 40, new System.Numerics.Vector3(400, 0, 0), "test-2d-600.ostm")));
return 0;
