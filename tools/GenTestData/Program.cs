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
return 0;
