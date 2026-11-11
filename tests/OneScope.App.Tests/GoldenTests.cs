using System.Text.Json;
using OneGame.Host;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// Headless captures are byte-identical run to run and equal to the committed goldens. The goldens come from lavapipe (the software Vulkan
/// driver); on another driver the pixels differ in the last bits, so the comparison runs only where the capture says it was drawn by llvmpipe.
/// Set ONESCOPE_UPDATE_GOLDENS=1 and ONESCOPE_GOLDEN_DIR=<the goldens folder> to rewrite them after a change you have looked at.
/// </summary>
[Collection("shell")]
public sealed class GoldenTests
{
    private const string Reference = "llvmpipe";

    private static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "goldens", name + ".png");

    private static string SourceGoldenPath(string name)
    {
        string dir = Environment.GetEnvironmentVariable("ONESCOPE_GOLDEN_DIR")
            ?? throw new InvalidOperationException("Set ONESCOPE_GOLDEN_DIR to tests/OneScope.App.Tests/goldens to update the goldens.");
        return Path.Combine(dir, name + ".png");
    }

    private static (string Sha, string Renderer, string File) CaptureOnce(string file, int seek, string view, string size, bool fork, int width, int height)
    {
        using Harness h = new Harness(width, height).Open(file);
        h.Call("seek", $$"""{"step":{{seek}}}""");
        if (fork)
        {
            // One fork, poked once and run on, so the timeline shows a branch with its divergence marker.
            h.Call("fork");
            h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":104,"delta":[3000,4000,0]},"step":true}""");
            h.Call("step", """{"source":1,"delta":120}""");
            h.Call("seek", $$"""{"step":{{seek + 60}}}""");
        }

        h.Call("overlay", """{"overlay":"Velocities","on":true}""");
        h.Call("overlay", """{"view":0,"overlay":"Labels","on":true}""");
        h.Call("inspect", """{"entity":104}""");
        // Close enough that the bodies are more than a few pixels wide.
        h.Call("view", """{"index":0,"target":[5000,1200,5000],"distance":6500,"halfHeight":3200,"yaw":35,"pitch":28}""");
        h.Call("view", """{"index":1,"target":[5000,3000,0],"distance":6500,"halfHeight":3400}""");
        string name = view.Length == 0 ? "shell" : "view";
        string viewArg = view.Length == 0 ? string.Empty : $$""","view":{{view}}""";
        JsonElement r = h.Call("capture_frame", $$"""{"path":"{{name}}.png","size":"{{size}}"{{viewArg}}}""");
        string path = r.GetProperty("path").GetString()!;
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(path, copy);
        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }

    [Theory]
    [InlineData("view-2d", "test-2d-600.ostm", 300, "0", "480x360")]
    [InlineData("view-3d", "test-10000.ostm", 500, "0", "480x360")]
    [InlineData("shell", "test-10000.ostm", 500, "", "960x540")]
    [InlineData("fork-timeline", "test-2d-600.ostm", 300, "", "1280x720")]
    public void CapturesAreByteIdenticalAcrossThreeRunsAndMatchTheGolden(string golden, string file, int seek, string view, string size)
    {
        bool fork = golden == "fork-timeline";
        (int width, int height) = fork ? (1280, 720) : (960, 540);
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(CaptureOnce(file, seek, view, size, fork, width, height));
            }

            Check(golden, runs);
        }
        finally
        {
            foreach (var r in runs)
            {
                File.Delete(r.File);
            }
        }
    }

    private static void Check(string golden, List<(string Sha, string Renderer, string File)> runs)
    {
        Harness.Report($"golden {golden}: runs {string.Join(" ", runs.Select(r => r.Sha[..16]))} renderer {runs[0].Renderer}");
        Assert.Single(runs.Select(r => r.Sha).Distinct());
        Assert.Single(runs.Select(r => Harness.Sha(File.ReadAllBytes(r.File))).Distinct());
        if (!runs[0].Renderer.Contains(Reference, StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("ONESCOPE_UPDATE_GOLDENS") != "1")
        {
            // Run-to-run identity above holds on any renderer; the pixels of the goldens only on the one that drew them.
            Harness.Report($"golden {golden}: renderer {runs[0].Renderer} is not {Reference}; compared with itself only");
            return;
        }

        if (Environment.GetEnvironmentVariable("ONESCOPE_UPDATE_GOLDENS") == "1")
        {
            File.Copy(runs[0].File, SourceGoldenPath(golden), overwrite: true);
            return;
        }

        string goldenFile = GoldenPath(golden);
        Assert.True(File.Exists(goldenFile), $"Golden {golden}.png is missing; run with ONESCOPE_UPDATE_GOLDENS=1 and look at the result.");
        CapturedFrame expected = Png.Load(goldenFile);
        CapturedFrame actual = Png.Load(runs[0].File);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int max = 0;
        for (int i = 0; i < expected.Rgba.Length; i++)
        {
            max = Math.Max(max, Math.Abs(expected.Rgba[i] - actual.Rgba[i]));
        }

        Harness.Report($"golden {golden}: max per-channel difference {max} (tolerance 0); golden sha {Harness.Sha(File.ReadAllBytes(goldenFile))[..16]}");
        Assert.Equal(0, max);
    }

    [Fact]
    public void TheGpuValidationLayerHasNothingToSayAboutAnyView()
    {
        if (Environment.GetEnvironmentVariable("ONEGAME_GPU_VALIDATION") != "1")
        {
            Assert.Skip("Set ONEGAME_GPU_VALIDATION=1 (with the Khronos validation layer on VK_ADD_LAYER_PATH) to run this.");
        }

        using Harness h = new Harness(960, 540).Open().Open("test-2d-600.ostm");
        h.Call("seek", """{"step":400}""");
        h.Call("overlay", """{"view":0,"overlay":"Velocities","on":true}""");
        h.Call("overlay", """{"view":0,"overlay":"Bounds","on":true}""");
        h.Call("view", """{"add":"3d"}""");
        h.Call("capture_frame", """{"path":"a.png"}""");
        h.Call("capture_frame", """{"path":"b.png","view":0,"size":"640x360"}""");
        h.Call("capture_frame", """{"path":"c.png","view":1,"size":"640x360"}""");
        h.Call("seek", """{"step":401}""");
        h.Call("overlay", """{"wireframe":true}""");
        h.Call("capture_frame", """{"path":"d.png"}""");
        // A fork with a branch on the timeline and a selection outlined in the 2D and 3D views.
        h.Call("overlay", """{"wireframe":false}""");
        h.Call("fork", """{"source":1,"step":300}""");
        h.Call("poke", """{"poke":"impulse","args":{"target":104,"delta":[2000,1000,0]},"step":true}""");
        h.Call("inspect", """{"entity":104}""");
        h.Call("capture_frame", """{"path":"e.png"}""");
        h.Call("capture_frame", """{"path":"f.png","view":0,"size":"640x360"}""");
        string[] messages = h.Game.TakeGpuValidationMessages();
        Harness.Report($"validation: {messages.Length} messages over 6 captures of 3D, 2D, wireframe, fork and selection views");
        Assert.Empty(messages);
    }
}
