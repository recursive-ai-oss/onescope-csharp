using System.Text.Json;
using RecursiveAI.OnePhysics.Replay;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// The bundled OnePhysics module in the shell: replays give the library's own hashes, forks leave the original alone, and the views look right.
/// The inputs are in testdata, written by tools/GenPhysicsData with the library's recorder.
/// </summary>
[Collection("shell")]
public sealed class PhysicsTests
{
    // Steps chosen to straddle keyframes at interval 16 (0, 16, 32, 48, 64), forward and then backward.
    // The recorded worlds had stepped once before recording began, so the host's step numbers (and the shell's) run one ahead of the library's hash index.
    private const long First = 1;

    private static readonly long[] Walk = [0, 1, 15, 16, 17, 31, 32, 33, 47, 48, 64, 50, 20, 17, 16, 15, 1, 0, 64, 33, 0];

    /// <summary>The library's own world hash after each step, by playing the replay straight through its own player.</summary>
    private static ulong[] LibraryHashes(string file, int dimension)
    {
        using var stream = File.OpenRead(Harness.Data(file));
        var player = new ReplayPlayer(stream);
        var hashes = new List<ulong>();
        if (dimension == 3)
        {
            var world = player.CreateWorld3D();
            hashes.Add(world.ComputeHash());
            while (player.ReadNextStep(world))
            {
                hashes.Add(world.ComputeHash());
            }
        }
        else
        {
            var world = player.CreateWorld2D();
            hashes.Add(world.ComputeHash());
            while (player.ReadNextStep(world))
            {
                hashes.Add(world.ComputeHash());
            }
        }

        return [.. hashes];
    }

    private static string Hex(ulong v) => v.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    private static string ShellHash(Harness h, long step, int source = 0)
    {
        h.Call("seek", $$"""{"step":{{step}}}""");
        return h.Call("sources").GetProperty("sources")[source].GetProperty("hash").GetString()!;
    }

