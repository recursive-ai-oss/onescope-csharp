using System.Numerics;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class FieldLinkDiffTests
{
    private static readonly FieldInfo Range = new("f", "F", ColorMap.Viridis, 10, 20, "u");

    [Fact]
    public void ColorMapsHitTheirEndsClampAndMarkMissingData()
    {
        foreach (ColorMap map in Enum.GetValues<ColorMap>())
        {
            Assert.Equal(ColorMaps.Evaluate(map, 0f), ColorMaps.Evaluate(map, -5f));
            Assert.Equal(ColorMaps.Evaluate(map, 1f), ColorMaps.Evaluate(map, 9f));
            Assert.NotEqual(ColorMaps.Evaluate(map, 0f), ColorMaps.Evaluate(map, 1f));
            Assert.Equal(ColorMaps.NoData, ColorMaps.Evaluate(map, float.NaN));
        }

        Assert.Equal(new Color32(68, 1, 84), ColorMaps.Evaluate(ColorMap.Viridis, 0f));
        Assert.Equal(ColorMaps.Evaluate(ColorMap.Viridis, 0f), ColorMaps.Evaluate(Range, 10f));
        Assert.Equal(ColorMaps.Evaluate(ColorMap.Viridis, 1f), ColorMaps.Evaluate(Range, 99f));
        Assert.Equal(0.5f, ColorMaps.Normalize(new FieldInfo("e", "E", ColorMap.Heat, 3, 3), 7f));
    }

    [Fact]
    public void FieldMeshesKeepTheirValuesPerVertexOrPerTriangleAndRejectWrongCounts()
    {
        var list = new DrawList();
        Vector3[] v = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        int[] idx = [0, 1, 2, 0, 2, 3];
        var style = new DrawStyle(Color32.Grey);
        list.FieldMesh(v, idx, [10f, 12f, 20f, 15f], FieldDomain.Vertex, Range, style);
        list.FieldMesh(v, idx, [11f, 19f], FieldDomain.Element, Range, style);
        Assert.Equal(0, list.Dropped);
        Assert.Equal(2, list.FieldBindings.Count);
        DrawItem a = list.Items[0];
        DrawItem b = list.Items[1];
        Assert.Equal(1, a.Field);
        Assert.Equal(2, b.Field);

        // Vertex field: the second triangle's third corner is vertex 3.
        Assert.Equal(20f, list.FieldValueAt(a, a.First + 2));
        Assert.Equal(15f, list.FieldValueAt(a, a.First + 5));
        // Element field: every corner of triangle 1 reads its one value.
        Assert.Equal(19f, list.FieldValueAt(b, b.First + 3));
        Assert.Equal(19f, list.FieldValueAt(b, b.First + 5));
        Assert.True(float.IsNaN(list.FieldValueAt(b, b.First + 6)));
        Assert.Equal(ColorMaps.Evaluate(Range, 19f), list.FieldColorAt(b, b.First + 4));

        list.FieldMesh(v, idx, [1f], FieldDomain.Vertex, Range, style);
        list.FieldMesh(v, idx, [1f, 2f, 3f], FieldDomain.Element, Range, style);
        Assert.Equal(2, list.Dropped);
        Assert.Equal(2, list.Items.Length);

        list.Clear();
        Assert.Empty(list.FieldBindings);
        Assert.Equal(0, list.FieldValues.Length);
    }

    [Fact]
    public void FieldPointsTakeTheirColourFromTheirValue()
    {
        var list = new DrawList();
        list.FieldPoints([Vector3.Zero, Vector3.One], [10f, 20f], Range, 5, new DrawStyle(Color32.Grey));
        Assert.Equal(2, list.Items.Length);
        Assert.Equal(ColorMaps.Evaluate(Range, 20f), list.Items[1].Color);
        Assert.Equal(20f, list.FieldValueOf(list.Items[1]));
        list.FieldPoints([Vector3.Zero], [1f, 2f], Range, 5, new DrawStyle(Color32.Grey));
        Assert.Equal(1, list.Dropped);
    }

    [Fact]
    public void FillingAFieldMeshAllocatesNothingOnceWarm()
    {
        var list = new DrawList();
        Vector3[] v = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        int[] idx = [0, 1, 2];
        float[] values = [1f, 2f, 3f];
        for (int i = 0; i < 10; i++)
        {
            list.Clear();
            list.FieldMesh(v, idx, values, FieldDomain.Vertex, Range, new DrawStyle(Color32.Grey));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            list.Clear();
            list.FieldMesh(v, idx, values, FieldDomain.Vertex, Range, new DrawStyle(Color32.Grey));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static TestWorld World(byte[] file, long steps)
    {
        TestSource s = (TestSource)Fixtures.Module.Open(file, "t");
        var w = (TestWorld)s.CreateWorld();
        for (long i = 0; i < steps; i++)
        {
            w.Advance(s.Recording.InputAt(i).Span, TimelineSink.None);
        }

        return w;
    }

    private static byte[] Small() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-2d-600.ostm"));

    [Fact]
    public void TheTestModuleFieldAtAVertexIsTheFieldAtThatPoint()
    {
        TestWorld w = World(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-field-2d.ostm")), 120);
        var list = new DrawList();
        w.Draw(list, new DrawRequest(1UL << TestModule.TestModule.DensityOverlay));
        DrawItem item = list.Items[0];
        Assert.Single(list.FieldBindings);
        for (int k = 0; k < 9; k++)
        {
            int idx = list.Indices[item.First + (k * 3)];
            Vector3 p = list.Vertices[idx];
            Assert.True(w.TryReadFieldAt("density", p, out double value));
            Assert.Equal((float)value, list.FieldValueAt(item, item.First + (k * 3)));
        }

        Assert.True(w.TryReadField("density", new EntityId(100), out double atBody));
        Assert.True(atBody >= 1.0);
        Assert.False(w.TryReadField("density", new EntityId(9999), out _));
        Assert.False(w.TryReadField("nope", new EntityId(100), out _));
        Assert.False(w.TryReadFieldAt("density", new Vector3(-1, 0, 0), out _));
    }

    [Fact]
    public void LinkedWorldsDeclareTheTransferFromBothSides()
    {
        TestWorld left = World(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-link-left.ostm")), 10);
        TestWorld right = World(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-link-right.ostm")), 10);
        var l = new List<EntityLink>();
        var r = new List<EntityLink>();
        left.CollectLinks(l);
        right.CollectLinks(r);
        var body = new EntityId(103);
        Assert.Equal([new EntityLink(body, LinkDirection.ContinuedBy, TestModule.TestModule.RightKey, body, 150)], l);
        Assert.Equal([new EntityLink(body, LinkDirection.Continues, TestModule.TestModule.LeftKey, body, 150)], r);

        // The body is in the left world before step 150 and in the right world from it.
        var info = new List<EntityInfo>();
        left.CollectEntities(info);
        Assert.Contains(info, e => e.Id == body);
        right.CollectEntities(info);
        Assert.DoesNotContain(info, e => e.Id == body);
        right = World(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-link-right.ostm")), 150);
        right.CollectEntities(info);
        Assert.Contains(info, e => e.Id == body);
        left = World(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-link-left.ostm")), 150);
        left.CollectEntities(info);
        Assert.DoesNotContain(info, e => e.Id == body);
    }

    [Fact]
    public void ASharedDiffIgnoresEntitiesOnlyTheWholeWorldHoldsAndNamesAPlantedDifference()
    {
        // A fork with body 105 removed is the partial world; the original is the whole one.
        TestSource whole = (TestSource)Fixtures.Module.Open(Small(), "whole");
        var history = new History(whole, new ScopeHistoryOptions { KeyframeInterval = 64 });
        Branch partial = history.Fork(100);
        partial.Poke(new PokeCall("remove", [ScopeValue.OfEntity(new EntityId(105))]));
        partial.Step();
        partial.Poke(new PokeCall("remove", [ScopeValue.OfEntity(new EntityId(106))]));
        partial.Step();
        partial.Step();
        history.Seek(103);
        partial.History.Seek(103);

        SharedDifference d = partial.History.World.CompareShared(history.World, EntityMap.Identity);
        Assert.Null(d.First);
        // 12 shared entities: ten bodies, the floor and the zone.
        Assert.Equal(12, d.Shared);
        Assert.Equal(0, d.OnlyLeft);
        Assert.Equal(2, d.OnlyRight);
        // The ordinary diff sees the removed bodies as a difference; the shared one does not.
        Assert.NotNull(partial.History.World.FirstDifference(history.World));

        // Plant a difference in a body both worlds hold.
        Branch planted = history.Fork(100);
        planted.Poke(new PokeCall("remove", [ScopeValue.OfEntity(new EntityId(105))]));
        planted.Poke(new PokeCall("impulse", [ScopeValue.OfEntity(new EntityId(102)), ScopeValue.OfVector(new Vector3(500, 0, 0))]));
        planted.Step();
        planted.Step();
        planted.Step();
        history.Seek(103);
        planted.History.Seek(103);
        SharedDifference p = planted.History.World.CompareShared(history.World, EntityMap.Identity);
        Assert.NotNull(p.First);
        Assert.Equal(new EntityId(102), p.First!.Value.Entity);
        Assert.Equal("position", p.First.Value.Field);
        Assert.Equal(1, p.OnlyRight);
    }

    [Fact]
    public void AnExplicitMapPairsDifferentIdsAndLeavesTheRestUnshared()
    {
        TestWorld a = World(Small(), 50);
        TestWorld b = World(Small(), 50);
        var map = EntityMap.FromPairs([new(new EntityId(100), new EntityId(101)), new(new EntityId(101), new EntityId(100))]);
        SharedDifference d = ((IScopeWorld)a).CompareShared(b, map);
        Assert.Equal(2, d.Shared);
        Assert.Equal(12, d.OnlyLeft);
        Assert.Equal(12, d.OnlyRight);
        Assert.NotNull(d.First);
        Assert.Equal(new EntityId(100), d.First!.Value.Entity);

        Assert.Throws<ArgumentException>(() => EntityMap.FromPairs([new(new EntityId(1), new EntityId(2)), new(new EntityId(1), new EntityId(3))]));
        Assert.Throws<ArgumentException>(() => EntityMap.FromPairs([new(EntityId.None, new EntityId(3))]));
    }
}
