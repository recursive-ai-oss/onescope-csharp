using System.Globalization;
using System.Text.Json;
using OneScope.Sdk;
using RecursiveAI.OnePath.Scope;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// The bundled OnePath module in the shell: crowd replays give the library's own hashes, the stage and iteration axes land exactly,
/// forks leave the original alone, and the views look right. The inputs are in testdata, written by tools/GenPathData.
/// </summary>
[Collection("shell")]
public sealed class PathTests
{
    // Steps chosen to straddle keyframes at interval 16 and 64, forward and then backward.
    private static readonly long[] Walk = [0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 119, 120, 100, 50, 17, 16, 15, 1, 0, 120, 64, 0];

    private static string Q(string s) => JsonSerializer.Serialize(s);

    internal static string ShellHash(Harness h, long step, int source = 0)
    {
        h.Call("seek", $$"""{"step":{{step}}}""");
        return h.Call("sources").GetProperty("sources")[source].GetProperty("hash").GetString()!;
    }

    [Theory]
    [InlineData("path-2d", 2)]
    [InlineData("path-3d", 3)]
    [InlineData("combo-crowd", 2)]
    public void CrowdReplayHashesEqualTheLibrarysAtEveryStepWeLandOn(string name, int dimension)
    {
        string[] recorded = File.ReadAllLines(Harness.Data(name + ".hashes"));
        foreach (int interval in new[] { 16, 64 })
        {
            using Harness h = new Harness(bundled: true);
            JsonElement open = h.Call("open", $$"""{"path":{{Q(Harness.Data(name + ".oprp"))}},"keyframeInterval":{{interval}}}""");
            Assert.Equal("onepath", open.GetProperty("module").GetString());
            Assert.Equal(dimension, open.GetProperty("dimensions").GetInt32());
            Assert.Equal(recorded.Length - 1, open.GetProperty("steps").GetInt64());
            long[] walk = [.. Walk.Where(s => s < recorded.Length)];
            foreach (long step in walk)
            {
                Assert.Equal(recorded[step], ShellHash(h, step));
            }

            Harness.Report($"{name} interval {interval}: {walk.Length} landings equal the library's hashes at steps {string.Join(",", walk.Distinct().Order())}");
        }
    }

    /// <summary>The module's own view hash for an axis position, found without the shell: step a world, then ask the module to view the axis.</summary>
    private static ulong DirectView(string file, long step, string axis, int position)
    {
        IScopeSource source = new OnePathModule().Open(File.ReadAllBytes(Harness.Data(file)), file);
        IScopeWorld world = source.CreateWorld();
        for (long s = 0; s < step; s++)
        {
            world.Advance(source.Recording.InputAt(s).Span, TimelineSink.None);
        }

        world.AdvanceAxis(axis, position, source.Recording.InputAt(step).Span);
        return world.Hash();
    }

    private static string ViewHash(Harness h, string axis, int position, long? step = null)
    {
        if (step is { } s)
        {
            h.Call("seek", $$"""{"step":{{s}}}""");
        }

        JsonElement r = h.Call("seek", $$"""{"axis":"{{axis}}","position":{{position}}}""");
        return r.GetProperty("sources")[0].GetProperty("viewHash").GetString()!;
    }

