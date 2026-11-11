using System.Numerics;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

public class DrawListTests
{
    [Fact]
    public void EveryPrimitiveKindIsRecorded()
    {
        var list = new DrawList();
        var style = new DrawStyle(Color32.White, 5, 2, DrawFlags.Wireframe);
        list.Line(Vector3.Zero, Vector3.One, style);
        list.Point(Vector3.One, 3, style);
        list.Triangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, style);
        list.Mesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], style);
        list.Mesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [0, 1, 2], style);
        list.Box(Vector3.Zero, Vector3.One, Quaternion.Identity, style);
        list.Sphere(Vector3.Zero, 2, style);
        list.Capsule(Vector3.Zero, Vector3.UnitY, 1, style);
        list.Cylinder(Vector3.Zero, Vector3.UnitY, 1, style);
        list.Arrow(Vector3.Zero, Vector3.UnitX, 0.1f, style);
        list.Label(Vector3.One, "hello", style);
        Assert.Equal(
            [DrawKind.Line, DrawKind.Point, DrawKind.Triangle, DrawKind.Mesh, DrawKind.Mesh, DrawKind.Box, DrawKind.Sphere, DrawKind.Capsule, DrawKind.Cylinder, DrawKind.Arrow, DrawKind.Label],
            list.Items.ToArray().Select(i => i.Kind));
        Assert.Equal([0, 1, 2, 3, 4, 5], list.Indices.ToArray());
        Assert.Equal("hello", list.TextOf(list.Items[^1]).ToString());
        Assert.Equal(5, list.Items[0].Overlay);
        Assert.Equal(DrawFlags.Wireframe, list.Items[0].Flags);
        Assert.Equal(0, list.Dropped);
    }

    [Fact]
    public void EveryItemCarriesTheEntityItShows()
    {
        var list = new DrawList();
        var style = new DrawStyle(Color32.White, 0, 0, DrawFlags.None, new EntityId(42));
        list.Line(Vector3.Zero, Vector3.One, style);
        list.Sphere(Vector3.Zero, 1, style);
        list.Label(Vector3.One, "x", style);
        list.Box(Vector3.Zero, Vector3.One, Quaternion.Identity, new DrawStyle(Color32.White));
        Assert.All(list.Items[..3].ToArray(), i => Assert.Equal(42UL, i.Entity.Value));
        Assert.False(list.Items[3].Entity.IsSome);
        list.Clear();
        list.Line(Vector3.Zero, Vector3.One, new DrawStyle(Color32.White));
        Assert.False(list.Items[0].Entity.IsSome);
    }

    [Fact]
    public void ThePokeRoleIsDeclaredByTheModuleAndDefaultsToNone()
    {
        var module = new TestModule.TestModule();
        var info = module.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "test-2d-600.ostm")), "t").Info;
        Assert.Equal(PokeRole.DragToPoint, info.Pokes.Single(p => p.Id == "teleport").Role);
        Assert.All(info.Pokes.Where(p => p.Id != "teleport"), p => Assert.Equal(PokeRole.None, p.Role));
    }

    [Fact]
    public void BadMeshesAreDroppedAndCounted()
    {
        var list = new DrawList();
        var style = new DrawStyle(Color32.White);
        list.Mesh([Vector3.Zero], [0, 0, 5], style);
        list.Mesh([Vector3.Zero], [0, 0], style);
        Assert.Empty(list.Items.ToArray());
        Assert.Equal(2, list.Dropped);
        list.Clear();
        Assert.Equal(0, list.Dropped);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    public void FillingTheListAllocatesNothingInSteadyState(int dims)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", dims == 3 ? "test-10000.ostm" : "test-2d-600.ostm");
        IScopeSource source = Fixtures.Module.Open(File.ReadAllBytes(path), "d");
        var h = new History(source);
        h.Seek(300);
        var list = new DrawList();
        var request = new DrawRequest(0b1111);
        h.World.Draw(list, request);
        int items = list.Items.Length;
        Assert.True(items > 24);
        for (int i = 0; i < 3; i++)
        {
            list.Clear();
            h.World.Draw(list, request);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            list.Clear();
            h.World.Draw(list, request);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(items, list.Items.Length);
        Fixtures.Report($"draw {dims}D: {items} items per frame, 0 bytes allocated over 100 frames after warm-up");
    }

    [Fact]
    public void TheTestModuleDrawsThe3DPrimitivesAnd2DOnes()
    {
        var kinds3 = Kinds("test-10000.ostm");
        var kinds2 = Kinds("test-2d-600.ostm");
        Assert.Superset(new HashSet<DrawKind> { DrawKind.Sphere, DrawKind.Box, DrawKind.Capsule, DrawKind.Cylinder, DrawKind.Arrow, DrawKind.Label, DrawKind.Line }, kinds3);
        Assert.Superset(new HashSet<DrawKind> { DrawKind.Triangle, DrawKind.Point, DrawKind.Arrow, DrawKind.Label, DrawKind.Line }, kinds2);

        static HashSet<DrawKind> Kinds(string file)
        {
            var h = new History(Fixtures.Module.Open(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", file)), file));
            h.Seek(100);
            var list = new DrawList();
            h.World.Draw(list, new DrawRequest(0b1111));
            return [.. list.Items.ToArray().Select(i => i.Kind)];
        }
    }
}
