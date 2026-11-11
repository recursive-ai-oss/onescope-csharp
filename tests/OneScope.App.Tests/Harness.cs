using System.Security.Cryptography;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;


namespace OneScope.App.Tests;

/// <summary>A headless shell on the test module, driven through the same operation table the CLI and MCP use.</summary>
internal sealed class Harness : IDisposable
{
    private readonly GameInstance _game;

    public Harness(int width = 960, int height = 540, bool bundled = false)
    {
        Output = Path.Combine(Path.GetTempPath(), "onescope-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Output);
        Shell = new ShellModel();
        Shell.AddModule(new TestModule.TestModule());
        if (bundled)
        {
            Shell.AddBundledModules();
        }

        _game = GameInstance.Create(new ScopeGame(Shell), new GameOptions { Headless = true, Width = width, Height = height });
        var surface = new ControlSurface(_game, Output);
        (_game.Stage as ScopeStage)?.Attach();
        Ops = new ScopeEndpoint(Shell, _game, surface);
    }

    public GameInstance Game => _game;

    public ShellModel Shell { get; }

    public ScopeEndpoint Ops { get; }

    public string Output { get; }

    public static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", name);

    public JsonElement Call(string op, string? args = null)
    {
        using JsonDocument doc = JsonDocument.Parse(Ops.Call(op, args));
        return doc.RootElement.Clone();
    }

    public Harness Open(string file = "test-10000.ostm")
    {
        Call("open", JsonSerializer.Serialize(new { path = Data(file) }));
        return this;
    }

    /// <summary>
    /// Skips the test when this machine has no graphics device, which can only be told by drawing: onegame draws a shell that has no device on its CPU
    /// path (<c>CaptureResult.Renderer</c> is "cpu"), and that path paints the UI panels but not the scene views, which are drawn with the 2D canvas and
    /// the 3D renderer on a device. A hosted Windows runner has the Vulkan loader but no driver (vkCreateInstance fails with ErrorIncompatibleDriver).
    /// Only Windows may skip; on Linux and macOS a missing device is a broken environment and fails the test.
    /// </summary>
    public void RequireGraphicsDevice()
    {
        string renderer = Call("capture_frame", """{"size":"360p"}""").GetProperty("renderer").GetString()!;
        if (renderer != "cpu")
        {
            return;
        }

        Assert.True(OperatingSystem.IsWindows(), "No graphics device: the shell drew on its CPU path. Linux needs lavapipe (VK_DRIVER_FILES) and macOS needs Metal.");
        Assert.Skip("This machine has no Vulkan driver, so the shell draws its CPU path, which has no scene views (onegame: CaptureResult.Renderer is \"cpu\"); this test looks at scene pixels.");
    }

    public string Capture(string path, string args)
    {
        JsonElement r = Call("capture_frame", args);
        return r.GetProperty("sha256").GetString()!;
    }

    public void Dispose()
    {
        Ops.Dispose();
        _game.Dispose();
        try
        {
            Directory.Delete(Output, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>World hashes from running a plain world straight through, with no history or shell involved.</summary>
    public static ulong[] StraightHashes(string file)
    {
        var module = new TestModule.TestModule();
        IScopeSource source = module.Open(File.ReadAllBytes(Data(file)), file);
        IScopeWorld world = source.CreateWorld();
        var hashes = new ulong[source.Recording.StepCount + 1];
        hashes[0] = world.Hash();
        for (long s = 0; s < source.Recording.StepCount; s++)
        {
            world.Advance(source.Recording.InputAt(s).Span, TimelineSink.None);
            hashes[s + 1] = world.Hash();
        }

        return hashes;
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static void Report(string line)
    {
        string? path = Environment.GetEnvironmentVariable("ONESCOPE_TEST_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
