using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

/// <summary>A history that starts at a step other than 0, as a live attach to a program that has already run 1502 steps does.</summary>
public class StartStepTests
{
    private const long Start = 1502;
    private const long End = 4000;

    /// <summary>A live-style source: the host's snapshot at step 1502, then the host's inputs for steps 1502 to 3999.</summary>
    private static (TestSource Source, byte[] Replay) OffsetSource()
    {
        TestSource full = Fixtures.OpenLong();
        IScopeWorld world = full.CreateWorld();
        for (long s = 0; s < Start; s++)
        {
            world.Advance(full.Recording.InputAt(s).Span, TimelineSink.None);
        }

        var snapshot = new ArrayBufferWriter<byte>();
        snapshot.Write(full.Recording.Setup.Span);
        world.WriteSnapshot(snapshot);
        ILiveSource live = Fixtures.Module.OpenLive("1.0.0", TestFormat.Version, snapshot.WrittenMemory, "offset");
        Assert.Equal(Start, live.Source.Recording.StartStep);
        for (long s = Start; s < End; s++)
        {
            live.AppendRecord(full.Recording.InputAt(s).Span);
        }

        return ((TestSource)live.Source, Fixtures.Write(live.Source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(1000)]
    public void SeekIsExactFromAStartStepForwardBackwardAndAcrossKeyframes(int interval)
    {
        (TestSource source, _) = OffsetSource();
        var history = new History(source, new ScopeHistoryOptions { KeyframeInterval = interval });
        Assert.Equal(Start, history.StartStep);
        Assert.Equal(Start, history.CurrentStep);
        Assert.Equal(End, history.EndStep);
        Assert.Equal(End - Start, history.StepCount);
        Assert.Equal(Fixtures.StraightHashes.Value[Start], history.StateHash);

        var order = new List<long>();
        for (long s = Start; s <= End; s += 13)
        {
            order.Add(s);
        }

        for (long s = End; s >= Start; s -= 29)
        {
            order.Add(s);
        }

        uint state = 7;
        for (int i = 0; i < 400; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            order.Add(Start + ((state >> 8) % (End - Start + 1)));
        }

        order.AddRange([Start, End, Start, 2000, 1999, 2001, 2048, 2047]);
        foreach (long step in order)
        {
            history.Seek(step);
            Assert.Equal(step, history.CurrentStep);
            Assert.Equal(Fixtures.StraightHashes.Value[step], history.StateHash);
        }

        Assert.All(history.KeyframeSteps, k => Assert.True(k == Start || k % interval == 0, $"keyframe at {k}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Seek(Start - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Seek(End + 1));
        Assert.False(history.Step(AxisIds.Time, -1 - (End - Start)));
        Fixtures.Report($"start-step seek interval={interval}: {order.Count} seeks from step {Start}, all hashes equal the host's");
    }

    [Fact]
    public void ForkFromAStartStepIsolatedRecordedAndReproducible()
    {
        (TestSource source, byte[] replay) = OffsetSource();
        var parent = new History(source, new ScopeHistoryOptions { KeyframeInterval = 64 });
        parent.Seek(2600);
        Branch branch = parent.Fork(2600);
        Assert.Equal(Start, branch.History.StartStep);
        branch.Poke(new PokeCall("impulse", [ScopeValue.OfEntity(new EntityId(TestModule.TestModule.FirstBodyId + 3)), ScopeValue.OfVector(new Vector3(500, 2000, -300))]));
        var forkHashes = new List<ulong> { branch.History.StateHash };
        for (int i = 0; i < 150; i++)
        {
            Assert.True(branch.Step());
            forkHashes.Add(branch.History.StateHash);
        }

        Assert.Equal(2600L, branch.DivergenceStep);
        Assert.Equal(2750, branch.History.EndStep);
        Assert.Equal(replay, Fixtures.Write(source));
        parent.Seek(3000);
        Assert.Equal(Fixtures.StraightHashes.Value[3000], parent.StateHash);

        // The fork's replay keeps its start step and reproduces its hashes, and before the divergence the host's.
        byte[] forkBytes = Fixtures.Write(branch.History.Source);
        IScopeSource reopened = Fixtures.Module.Open(forkBytes, "fork");
        Assert.Equal(Start, reopened.Recording.StartStep);
        Assert.Equal(forkBytes, Fixtures.Write(reopened));
        var replayed = new History(reopened, new ScopeHistoryOptions { KeyframeInterval = 7 });
        for (long k = Start; k <= 2750; k++)
        {
            replayed.Seek(k);
            ulong expected = k >= 2600 ? forkHashes[(int)(k - 2600)] : Fixtures.StraightHashes.Value[k];
            Assert.Equal(expected, replayed.StateHash);
        }

        // Forking at the very first and last step works; outside the range it does not.
        Assert.Equal(Fixtures.StraightHashes.Value[Start], parent.Fork(Start).History.StateHash);
        Assert.Equal(Fixtures.StraightHashes.Value[End], parent.Fork(End).History.StateHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.Fork(Start - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.Fork(End + 1));
    }

    [Fact]
    public void DiffNamesTheFirstDifferenceOnAHistoryThatStartsLate()
    {
        (TestSource source, _) = OffsetSource();
        var parent = new History(source);
        Branch branch = parent.Fork(2000);
        var body = new EntityId(TestModule.TestModule.FirstBodyId + 5);
        branch.Poke(new PokeCall("teleport", [ScopeValue.OfEntity(body), ScopeValue.OfVector(new Vector3(1234, 4321, 2222))]));
        for (int i = 0; i < 40; i++)
        {
            branch.Step();
        }

        Assert.Null(parent.Diff(branch.History, 2000));
        Difference? d = parent.Diff(branch.History, 2040);
        Assert.NotNull(d);
        Assert.Equal(2000L, branch.DivergenceStep);
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.Diff(branch.History, 1000));
    }

    [Fact]
    public void ARecordedHashThatDisagreesIsFlaggedOnceAtItsStep()
    {
        (TestSource source, _) = OffsetSource();
        ulong[] hashes = Fixtures.StraightHashes.Value[(int)Start..((int)End + 1)];
        byte[] setup = source.Recording.Setup.ToArray();
        var snapshot = new ArrayBufferWriter<byte>();
        IScopeWorld w = source.CreateWorld();
        w.WriteSnapshot(snapshot);

        byte[] Write(ulong[] rows)
        {
            var b = new ArrayBufferWriter<byte>();
            TestFormat.Write(b, setup, source.Recording, rows, snapshot.WrittenSpan.ToArray());
            return b.WrittenSpan.ToArray();
        }

        // A file whose hashes match gives no finding, even when read all the way through.
        var good = new History(Fixtures.Module.Open(Write(hashes), "good"));
        Assert.Equal(Start, good.StartStep);
        Assert.False(good.Recording.ExpectedHashes.IsEmpty);
        good.Scan();
        Assert.Null(good.FirstHashMismatch);
        Assert.DoesNotContain(good.Findings, f => f.Code == History.MismatchCode);

        // Two planted differences: only the first is reported, at its own step, as an error.
        ulong[] bad = (ulong[])hashes.Clone();
        bad[100] ^= 1;
        bad[300] ^= 1;
        var history = new History(Fixtures.Module.Open(Write(bad), "bad"));
        Assert.Null(history.FirstHashMismatch);
        history.Scan();
        Assert.Equal(Start + 100, history.FirstHashMismatch);
        Finding finding = Assert.Single(history.Findings, f => f.Code == History.MismatchCode);
        Assert.Equal(Start + 100, finding.Step);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains((Start + 100).ToString(System.Globalization.CultureInfo.InvariantCulture), finding.Text, StringComparison.Ordinal);

        // A fork of it records no hashes, since pokes make it differ on purpose.
        Branch branch = history.Fork(Start + 50);
        Assert.True(branch.History.Recording.ExpectedHashes.IsEmpty);
        Assert.Null(branch.History.FirstHashMismatch);
    }

    [Fact]
    public void ARecordingAcceptsInputsUpToTheLimitAndNoMore()
    {
        byte[] setup = TestFormat.MakeSetup(2, 4, 1);
        var big = new byte[Recording.MaxInputBytes];
        Recording r = Recording.Create(setup, [big]);
        Assert.Equal(1, r.StepCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => Recording.Create(setup, [new byte[Recording.MaxInputBytes + 1]]));
        Recording writable = r.Fork(0);
        writable.Append(big);
        Assert.Throws<ArgumentOutOfRangeException>(() => writable.Append(new byte[Recording.MaxInputBytes + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Recording.Create(setup, [], -1));
        Assert.Equal(16 << 20, Recording.MaxInputBytes);
    }

    [Fact]
    public void RecordingsWithDifferentStartsCompareWhereTheyOverlap()
    {
        byte[] setup = TestFormat.MakeSetup(2, 4, 1);
        ReadOnlyMemory<byte>[] a = [new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }];
        ReadOnlyMemory<byte>[] b = [new byte[] { 3 }, new byte[] { 9 }];
        Recording ra = Recording.Create(setup, a);
        Recording rb = Recording.Create(setup, b, startStep: 2);
        Assert.Equal(3L, ra.FirstDifference(rb));
        Assert.Equal(3L, rb.FirstDifference(ra));
        Assert.Equal(4, rb.EndStep);
        Assert.Equal(new byte[] { 9 }, rb.InputAt(3).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => rb.InputAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rb.InputAt(4));
    }

    [Fact]
    public void AStartSnapshotTooShortToNameItsStepIsRefused()
    {
        byte[] setup = TestFormat.MakeSetup(2, 4, 1);
        var w = new ArrayBufferWriter<byte>();
        TestFormat.Write(w, setup, Recording.Create(setup, []), null, new byte[3]);
        ScopeFormatException ex = Assert.Throws<ScopeFormatException>(() => Fixtures.Module.Open(w.WrittenMemory, "short.ostm"));
        Assert.Contains("short.ostm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 8 bytes", ex.Message, StringComparison.Ordinal);
    }
}
