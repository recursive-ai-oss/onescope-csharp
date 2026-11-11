using System.Runtime.CompilerServices;
using OneGame.Control;
using OneGame.Host;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// Which graphics device the tests draw on, and what the shell says when the window can't give a frame. Every device creation in a process
/// goes through onegame's adapter pick, so one pin makes every shell in the process land on the same device.
/// </summary>
public sealed class DeviceTests
{
    /// <summary>
    /// The goldens come from the software Vulkan driver, so on Linux and Windows the tests ask for it unless a run names another adapter
    /// (ONEGAME_GPU_ADAPTER, for example <c>discrete</c> to check a capture on a real GPU). macOS draws with Metal, which has no adapter list.
    /// </summary>
    [ModuleInitializer]
    internal static void PinTheAdapter()
    {
        if (!OperatingSystem.IsMacOS() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONEGAME_GPU_ADAPTER")))
        {
            Environment.SetEnvironmentVariable("ONEGAME_GPU_ADAPTER", "software");
        }
    }

    /// <summary>The device a headless shell drew a capture on (the capture reports it; <c>info</c> names an adapter only for a windowed shell).</summary>
    private static string AdapterOf(Harness h) => h.Call("capture_frame", """{"size":"360p"}""").GetProperty("renderer").GetString()!;

    [Fact]
    public void TwoShellsInOneProcessDrawOnTheSameDevice()
    {
        using var first = new Harness().Open();
        using var second = new Harness().Open();
        string a = AdapterOf(first);
        string b = AdapterOf(second);
        Harness.Report($"adapter pin '{Environment.GetEnvironmentVariable("ONEGAME_GPU_ADAPTER")}': first shell {a}; second shell {b}");
        Assert.Equal(a, b);
        if (!OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("ONEGAME_GPU_ADAPTER") == "software")
        {
            Assert.Contains("llvmpipe", a, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void APresentedCaptureThatFailsIsATypedErrorThatSaysWhy()
    {
        using var h = new Harness().Open();
        h.Ops.CaptureHook = (_, _) => throw new PresentedCaptureException(PresentedCaptureFailure.Minimized);
        ControlException e = Assert.Throws<ControlException>(() => h.Call("capture_frame", """{"presented":true}"""));
        Assert.Equal("presented_unavailable", e.Code);
        Assert.Contains("minimized", e.Message, StringComparison.Ordinal);
        Assert.Contains("without 'presented'", e.Message, StringComparison.Ordinal);
        Harness.Report($"presented capture error: {{\"ok\":false,\"error\":{{\"code\":\"{e.Code}\",\"message\":\"{e.Message}\"}}}}");
        h.Ops.CaptureHook = null;
        Assert.Equal("rejected", Assert.Throws<ControlException>(() => h.Call("capture_frame", """{"presented":true}""")).Code);
    }
}