    [Fact]
    public void TheStageAxisOfABuildInputHasSevenPositionsAndEachLandsOnTheModulesOwnHash()
    {
        using Harness h = new Harness(bundled: true);
        JsonElement open = h.Call("open", $$"""{"path":{{Q(Harness.Data("path-build.opbi"))}}}""");
        JsonElement stage = open.GetProperty("axes").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "stage");
        Assert.Equal(7, stage.GetProperty("length").GetInt32());
        var stages = h.Call("entities", """{"kind":"stage"}""").GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetUInt64()).ToArray();
        Assert.Equal(7, stages.Length);
        var stageHashes = new HashSet<string>();
        string time = h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString()!;
        // Forward, then backward and skipping: every landing is the module's own view hash, and the time hash never moves.
        foreach (int p in new[] { 1, 2, 3, 4, 5, 6, 7, 5, 2, 7, 1, 4 })
        {
            Assert.Equal(DirectView("path-build.opbi", 0, "stage", p).ToString("x16", CultureInfo.InvariantCulture), ViewHash(h, "stage", p));
            Assert.Equal(time, h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString());
            JsonElement rows = h.Call("inspect", $$"""{"entity":{{stages[p - 1]}}}""").GetProperty("properties");
            Assert.Equal(p, rows.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "position").GetProperty("value").GetInt32());
            stageHashes.Add(rows.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "hash").GetProperty("value").GetString()!);
        }

        Assert.Equal(7, stageHashes.Count);
        Harness.Report($"stage axis: 7 positions land on the module's own view hash, forward, backward and skipping; the 7 stage hashes in the inspector differ ({string.Join(" ", stageHashes.Select(s => s[..8]))})");
    }

    [Theory]
    [InlineData("path-search.opgr")]
    [InlineData("path-flow.opgr")]
    public void TheIterationAxisOfAGridRunIsExactAtEveryStepAndPosition(string file)
    {
        using Harness h = new Harness(bundled: true);
        JsonElement open = h.Call("open", $$"""{"path":{{Q(Harness.Data(file))}}}""");
        int length = open.GetProperty("axes").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "iteration").GetProperty("length").GetInt32();
        int checkedCount = 0;
        foreach (long step in new long[] { 0, 3, 10, 11, 26, 41, 56 })
        {
            foreach (int p in new[] { 1, length / 3, length / 2, length })
            {
                Assert.Equal(DirectView(file, step, "iteration", p).ToString("x16", CultureInfo.InvariantCulture), ViewHash(h, "iteration", p, step));
                checkedCount++;
            }
        }

        // Stepping the axis never changes the time hash.
        long time = 30;
        string before = ShellHash(h, time);
        ViewHash(h, "iteration", length / 2);
        Assert.Equal(before, h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString());
        Harness.Report($"{file}: {checkedCount} (step, iteration) landings equal the module's direct hash; the time hash is unchanged by the axis");
    }

    private static List<(string Sha, string Renderer, string File)> Runs(string file, string[] ops, string size = "480x360", int shots = 3, bool shell = false)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        for (int i = 0; i < shots; i++)
        {
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(Harness.Data(file))}}}""");
            foreach (string op in ops)
            {
                int space = op.IndexOf(' ', StringComparison.Ordinal);
                h.Call(op[..space], op[(space + 1)..]);
            }

            string viewArg = shell ? string.Empty : ",\"view\":0";
            JsonElement r = h.Call("capture_frame", $$"""{"path":"golden.png","size":"{{size}}"{{viewArg}}}""");
            string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
            File.Copy(r.GetProperty("path").GetString()!, copy);
            runs.Add((r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy));
        }

        return runs;
    }

    private static void CheckGolden(string golden, string file, params string[] ops)
    {
        List<(string Sha, string Renderer, string File)> runs = Runs(file, ops);
        try
        {
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EachBuildStageHasAGoldenAndIsIdenticalAcrossThreeRuns(int stage)
    {
        CheckGolden($"path-stage-{stage}", "path-build.opbi",
            $$"""seek {"axis":"stage","position":{{stage}}}""",
            """view {"index":0,"target":[18,1,12],"distance":42,"yaw":-25,"pitch":42}""");
    }

    [Fact]
    public void ASearchMidWayShowsItsOpenAndClosedFrontier()
    {
        CheckGolden("path-search-mid", "path-search.opgr", """seek {"step":4}""", """seek {"axis":"iteration","position":12}""");
    }

    [Fact]
    public void AFlowFieldShowsItsCostsAndDirections()
    {
        CheckGolden("path-flow", "path-flow.opgr", """seek {"step":16}""", """overlay {"view":0,"overlay":"Costs","on":true}""", """overlay {"view":0,"overlay":"Flow directions","on":true}""");
    }

    [Fact]
    public void ACrowdIsDrawnIn2D()
    {
        CheckGolden("path-crowd-2d", "path-2d.oprp", """seek {"step":60}""", """overlay {"view":0,"overlay":"Velocities","on":true}""");
    }

    [Fact]
    public void ACrowdIsDrawnIn3D()
    {
        CheckGolden("path-crowd-3d", "path-3d.oprp", """seek {"step":60}""", """view {"index":0,"target":[14,0,12],"distance":44,"yaw":-25,"pitch":45}""", """overlay {"view":0,"overlay":"Velocities","on":true}""");
    }

    [Fact]
    public void TheGpuValidationLayerHasNothingToSayAboutOnePathViews()
    {
        if (Environment.GetEnvironmentVariable("ONEGAME_GPU_VALIDATION") != "1")
        {
            Assert.Skip("Set ONEGAME_GPU_VALIDATION=1 (with the Khronos validation layer on VK_ADD_LAYER_PATH) to run this.");
        }

        int captures = 0;
        var messages = new List<string>();
        foreach ((string file, string[] ops) in new (string, string[])[]
        {
            ("path-build.opbi", ["""seek {"axis":"stage","position":4}"""]),
            ("path-search.opgr", ["""seek {"step":4}""", """seek {"axis":"iteration","position":12}"""]),
            ("path-flow.opgr", ["""seek {"step":16}"""]),
            ("path-2d.oprp", ["""seek {"step":60}"""]),
            ("path-3d.oprp", ["""seek {"step":60}""", """overlay {"wireframe":true}"""]),
        })
        {
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(Harness.Data(file))}}}""");
            foreach (string op in ops)
            {
                int space = op.IndexOf(' ', StringComparison.Ordinal);
                h.Call(op[..space], op[(space + 1)..]);
            }

            h.Call("capture_frame", $$"""{"path":"v{{captures++}}.png"}""");
            h.Call("capture_frame", $$"""{"path":"v{{captures++}}.png","view":0,"size":"640x360"}""");
            messages.AddRange(h.Game.TakeGpuValidationMessages());
        }

        Harness.Report($"onepath validation: {messages.Count} messages over {captures} captures (build stage, search frontier, flow field, 2D and 3D crowd, wireframe)");
        Assert.Empty(messages);
    }
}
