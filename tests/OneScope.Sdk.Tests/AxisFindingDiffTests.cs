using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class AxisFindingDiffTests
{
    private static ulong Reference(long step, int position)
    {
        TestSource source = Fixtures.OpenLong();
        IScopeWorld world = source.CreateWorld();
        for (long s = 0; s < step; s++)
        {
            world.Advance(source.Recording.InputAt(s).Span, TimelineSink.None);
        }

        world.AdvanceAxis(TestModule.TestModule.StageAxis, position, source.Recording.InputAt(step).Span);
        return world.Hash();
    }

    [Fact]
    public void StageAxisIsExactByAnyPathAndLeavesTimeAlone()
    {
        const string stage = TestModule.TestModule.StageAxis;
        var h = new History(Fixtures.OpenLong(), new HistoryOptions { KeyframeInterval = 64 });
        var lines = new List<string>();
        foreach (long step in new long[] { 0, 1, 63, 64, 65, 4321, 9999 })
        {
            ulong[] reference = Enumerable.Range(0, 5).Select(p => Reference(step, p)).ToArray();
            h.Seek(step);
            ulong timeHash = h.StateHash;
            Assert.Equal(Fixtures.StraightHashes.Value[step], timeHash);
            foreach (int p in new[] { 3, 1, 4, 2, 0, 4, 4, 1 })
            {
                h.Seek(stage, p);
                Assert.Equal(reference[p], h.View.Hash());
                Assert.Equal(timeHash, h.StateHash);
                Assert.Equal(step, h.CurrentStep);
                Assert.Equal(step, h.View.Step);
                Assert.Equal(p == 0 ? AxisIds.Time : stage, h.ViewAxis);
            }

            // Leave the axis, travel in time, come back: same hashes.
            h.Seek(stage, 2);
            h.Seek((step + 777) % 10_000);
            h.Seek(step);
            h.Seek(stage, 2);
            Assert.Equal(reference[2], h.View.Hash());
            Assert.Equal(timeHash, h.StateHash);
            lines.Add($"{step}:[{string.Join(',', reference.Select(r => r.ToString("x8")[..8]))}]");
        }

        Fixtures.Report("axis stage (position hashes 0..4, first 8 hex) " + string.Join(' ', lines));
    }

    [Fact]
    public void StepAlongAnAxisMovesByPosition()
    {
        var h = new History(Fixtures.OpenLong());
        h.Seek(10);
        Assert.True(h.Step(TestModule.TestModule.StageAxis));
        Assert.True(h.Step(TestModule.TestModule.StageAxis, 2));
        Assert.Equal(3, h.ViewPosition);
        Assert.True(h.Step(TestModule.TestModule.StageAxis, -3));
        Assert.Equal(AxisIds.Time, h.ViewAxis);
        Assert.False(h.Step(TestModule.TestModule.StageAxis, -1));
        Assert.False(h.Step(TestModule.TestModule.StageAxis, 5));
        Assert.Throws<ArgumentException>(() => h.Seek("nope", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Seek(TestModule.TestModule.StageAxis, 5));
        h.Seek(10_000);
        Assert.Throws<InvalidOperationException>(() => h.Seek(TestModule.TestModule.StageAxis, 1));
    }

    [Fact]
    public void FindingsComeBackAtTheirKnownStepsAndTheSourceReportsItsUnit()
    {
        var h = new History(Fixtures.OpenLong());
        Assert.Equal(LengthUnit.Millimetre, h.Source.Info.Unit);
        Assert.Equal(0.001, h.Source.Info.Unit.Metres);
        h.Scan();
        Assert.Equal(0, h.CurrentStep);
        Assert.Equal(10_000, h.ScannedThrough);
        Assert.Equal([2500L, 5000, 7500, 10_000], h.Findings.Select(f => f.Step));
        Assert.All(h.Findings, f =>
        {
            Assert.Equal("TM001", f.Code);
            Assert.Single(f.Entities);
            Assert.StartsWith("Total kinetic energy is ", f.Text);
            Assert.Equal("Use set_gravity with a smaller magnitude.", f.SuggestedChange);
        });
        Assert.Equal([Severity.Warning, Severity.Error, Severity.Warning, Severity.Error], h.Findings.Select(f => f.Severity));
        Assert.Equal(new EntityId(TestModule.TestModule.FirstBodyId + 1), h.Findings[0].Entities[0]);

        TrackData energy = h.Tracks.Single(t => t.Info.Id == "energy");
        Assert.Equal(10_000, energy.Samples.Count);
        Assert.Equal(1L, energy.Samples[0].Step);
        Assert.Equal(10, h.Tracks.Single(t => t.Info.Id == "phase").Samples.Count);
        Assert.NotEmpty(h.Tracks.Single(t => t.Info.Id == "bounce").Samples);

        // Re-simulating (seeking around) does not record anything twice.
        h.Seek(9000);
        h.Seek(100);
        h.Scan();
        Assert.Equal(4, h.Findings.Count);
        Assert.Equal(10_000, energy.Samples.Count);
        Fixtures.Report("findings: " + string.Join("; ", h.Findings.Select(f => $"step {f.Step} {f.Code} {f.Severity} entity {f.Entities[0]} '{f.Text}'")) + "; unit " + h.Source.Info.Unit.Name);
    }

    [Fact]
    public void DiffNamesTheFirstDifferingEntityAndField()
    {
        TestSource source = Fixtures.OpenLong();
        var h = new History(source);
        h.Seek(500);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        h.World.WriteSnapshot(buffer);
        byte[] tampered = buffer.WrittenSpan.ToArray();
        int body = 5;
        int py = 12 + body * 32 + 12;
        tampered[py] ^= 1;
        IScopeWorld other = source.CreateWorld();
        other.Restore(tampered);

        Assert.Null(h.World.FirstDifference(h.World));
        Difference? d = h.World.FirstDifference(other);
        Assert.NotNull(d);
        Assert.Equal(new EntityId(TestModule.TestModule.FirstBodyId + 5), d.Value.Entity);
        Assert.Equal("position.y", d.Value.Field);
        Assert.NotEqual(d.Value.Left, d.Value.Right);
        Fixtures.Report($"diff planted body 5 position.y: entity {d.Value.Entity} field {d.Value.Field} {d.Value.Left} vs {d.Value.Right}");
    }

    [Fact]
    public void RestoreRejectsMalformedSnapshots()
    {
        IScopeWorld w = Fixtures.OpenLong().CreateWorld();
        Assert.Throws<ScopeFormatException>(() => w.Restore(new byte[10]));
        var good = new System.Buffers.ArrayBufferWriter<byte>();
        w.WriteSnapshot(good);
        byte[] bad = good.WrittenSpan.ToArray();
        bad[12] = 7;
        var ex = Assert.Throws<ScopeFormatException>(() => w.Restore(bad));
        Assert.Contains("alive", ex.Message);
    }
}
