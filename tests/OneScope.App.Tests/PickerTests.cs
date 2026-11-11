using System.Numerics;
using OneScope.Sdk;
using Xunit;

namespace OneScope.App.Tests;

public sealed class PickerTests
{
    private static EntityId E(ulong id) => new(id);

    private static int[] Order(DrawList l) => Enumerable.Range(0, l.Items.Length).ToArray();

    [Fact]
    public void ThePlaneFindsTheTopmostShapeUnderThePoint()
    {
        var list = new DrawList();
        list.Box(new Vector3(0, 0, 0), new Vector3(2, 1, 0), Quaternion.Identity, new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(1)));
        list.Sphere(new Vector3(1, 0, 0), 1, new DrawStyle(Color32.White, 0, 1, DrawFlags.None, E(2)));
        list.Triangle(new Vector3(10, 0, 0), new Vector3(12, 0, 0), new Vector3(11, 2, 0), new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(3)));
        list.Line(new Vector3(-5, -5, 0), new Vector3(5, -5, 0), new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(4)));
        list.Label(new Vector3(30, 30, 0), "x", new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(5)));
        list.Box(new Vector3(20, 0, 0), new Vector3(3, 3, 0), Quaternion.Identity, new DrawStyle(Color32.White, 0, 0, DrawFlags.Wireframe, E(6)));
        int[] order = Order(list);
        Assert.Equal(2UL, Picker.Pick2D(list, order, order.Length, new Vector2(1, 0), 0.1f).Value);
        Assert.Equal(1UL, Picker.Pick2D(list, order, order.Length, new Vector2(-1.9f, 0.9f), 0.1f).Value);
        Assert.Equal(3UL, Picker.Pick2D(list, order, order.Length, new Vector2(11, 0.5f), 0.1f).Value);
        Assert.Equal(4UL, Picker.Pick2D(list, order, order.Length, new Vector2(0, -5.05f), 0.1f).Value);
        Assert.False(Picker.Pick2D(list, order, order.Length, new Vector2(0, -5.5f), 0.1f).IsSome);
        Assert.False(Picker.Pick2D(list, order, order.Length, new Vector2(30, 30), 0.1f).IsSome, "labels are not pickable");
        Assert.False(Picker.Pick2D(list, order, order.Length, new Vector2(20, 0), 0.1f).IsSome, "a wireframe box is hollow");
    }

    [Fact]
    public void TheRayFindsTheNearestShapeAndTheHitPoint()
    {
        var list = new DrawList();
        list.Sphere(new Vector3(0, 0, 10), 1, new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(1)));
        list.Box(new Vector3(0, 0, 5), new Vector3(1, 1, 1), Quaternion.Identity, new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(2)));
        list.Capsule(new Vector3(5, -3, 8), new Vector3(5, 3, 8), 0.5f, new DrawStyle(Color32.White, 0, 0, DrawFlags.None, E(3)));
        EntityId hit = Picker.Pick3D(list, Vector3.Zero, Vector3.UnitZ, 0.01f, out Vector3 at);
        Assert.Equal(2UL, hit.Value);
        Assert.Equal(4f, at.Z, 4);
        Assert.Equal(3UL, Picker.Pick3D(list, new Vector3(5.2f, 0, 0), Vector3.UnitZ, 0.01f, out _).Value);
        Assert.False(Picker.Pick3D(list, new Vector3(50, 0, 0), Vector3.UnitZ, 0.01f, out _).IsSome);
        Assert.False(Picker.Pick3D(list, new Vector3(0, 0, 20), Vector3.UnitZ, 0.01f, out _).IsSome, "shapes behind the ray's origin are not hit");
    }

    [Fact]
    public void TheCameraRayThroughAProjectedPointComesBackToThatPoint()
    {
        var view = new ViewState { Kind = ViewKind.Space3D, Target = new Vector3(5000, 1200, 5000), Distance = 6500, Yaw = 35, Pitch = 28 };
        var rect = new OneGame.UI.Rect(100, 50, 400, 300);
        var world = new Vector3(4200, 900, 6100);
        var cam = Space3DRenderer.MakeCamera(view);
        Vector4 c = Vector4.Transform(new Vector4(world, 1), cam.View * cam.Projection(rect.Width / rect.Height));
        float sx = rect.X + (((c.X / c.W) * 0.5f) + 0.5f) * rect.Width;
        float sy = rect.Y + (1 - (((c.Y / c.W) * 0.5f) + 0.5f)) * rect.Height;
        ViewPicking.Ray(view, rect, sx, sy, out Vector3 origin, out Vector3 dir);
        // The point lies on the ray: the distance from it to the line is tiny against the scene's size.
        float off = Vector3.Cross(world - origin, dir).Length();
        Assert.True(off < 5, $"ray misses the point by {off}");
    }
}
