using System.Text.Json;
using OneGame.Host;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// Headless captures are byte-identical run to run and equal to the committed goldens. The goldens come from lavapipe (the software Vulkan
/// driver); on another driver the pixels differ in the last bits, so the comparison runs only where the capture says it was drawn by llvmpipe,
/// exactly on the build that drew them and within two steps per channel on another build.
/// Set ONESCOPE_UPDATE_GOLDENS=1 and ONESCOPE_GOLDEN_DIR=<the goldens folder> to rewrite them after a change you have looked at.
/// </summary>
[Collection("shell")]
public sealed class GoldenTests
{
    private const string Reference = "llvmpipe";

    /// <summary>The llvmpipe build the committed goldens were drawn with. Another build of the same driver rounds a few channels differently.</summary>
    private const string ReferenceBuild = "LLVM 21.";

    /// <summary>
    /// The most any channel may differ from a golden: none on the build that drew it, two steps on another llvmpipe build. The same shader code
    /// rounds a channel differently there, and by a different amount on different processors (the worker's CPU differs by at most 1, a hosted
    /// runner's by 2); a drawing change moves pixels far more than that.
    /// </summary>
    private static int Tolerance(string renderer) => renderer.Contains(ReferenceBuild, StringComparison.Ordinal) ? 0 : 2;

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

    internal static void Check(string golden, List<(string Sha, string Renderer, string File)> runs)
    {
        Harness.Report($"golden {golden}: runs {string.Join(" ", runs.Select(r => r.Sha[..16]))} renderer {string.Join(" | ", runs.Select(r => r.Renderer).Distinct())}");
        // Captures are identical run to run on one device. When a host has several GPUs the runtime may pick a different one for a later
        // shell (the runs report which), and two devices never agree to the last bit, so the identity check is per device.
        foreach (IGrouping<string, (string Sha, string Renderer, string File)> device in runs.GroupBy(r => r.Renderer))
        {
            Assert.Single(device.Select(r => r.Sha).Distinct());
            Assert.Single(device.Select(r => Harness.Sha(File.ReadAllBytes(r.File))).Distinct());
        }

        if (!runs.All(r => r.Renderer.Contains(Reference, StringComparison.OrdinalIgnoreCase)) && Environment.GetEnvironmentVariable("ONESCOPE_UPDATE_GOLDENS") != "1")
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
        int different = 0;
        for (int i = 0; i < expected.Rgba.Length; i++)
        {
            int d = Math.Abs(expected.Rgba[i] - actual.Rgba[i]);
            max = Math.Max(max, d);
            different += d > 0 ? 1 : 0;
        }

        int tolerance = Tolerance(runs[0].Renderer);
        Harness.Report($"golden {golden}: max per-channel difference {max} (tolerance {tolerance}), {different} of {expected.Rgba.Length} channel values differ; golden sha {Harness.Sha(File.ReadAllBytes(goldenFile))[..16]}");
        Assert.True(max <= tolerance, $"Golden {golden} differs from the capture by {max} in a channel ({different} of {expected.Rgba.Length} channel values differ); the tolerance on {runs[0].Renderer} is {tolerance}.");
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
        // The compare panel and the agreement row.
        h.Call("compare", """{"goto":"last"}""");
        h.Call("capture_frame", """{"path":"g.png"}""");
        string[] messages = h.Game.TakeGpuValidationMessages();
        Harness.Report($"validation: {messages.Length} messages over 7 captures of 3D, 2D, wireframe, fork, selection and compare views");
        Assert.Empty(messages);
    }
}
