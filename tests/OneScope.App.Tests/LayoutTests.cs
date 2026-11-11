using System.Text.Json;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>The shell with a fork and the poke panel open stays usable at small window sizes.</summary>
[Collection("shell")]
public sealed class LayoutTests
{
    internal static (string Sha, string Renderer, string File) Capture(int width, int height, Action<Harness>? more = null)
    {
        using Harness h = new Harness(width, height).Open("test-2d-600.ostm");
        h.Call("seek", """{"step":300}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":104,"delta":[3000,4000,0]},"step":true}""");
        h.Call("step", """{"source":1,"delta":60}""");
        h.Call("inspect", """{"entity":104}""");
        more?.Invoke(h);
        JsonElement r = h.Call("capture_frame", $$"""{"path":"layout.png","size":"{{width}}x{{height}}"}""");
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(r.GetProperty("path").GetString()!, copy);
        string? dump = Environment.GetEnvironmentVariable("ONESCOPE_LAYOUT_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            File.Copy(copy, Path.Combine(dump, $"layout-{width}x{height}{(more is null ? string.Empty : "-compare")}.png"), overwrite: true);
        }

        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }

    private static string TestModuleB => Path.Combine(AppContext.BaseDirectory, "modules", "test-rule-b", "OneScope.TestModule.dll");

    private static string Q(string text) => JsonSerializer.Serialize(text);

    /// <summary>One fork, poked and run on, with the poke panel open and an entity selected.</summary>
    [Theory]
    [InlineData("layout-960x540", 960, 540)]
    [InlineData("layout-1280x720", 1280, 720)]
    public void AForkWithThePokePanelOpenIsUsableAtSmallSizesAndMatchesTheGolden(string golden, int width, int height)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(Capture(width, height));
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

    [Theory]
    [InlineData("compare-fork", false)]
    [InlineData("compare-versions", true)]
    public void TheComparePanelListsTheDifferingFieldsAndMatchesTheGolden(string golden, bool versions)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(versions ? CaptureVersions() : Capture(1280, 720, h => h.Call("compare", """{"step":340}""")));
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

    private static (string Sha, string Renderer, string File) CaptureVersions()
    {
        using Harness h = new Harness(1280, 720);
        string a = Path.Combine(AppContext.BaseDirectory, "modules", "test-rule-a", "OneScope.TestModule.dll");
        h.Call("open", $$"""{"path":{{Q(Harness.Data("test-2d-600.ostm"))}},"moduleAssembly":{{Q(a)}}}""");
        h.Call("open", $$"""{"path":{{Q(Harness.Data("test-2d-600.ostm"))}},"moduleAssembly":{{Q(TestModuleB)}}}""");
        h.Call("compare", $$"""{"goto":"first"}""");
        h.Call("seek", $$"""{"step":{{TestModule.TestWorld.RuleStep + 8}}}""");
        h.Call("inspect", """{"entity":102}""");
        JsonElement r = h.Call("capture_frame", """{"path":"compare.png","size":"1280x720"}""");
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(r.GetProperty("path").GetString()!, copy);
        string? dump = Environment.GetEnvironmentVariable("ONESCOPE_LAYOUT_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            File.Copy(copy, Path.Combine(dump, "compare-versions.png"), overwrite: true);
        }

        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }
}