    [Theory]
    [InlineData("physics-2d", 2)]
    [InlineData("physics-3d", 3)]
    [InlineData("physics-3d-limits", 3)]
    public void ReplayHashesEqualTheLibrarysOwnAtEveryStepWeLandOn(string name, int dimension)
    {
        ulong[] library = LibraryHashes(name + ".oppr", dimension);
        string[] recorded = File.ReadAllLines(Harness.Data(name + ".hashes"));
        Assert.Equal(recorded, library.Select(Hex));

        foreach (int interval in new[] { 16, 64 })
        {
            using Harness h = new Harness(bundled: true);
            JsonElement open = h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data(name + ".oppr"))}},"keyframeInterval":{{interval}}}""");
            Assert.Equal("onephysics", open.GetProperty("module").GetString());
            Assert.Equal(dimension, open.GetProperty("dimensions").GetInt32());
            Assert.Equal(First, open.GetProperty("startStep").GetInt64());
            Assert.Equal(library.Length - 1 + First, open.GetProperty("steps").GetInt64());
            foreach (long step in Walk)
            {
                Assert.Equal(recorded[step], ShellHash(h, step + First));
            }

            Harness.Report($"{name} interval {interval}: {Walk.Length} landings equal the library's hashes at steps {string.Join(",", Walk.Distinct().Order())}");
        }
    }

    [Theory]
    [InlineData("physics-2d", 2)]
    [InlineData("physics-3d", 3)]
    public void SnapshotAndSceneFilesOpenToTheLibrarysStateHash(string name, int dimension)
    {
        // The snapshot and the scene were written from the replay's world after 32 steps.
        string[] recorded = File.ReadAllLines(Harness.Data(name + ".hashes"));
        foreach (string ext in new[] { ".opsn", ".scene" })
        {
            using Harness h = new Harness(bundled: true);
            JsonElement open = h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data(name + ext))}}}""");
            Assert.Equal(dimension, open.GetProperty("dimensions").GetInt32());
            Assert.Equal(recorded[32], open.GetProperty("hash").GetString());
        }
    }

    private static long Body(Harness h, string contains)
    {
        JsonElement entities = h.Call("entities").GetProperty("entities");
        foreach (JsonElement e in entities.EnumerateArray())
        {
            if (e.GetProperty("kind").GetString() == "body" && e.GetProperty("name").GetString()!.Contains(contains, StringComparison.Ordinal))
            {
                return e.GetProperty("id").GetInt64();
            }
        }

        throw new InvalidOperationException($"no body named like '{contains}'");
    }

    [Theory]
    [InlineData("physics-2d", "[0,6,0]")]
    [InlineData("physics-3d", "[0,6,0]")]
    public void EveryPokeOnAForkLeavesTheOriginalAlone(string name, string vector)
    {
        string file = Harness.Data(name + ".oppr");
        string shaBefore = Harness.Sha(File.ReadAllBytes(file));
        string[] recorded = File.ReadAllLines(Harness.Data(name + ".hashes"));
        using Harness h = new Harness(bundled: true);
        h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(file)}},"keyframeInterval":16}""");
        h.Call("seek", """{"step":21}""");
        long box = Body(h, "Body 1 ");
        long ball = Body(h, "Body 5 ");
        long mover = Body(h, "Body 2 ");
        long sleeper = Body(h, "(Asleep)");

        JsonElement fork = h.Call("fork");
        Assert.Equal(20 + First, fork.GetProperty("fork").GetProperty("forkStep").GetInt64());
        h.Call("poke", $$"""{"source":1,"poke":"impulse","args":{"target":{{box}},"impulse":{{vector}}},"step":true}""");
        h.Call("poke", $$"""{"source":1,"poke":"set_velocity","args":{"target":{{ball}},"velocity":{{vector}}},"step":true}""");
        h.Call("poke", $$"""{"source":1,"poke":"set_position","args":{"target":{{mover}},"position":[1.5,4,0]},"step":true}""");
        h.Call("poke", $$"""{"source":1,"poke":"wake","args":{"target":{{sleeper}}},"step":true}""");
        h.Call("step", """{"source":1,"delta":8}""");

        // The original's bytes and hashes are what they were.
        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));
        foreach (long step in new long[] { 0, 19, 20, 21, 24, 33, 64 })
        {
            Assert.Equal(recorded[step], ShellHash(h, step + First));
        }

        // One step after the first poke only the poked body differs, and diff names it and the field.
        JsonElement diff = h.Call("diff", """{"source":1,"step":22}""");
        Assert.False(diff.GetProperty("equal").GetBoolean());
        Assert.Equal(20 + First, diff.GetProperty("divergence").GetInt64());
        JsonElement difference = diff.GetProperty("difference");
        long diffEntity = difference.GetProperty("entity").GetInt64();
        string field = difference.GetProperty("field").GetString()!;
        Harness.Report($"{name} diff at 21: entity {diffEntity} field {field} {difference.GetProperty("value")} vs {difference.GetProperty("otherValue")}");
        Assert.Equal(box, diffEntity);
        Assert.False(string.IsNullOrEmpty(field));
        // Later the effect spreads; the module reports the world's own counters first.
        JsonElement later = h.Call("diff", """{"source":1,"step":25}""").GetProperty("difference");
        Harness.Report($"{name} diff at 24: entity {later.GetProperty("entity")} field {later.GetProperty("field")}");
        // Equal at the divergence step itself.
        Assert.True(h.Call("diff", """{"source":1,"step":21}""").GetProperty("equal").GetBoolean());

        // The saved fork reopens to the fork's hashes at every step.
        long steps = h.Call("sources").GetProperty("sources")[1].GetProperty("steps").GetInt64();
        var forkHashes = new List<string>();
        for (long s = First; s <= steps; s++)
        {
            forkHashes.Add(ShellHash(h, s, 1));
        }

        h.Call("replay_save", """{"source":1,"path":"fork.oppr"}""");
        h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Path.Combine(h.Output, "fork.oppr"))}},"keyframeInterval":16}""");
        for (long s = First; s <= steps; s++)
        {
            Assert.Equal(forkHashes[(int)(s - First)], ShellHash(h, s, 2));
        }

        Assert.NotEqual(recorded[24], forkHashes[24]);
        Harness.Report($"{name}: fork of {steps} steps, four pokes, saved and reopened; {forkHashes.Count} hashes equal");
    }

    private static (string Sha, string Renderer, string File) Capture(string file, string kind, string size)
    {
        using Harness h = new Harness(960, 540, bundled: true);
        h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data(file))}}}""");
        h.Call("seek", """{"step":41}""");
        h.Call("view", """{"index":0,"target":[0,3,0],"distance":20,"yaw":30,"pitch":25,"halfHeight":8}""");
        h.Call("view", """{"index":1,"target":[0,4,0],"halfHeight":9}""");
        h.Call("inspect", $$"""{"entity":{{Body(h, "Body 1 ")}}}""");
        string viewArg = kind == "shell" ? string.Empty : ",\"view\":0";
        JsonElement r = h.Call("capture_frame", $$"""{"path":"{{kind}}.png","size":"{{size}}"{{viewArg}}}""");
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(r.GetProperty("path").GetString()!, copy);
        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }

    [Theory]
    [InlineData("physics-view-2d", "physics-2d.oppr", "view", "480x360")]
    [InlineData("physics-view-3d", "physics-3d.oppr", "view", "480x360")]
    [InlineData("physics-shell-3d", "physics-3d.oppr", "shell", "960x540")]
    public void PhysicsCapturesAreByteIdenticalAcrossThreeRunsAndMatchTheGolden(string golden, string file, string kind, string size)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(Capture(file, kind, size));
            }

            GoldenTests.Check(golden, runs);
        }
        finally
        {
            foreach (var r in runs)
            {
                File.Delete(r.File);
            }
        }
    }

    [Fact]
    public void JointLimitsInThreeDimensionsAreDrawnWhenTheOverlayIsOnAndNotOtherwise()
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            string[] shas = new string[2];
            for (int i = 0; i < 3; i++)
            {
                using Harness h = new Harness(960, 540, bundled: true);
                h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data("physics-3d-limits.oppr"))}}}""");
                h.RequireGraphicsDevice();
                h.Call("seek", """{"step":30}""");
                h.Call("view", """{"index":0,"target":[0,3,0],"distance":17,"yaw":20,"pitch":15}""");
                JsonElement overlays = h.Call("overlay", """{"view":0}""");
                Assert.Contains(overlays.GetProperty("overlays").EnumerateArray(), o => o.GetProperty("name").GetString() == "Joint limits");
                if (i == 0)
                {
                    h.Call("overlay", """{"view":0,"overlay":"Joint limits","on":false}""");
                    shas[0] = h.Capture("off.png", """{"path":"off.png","view":0,"size":"960x540"}""");
                }

                h.Call("overlay", """{"view":0,"overlay":"Joint limits","on":true}""");
                JsonElement r = h.Call("capture_frame", """{"path":"limits.png","view":0,"size":"960x540"}""");
                string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
                File.Copy(r.GetProperty("path").GetString()!, copy);
                runs.Add((r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy));
                shas[1] = runs[^1].Sha;
            }

            Assert.NotEqual(shas[0], shas[1]);
            GoldenTests.Check("physics-limits-3d", runs);
        }
        finally
        {
            foreach (var r in runs)
            {
                File.Delete(r.File);
            }
        }
    }

    [Fact]
    public void TheGpuValidationLayerHasNothingToSayAboutPhysicsViews()
    {
        if (Environment.GetEnvironmentVariable("ONEGAME_GPU_VALIDATION") != "1")
        {
            Assert.Skip("Set ONEGAME_GPU_VALIDATION=1 (with the Khronos validation layer on VK_ADD_LAYER_PATH) to run this.");
        }

        using Harness h = new Harness(960, 540, bundled: true);
        h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data("physics-3d.oppr"))}}}""");
        h.Call("seek", """{"step":40}""");
        h.Call("inspect", $$"""{"entity":{{Body(h, "Body 1 ")}}}""");
        h.Call("capture_frame", """{"path":"a.png"}""");
        h.Call("capture_frame", """{"path":"b.png","view":0,"size":"640x360"}""");
        h.Call("overlay", """{"overlay":"Bounds","on":true}""");
        h.Call("overlay", """{"wireframe":true}""");
        h.Call("capture_frame", """{"path":"c.png"}""");
        h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data("physics-2d.oppr"))}}}""");
        h.Call("fork", """{"source":1,"step":20}""");
        h.Call("poke", $$"""{"poke":"impulse","args":{"target":72057598332895233,"impulse":[0,6,0]},"step":true}""");
        h.Call("capture_frame", """{"path":"d.png"}""");
        h.Call("open", $$"""{"path":{{JsonSerializer.Serialize(Harness.Data("physics-3d-limits.oppr"))}}}""");
        h.Call("overlay", """{"view":0,"overlay":"Joint limits","on":true}""");
        h.Call("capture_frame", """{"path":"e.png","view":0}""");
        string[] messages = h.Game.TakeGpuValidationMessages();
        Harness.Report($"physics validation: {messages.Length} messages over 5 captures of 3D, wireframe, bounds, 2D fork and joint-limit views");
        Assert.Empty(messages);
    }
}
