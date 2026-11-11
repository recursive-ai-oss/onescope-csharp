using OneGame.Control;
using OneScope.Sdk;
using Xunit;

namespace OneScope.App.Tests;

[Collection("shell")]
public sealed class LoaderTests
{
    private static string ModulePath => Path.Combine(AppContext.BaseDirectory, "OneScope.TestModule.dll");

    [Fact]
    public void TwoCopiesOfAModuleLoadSideBySideAndPlayTheSameReplay()
    {
        using LoadedModule a = ModuleLoader.Load(ModulePath);
        using LoadedModule b = ModuleLoader.Load(ModulePath);
        Assert.NotSame(a.Module.GetType(), b.Module.GetType());
        Assert.NotSame(a.Module.GetType().Assembly, typeof(TestModule.TestModule).Assembly);
        // The SDK is shared with the host, so the shell can use both modules through the same contract.
        Assert.Same(typeof(IScopeModule).Assembly, a.Module.GetType().GetInterface(nameof(IScopeModule))!.Assembly);

        byte[] bytes = File.ReadAllBytes(Harness.Data("test-10000.ostm"));
        var ha = new History(a.Module.Open(bytes, "a"));
        var hb = new History(b.Module.Open(bytes, "b"));
        foreach (long step in new long[] { 100, 5000, 64, 9999 })
        {
            ha.Seek(step);
            hb.Seek(step);
            Assert.Equal(ha.StateHash, hb.StateHash);
        }
    }

    [Fact]
    public void AModuleContextUnloadsWhenItsModuleIsDisposed()
    {
        WeakReference weak = LoadAndDrop();
        // A collectible context is freed by the collection that finds nothing holding it, and the runtime finishes the unload after the finalizers
        // have run, which on a busy machine can take more than a few immediate collections. Give it up to two seconds; a context that something still
        // references (a leak) never goes, however long this waits.
        int rounds = 0;
        for (; rounds < 100 && weak.IsAlive; rounds++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            if (weak.IsAlive)
            {
                Thread.Sleep(20);
            }
        }

        Harness.Report($"module context unloaded after {rounds} collections");
        Assert.False(weak.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndDrop()
    {
        LoadedModule m = ModuleLoader.Load(ModulePath);
        var history = new History(m.Module.Open(File.ReadAllBytes(Harness.Data("test-2d-600.ostm")), "x"));
        history.Seek(10);
        WeakReference weak = m.ContextReference;
        m.Dispose();
        return weak;
    }

    [Fact]
    public void MissingAndNotAModuleAreTypedErrors()
    {
        ControlException missing = Assert.Throws<ControlException>(() => ModuleLoader.Load("/no/such/module.dll"));
        Assert.Equal("not_found", missing.Code);
        string junk = Path.Combine(Path.GetTempPath(), "junk-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(junk, [1, 2, 3, 4]);
        try
        {
            ControlException bad = Assert.Throws<ControlException>(() => ModuleLoader.Load(junk));
            Assert.Equal("bad_module", bad.Code);
            Assert.Contains(junk, bad.Message);
        }
        finally
        {
            File.Delete(junk);
        }

        // The SDK assembly is a valid .NET assembly with no module in it.
        ControlException none = Assert.Throws<ControlException>(() => ModuleLoader.Load(typeof(IScopeModule).Assembly.Location));
        Assert.Equal("bad_module", none.Code);
    }
}
