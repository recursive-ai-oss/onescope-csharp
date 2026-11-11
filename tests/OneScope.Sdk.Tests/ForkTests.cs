using System.Numerics;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class ForkTests
{
    private static PokeCall Impulse(int body) =>
        new("impulse", [ScopeValue.OfEntity(new EntityId(TestModule.TestModule.FirstBodyId + (ulong)body)), ScopeValue.OfVector(new Vector3(500, 2000, -300))]);

    [Fact]
    public void ForkIsIsolatedRecordedAndReproducible()
    {
        TestSource source = Fixtures.OpenLong();
        byte[] originalBytes = Fixtures.Write(source);
        var parent = new History(source, new ScopeHistoryOptions { KeyframeInterval = 64 });
        parent.Seek(3000);

        Branch branch = parent.Fork(3000);
        Assert.Null(branch.DivergenceStep);
        Assert.Equal(3000, branch.History.CurrentStep);
        Assert.Equal(Fixtures.StraightHashes.Value[3000], branch.History.StateHash);

        branch.Poke(Impulse(3));
        Assert.Equal(1, branch.PendingPokes);
        var forkHashes = new List<ulong> { branch.History.StateHash };
        for (int i = 0; i < 200; i++)
        {
            Assert.True(branch.Step());
            forkHashes.Add(branch.History.StateHash);
        }

        Assert.Equal(3200, branch.History.StepCount);
        Assert.Equal(3000L, branch.DivergenceStep);
        Assert.Single(parent.Branches);

        // Original: bytes, cursor and per-step hashes unchanged.
        Assert.Equal(originalBytes, Fixtures.Write(source));
        Assert.Equal(3000, parent.CurrentStep);
        Assert.Equal(10_000, parent.StepCount);
        foreach (long k in new long[] { 0, 2999, 3000, 3001, 3200, 9999, 10_000 })
        {
            parent.Seek(k);
            Assert.Equal(Fixtures.StraightHashes.Value[k], parent.StateHash);
        }

        // The fork's replay round-trips byte for byte.
        byte[] forkBytes = Fixtures.Write(branch.History.Source);
        IScopeSource reopened = Fixtures.Module.Open(forkBytes, "fork.ostm");
        Assert.Equal(forkBytes, Fixtures.Write(reopened));

        // Replaying it reproduces the fork's hashes, and before the divergence the original's.
        var replay = new History(reopened, new ScopeHistoryOptions { KeyframeInterval = 7 });
        for (long k = 0; k <= 3200; k++)
        {
            replay.Seek(k);
            ulong expected = k >= 3000 ? forkHashes[(int)(k - 3000)] : Fixtures.StraightHashes.Value[k];
            Assert.Equal(expected, replay.StateHash);
        }

        // Diff: equal through the divergence step, different on the next, and it names the poked body.
        Assert.Null(parent.Diff(branch.History, 3000));
        Difference? d = parent.Diff(branch.History, 3001);
        Assert.NotNull(d);
        Assert.Equal(new EntityId(TestModule.TestModule.FirstBodyId + 3), d.Value.Entity);
        Assert.Equal("position.x", d.Value.Field);
        Fixtures.Report($"fork: at 3000 poke impulse(body 3), 200 steps; replay equal at all 3201 steps; divergence step {branch.DivergenceStep}; first diff at step 3001: entity {d.Value.Entity} field {d.Value.Field} {d.Value.Left} vs {d.Value.Right}; original bytes unchanged ({originalBytes.Length} bytes), fork replay {forkBytes.Length} bytes round-trips");
    }

    [Fact]
    public void DivergenceIsTheFirstDifferentInputNotTheForkStep()
    {
        var parent = new History(Fixtures.OpenLong());
        Branch branch = parent.Fork(500);
        for (int i = 0; i < 10; i++)
        {
            branch.Step();
        }

        Assert.Null(branch.DivergenceStep);
        branch.Poke(Impulse(1));
        branch.Step();
        Assert.Equal(510L, branch.DivergenceStep);
    }

    [Fact]
    public void ForkAtTheEndExtendsPastTheOriginal()
    {
        var parent = new History(Fixtures.OpenLong());
        Branch branch = parent.Fork(10_000);
        branch.Step();
        Assert.Equal(10_001, branch.History.StepCount);
        Assert.Equal(10_000, parent.StepCount);
        Assert.Null(branch.DivergenceStep);
    }

    [Fact]
    public void PokesAreValidatedAndNeedTheEndOfTheRecording()
    {
        var parent = new History(Fixtures.OpenLong());
        Branch branch = parent.Fork(100);
        Assert.Throws<ArgumentException>(() => branch.Poke(new PokeCall("nope", [])));
        Assert.Throws<ArgumentException>(() => branch.Poke(new PokeCall("remove", [ScopeValue.OfEntity(new EntityId(9999))])));
        Assert.Throws<ArgumentException>(() => branch.Poke(new PokeCall("set_gravity", [ScopeValue.OfText("x")])));
        Assert.Equal(0, branch.PendingPokes);
        branch.Step();
        branch.History.Seek(100);
        Assert.Throws<InvalidOperationException>(() => branch.Poke(Impulse(0)));
        Assert.Throws<InvalidOperationException>(() => parent.Recording.Append([1]));
    }

    [Fact]
    public void PokesCoverEveryDeclaredKind()
    {
        var parent = new History(Fixtures.OpenLong());
        Branch branch = parent.Fork(50);
        branch.Poke(new PokeCall("teleport", [ScopeValue.OfEntity(new EntityId(100)), ScopeValue.OfVector(new Vector3(1000, 1000, 1000))]));
        branch.Poke(new PokeCall("remove", [ScopeValue.OfEntity(new EntityId(101))]));
        branch.Poke(new PokeCall("set_gravity", [ScopeValue.OfInteger(-9)]));
        branch.Step();
        var entities = new List<EntityInfo>();
        branch.History.World.CollectEntities(entities);
        Assert.DoesNotContain(entities, e => e.Id.Value == 101);
        var props = new PropertyList();
        Assert.True(branch.History.World.Inspect(TestModule.TestModule.World, props));
        Assert.Contains(props.Rows, r => r.Name == "gravity" && r.Value.GetInteger() == -9);
        Assert.Equal(14 + 2 + 5, branch.History.Recording.InputAt(50).Length);
    }
}
