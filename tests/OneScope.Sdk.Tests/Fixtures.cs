using System.Buffers;
using OneScope.Sdk;
using OneScope.TestModule;

namespace OneScope.Sdk.Tests;

internal static class Fixtures
{
    public static readonly TestModule.TestModule Module = new();

    private static readonly Lazy<byte[]> Long = new(() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-10000.ostm")));

    public static byte[] LongBytes => Long.Value;

    public static TestSource OpenLong() => (TestSource)Module.Open(LongBytes, "test-10000.ostm");

    /// <summary>Hashes from running a plain world straight through, with no history involved.</summary>
    public static readonly Lazy<ulong[]> StraightHashes = new(() =>
    {
        TestSource source = OpenLong();
        IScopeWorld world = source.CreateWorld();
        var hashes = new ulong[source.Recording.StepCount + 1];
        hashes[0] = world.Hash();
        for (long s = 0; s < source.Recording.StepCount; s++)
        {
            world.Advance(source.Recording.InputAt(s).Span, TimelineSink.None);
            hashes[s + 1] = world.Hash();
        }

        return hashes;
    });

    public static byte[] Write(IScopeSource source)
    {
        var w = new ArrayBufferWriter<byte>();
        source.WriteReplay(w);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The fewest bytes the calling thread allocated over any of up to <paramref name="rounds"/> runs of <paramref name="body"/>, stopping at the first run that
    /// allocated nothing. The runtime's own work on this thread (promoting a method to optimised code, a collection another test starts) can add a few
    /// thousand bytes to one run; code that really allocates does so in every run, so the smallest run is the one that tells.
    /// </summary>
    public static long SmallestAllocation(Action body, int rounds = 25)
    {
        long smallest = long.MaxValue;
        for (int round = 0; round < rounds && smallest != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return smallest;
    }

    public static void Report(string line)
    {
        string? path = Environment.GetEnvironmentVariable("ONESCOPE_TEST_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
