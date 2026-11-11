using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class SeekTests
{
    public static TheoryData<int> Intervals => new() { 1, 7, 64, 1000 };

    private static long[] StepSet(int n, long last)
    {
        var set = new SortedSet<long> { 0, 1, n - 1, n, n + 1, 2L * n, last - 1, last };
        set.RemoveWhere(s => s < 0 || s > last);
        return [.. set];
    }

    [Fact]
    public void StraightPlaybackMatchesTheHashesInTheFile()
    {
        TestSource source = Fixtures.OpenLong();
        Assert.Equal(source.ExpectedHashes, Fixtures.StraightHashes.Value);
    }

    [Theory]
    [MemberData(nameof(Intervals))]
    public void HistoryPlaybackGivesTheModulesOwnRows(int interval)
    {
        TestSource source = Fixtures.OpenLong();
        var history = new History(source, new HistoryOptions { KeyframeInterval = interval });
        Assert.Equal(source.ExpectedHashes![0], history.StateHash);
        long mismatches = 0;
        for (long s = 1; s <= source.Recording.StepCount; s++)
        {
            Assert.True(history.Step());
            mismatches += history.StateHash == source.ExpectedHashes[s] ? 0 : 1;
        }

        Assert.Equal(0, mismatches);
        Assert.False(history.Step());
        Fixtures.Report($"replay-vs-module interval={interval}: {source.Recording.StepCount + 1} rows compared, 0 mismatches");
    }

    [Theory]
    [MemberData(nameof(Intervals))]
    public void SeekIsExactByEveryPath(int interval)
    {
        ulong[] truth = Fixtures.StraightHashes.Value;
        long last = truth.Length - 1;
        long[] set = StepSet(interval, last);
        var report = new List<string>();

        void Check(History h, string path, long k)
        {
            h.Seek(k);
            Assert.Equal(k, h.CurrentStep);
            Assert.Equal(truth[k], h.StateHash);
            Assert.Equal(k, h.World.Step);
        }

        // Forward, then backward, on one history (keyframes accumulate as it goes).
        var h1 = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = interval, MaxKeyframes = 100_000 });
        foreach (long k in set)
        {
            Check(h1, "forward", k);
        }

        foreach (long k in set.Reverse())
        {
            Check(h1, "backward", k);
        }

        // Random jumps, fresh history.
        var rng = new Random(1234 + interval);
        var h2 = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = interval, MaxKeyframes = 100_000 });
        for (int i = 0; i < 150; i++)
        {
            Check(h2, "random", rng.NextInt64(0, last + 1));
        }

        foreach (long k in set)
        {
            Check(h2, "random-set", k);
        }

        // Evicting history: at most 5 keyframes, random jumps and the step set.
        var h3 = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = interval, MaxKeyframes = 5 });
        for (int i = 0; i < 150; i++)
        {
            Check(h3, "evicting", rng.NextInt64(0, last + 1));
            Assert.True(h3.KeyframeCount <= 5);
        }

        foreach (long k in set)
        {
            Check(h3, "evicting-set", k);
        }

        // Cold seek: each step in the set on a brand-new history.
        foreach (long k in set)
        {
            var cold = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = interval });
            Check(cold, "cold", k);
            report.Add($"{k}:{truth[k]:x16}");
        }

        Fixtures.Report($"seek interval={interval} paths=forward,backward,random(150),evicting(max 5, 150),cold; each equal to straight playback: {string.Join(' ', report)}");
    }

    [Fact]
    public void SeekOutOfRangeThrows()
    {
        var h = new History(Fixtures.OpenLong());
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Seek(10_001));
    }

    [Fact]
    public void ABadInputLeavesTheHistoryUsable()
    {
        var recording = Recording.Create(TestFormat.MakeSetup(3, 4, 1), [ReadOnlyMemory<byte>.Empty, new byte[] { 99 }, ReadOnlyMemory<byte>.Empty]);
        var h = new History(TestModule.TestModule.CreateSource("bad", TestFormat.MakeSetup(3, 4, 1), recording));
        h.Seek(1);
        ulong at1 = h.StateHash;
        Assert.Throws<ScopeFormatException>(() => h.Seek(3));
        h.Seek(1);
        Assert.Equal(at1, h.StateHash);
    }

    [Fact]
    public void KeyframesHeldMatchTheIntervalAndTheCap()
    {
        long steps = Fixtures.StraightHashes.Value.Length - 1;
        foreach (int n in new[] { 1, 7, 64, 1000 })
        {
            var h = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = n, MaxKeyframes = 100_000 });
            h.Scan();
            Assert.Equal(steps / n + 1, h.KeyframeCount);
            Assert.Equal(0, h.CurrentStep);
            Fixtures.Report($"keyframes interval={n}: {h.KeyframeCount} held for {steps} steps (floor(steps/N)+1 = {steps / n + 1}), {h.KeyframeBytes} bytes");
        }

        var capped = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = 1 });
        capped.Scan();
        Assert.Equal(1024, capped.KeyframeCount);
        Assert.Equal(0L, capped.KeyframeSteps.First());
        Assert.Equal(1024L * (12 + 24 * 32), capped.KeyframeBytes);
        Fixtures.Report($"keyframes interval=1 default cap: {capped.KeyframeCount} held, {capped.KeyframeBytes} bytes");
    }
}
