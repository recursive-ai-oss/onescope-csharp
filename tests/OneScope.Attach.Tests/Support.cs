using System.Buffers;
using System.Diagnostics;
using OneScope.Attach;
using OneScope.Probe;
using OneScope.Sdk;
using OneScope.TestModule;

namespace OneScope.Attach.Tests;

internal static class Support
{
    public static readonly TestModule.TestModule Module = new();

    public static byte[] LongBytes => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-10000.ostm"));

    public static TestSource OpenLong() => (TestSource)Module.Open(LongBytes, "test-10000.ostm");

    /// <summary>A fresh socket directory for one test, under the temp path (the host points it at scratch).</summary>
    public static string NewSocketDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "osp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ProbeInfo Info => new(TestFormat.ModuleId, "1.0.0", TestFormat.Version);

    public static byte[] Write(IScopeSource source)
    {
        var w = new ArrayBufferWriter<byte>();
        source.WriteReplay(w);
        return w.WrittenSpan.ToArray();
    }

    public static void Report(string line)
    {
        string? path = Environment.GetEnvironmentVariable("ONESCOPE_TEST_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static void WaitFor(Func<bool> condition, string what, int timeoutMilliseconds = 15000)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            Thread.Sleep(1);
        }
    }
}

/// <summary>A host program in miniature: the test module's world stepping on its own thread, feeding a probe.</summary>
internal sealed class LiveHost : ISnapshotSource
{
    private readonly TestSource _source = Support.OpenLong();
    private readonly TestWorld _world;

    public LiveHost()
    {
        _world = (TestWorld)_source.CreateWorld();
        Hashes = new ulong[_source.Recording.StepCount + 1];
        Hashes[0] = _world.Hash();
        StepNanos = new long[_source.Recording.StepCount];
    }

    /// <summary>World hash at each step the host reached; index is the host's step.</summary>
    public ulong[] Hashes { get; }

    /// <summary>Wall time of each step including the probe call, in nanoseconds. Indicative only.</summary>
    public long[] StepNanos { get; }

    public int Steps { get; private set; }

    public void WriteSnapshot(IBufferWriter<byte> writer)
    {
        ReadOnlySpan<byte> setup = _source.Recording.Setup.Span;
        setup.CopyTo(writer.GetSpan(setup.Length));
        writer.Advance(setup.Length);
        _world.WriteSnapshot(writer);
    }

    /// <summary>Runs <paramref name="steps"/> steps, offering each step's input to the probe after applying it.</summary>
    public void Run(ScopeProbe? probe, int steps, Action<int>? beforeStep = null, Func<int, byte[]?>? padding = null)
    {
        for (int s = Steps; s < steps; s++)
        {
            beforeStep?.Invoke(s);
            ReadOnlyMemory<byte> input = _source.Recording.InputAt(s);
            long t0 = Stopwatch.GetTimestamp();
            _world.Advance(input.Span, TimelineSink.None);
            probe?.Record(padding?.Invoke(s) ?? input.Span);
            StepNanos[s] = (long)Stopwatch.GetElapsedTime(t0).TotalNanoseconds;
            Hashes[s + 1] = _world.Hash();
            Steps = s + 1;
        }
    }

    public static string Stats(long[] nanos, int count)
    {
        long[] sorted = nanos[..count];
        Array.Sort(sorted);
        double mean = sorted.Average() / 1000.0;
        return $"mean {mean:F1} us, p99 {sorted[(int)(count * 0.99)] / 1000.0:F1} us, max {sorted[^1] / 1000.0:F1} us";
    }
}
