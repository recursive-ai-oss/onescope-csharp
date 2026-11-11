using System.Numerics;
using OneScope.Sdk;
using UiRect = OneGame.UI.Rect;

namespace OneScope.App;

/// <summary>
/// Turns a pointer position in a scene view into the entity under it and a point in the world, using the same camera maths the renderers use.
/// 2D views read the pointer as a point on the plane; 3D views as a ray, and a drag moves the entity on the plane through the point first hit,
/// facing the camera.
/// </summary>
internal static class ViewPicking
{
    /// <summary>A pointer position resolved against a view's camera.</summary>
    internal readonly record struct Hit(EntityId Entity, Vector3 Point, Vector3 PlaneNormal);

    /// <summary>The rectangle a view is drawn into, rounded the way the stage rounds it so picking and drawing agree.</summary>
    public static UiRect Round(UiRect bounds) => new(MathF.Round(bounds.X), MathF.Round(bounds.Y), MathF.Round(bounds.Width), MathF.Round(bounds.Height));

    /// <summary>Finds the entity under screen position (<paramref name="x"/>, <paramref name="y"/>) of the view drawn into <paramref name="rect"/>.</summary>
    public static Hit Pick(ShellModel shell, ViewState view, UiRect rect, float x, float y)
    {
        SourceSession source = shell.Source(view.Source);
        var list = new DrawList();
        source.History.View.Draw(list, new DrawRequest(view.Overlays));
        if (view.Kind == ViewKind.Plane2D)
        {
            float ppu = Math.Min(rect.Width, rect.Height) / (2 * Math.Max(view.HalfHeight, 1e-9f));
            Vector3 world = PointOn2D(view, rect, x, y);
            int n = list.Items.Length;
            int[] order = LayerOrder(list);
            EntityId hit = Picker.Pick2D(list, order, n, new Vector2(world.X, world.Y), 4 / ppu);
            return new Hit(hit, world, Vector3.UnitZ);
        }

        Ray(view, rect, x, y, out Vector3 origin, out Vector3 dir);
        float perPixel = 2 * view.Distance * MathF.Tan(0.35f) / Math.Max(rect.Height, 1);
        EntityId found = Picker.Pick3D(list, origin, dir, 4 * perPixel, out Vector3 point);
        Ray(view, rect, rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2), out _, out Vector3 forward);
        return new Hit(found, found.IsSome ? point : origin + (dir * view.Distance), -forward);
    }

    /// <summary>
    /// Where an entity is on screen in a view: the centre of its first solid or point item at the source's cursor. False when the entity draws nothing
    /// in the view (not in the source, or its overlay is off). The position may lie outside <paramref name="rect"/> when the camera looks elsewhere.
    /// </summary>
    public static bool Project(ShellModel shell, ViewState view, UiRect rect, EntityId entity, out Vector2 screen, out Vector3 world)
    {
        SourceSession source = shell.Source(view.Source);
        var list = new DrawList();
        source.History.View.Draw(list, new DrawRequest(view.Overlays));
        foreach (ref readonly DrawItem item in list.Items)
        {
            if (item.Entity != entity || item.Kind is DrawKind.Label)
            {
                continue;
            }

            world = item.Kind == DrawKind.Triangle ? (item.A + item.B + item.C) / 3 : item.A;
            screen = ToScreen(view, rect, world);
            return true;
        }

        screen = default;
        world = default;
        return false;
    }

    /// <summary>Where a world point lands on screen in a view drawn into <paramref name="rect"/>.</summary>
    public static Vector2 ToScreen(ViewState view, UiRect rect, Vector3 world)
    {
        if (view.Kind == ViewKind.Plane2D)
        {
            float ppu = Math.Min(rect.Width, rect.Height) / (2 * Math.Max(view.HalfHeight, 1e-9f));
            return new Vector2(rect.X + (rect.Width / 2) + ((world.X - view.Target.X) * ppu), rect.Y + (rect.Height / 2) - ((world.Y - view.Target.Y) * ppu));
        }

        var camera = Space3DRenderer.MakeCamera(view);
        Vector4 c = Vector4.Transform(new Vector4(world, 1), camera.View * camera.Projection(rect.Width / Math.Max(rect.Height, 1)));
        float x = c.X / c.W;
        float y = c.Y / c.W;
        return new Vector2(rect.X + (((x * 0.5f) + 0.5f) * rect.Width), rect.Y + ((1 - ((y * 0.5f) + 0.5f)) * rect.Height));
    }

    /// <summary>The world point under the pointer in a 2D view (Z = 0).</summary>
    public static Vector3 PointOn2D(ViewState view, UiRect rect, float x, float y)
    {
        float ppu = Math.Min(rect.Width, rect.Height) / (2 * Math.Max(view.HalfHeight, 1e-9f));
        return new Vector3(
            view.Target.X + ((x - (rect.X + (rect.Width / 2))) / ppu),
            view.Target.Y - ((y - (rect.Y + (rect.Height / 2))) / ppu),
            0);
    }

    /// <summary>Where the pointer's ray meets the plane through <paramref name="origin"/> with <paramref name="normal"/>; null when the ray is parallel to it or points away.</summary>
    public static Vector3? PointOnPlane(ViewState view, UiRect rect, float x, float y, Vector3 origin, Vector3 normal)
    {
        if (view.Kind == ViewKind.Plane2D)
        {
            return PointOn2D(view, rect, x, y);
        }

        Ray(view, rect, x, y, out Vector3 o, out Vector3 d);
        float denom = Vector3.Dot(normal, d);
        if (MathF.Abs(denom) < 1e-6f)
        {
            return null;
        }

        float t = Vector3.Dot(normal, origin - o) / denom;
        return t < 0 ? null : o + (d * t);
    }

    /// <summary>The ray through a pixel of a 3D view, from the camera the renderer uses.</summary>
    public static void Ray(ViewState view, UiRect rect, float x, float y, out Vector3 origin, out Vector3 direction)
    {
        var camera = Space3DRenderer.MakeCamera(view);
        Matrix4x4 viewProj = camera.View * camera.Projection(rect.Width / Math.Max(rect.Height, 1));
        float nx = (((x - rect.X) / Math.Max(rect.Width, 1)) * 2) - 1;
        float ny = 1 - (((y - rect.Y) / Math.Max(rect.Height, 1)) * 2);
        if (!Matrix4x4.Invert(viewProj, out Matrix4x4 inverse))
        {
            origin = view.Target;
            direction = -Vector3.UnitZ;
            return;
        }

        Vector3 near = Unproject(inverse, nx, ny, 0);
        Vector3 far = Unproject(inverse, nx, ny, 1);
        origin = near;
        direction = Vector3.Normalize(far - near);
    }

    private static Vector3 Unproject(Matrix4x4 inverse, float x, float y, float z)
    {
        Vector4 p = Vector4.Transform(new Vector4(x, y, z, 1), inverse);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    private static int[] LayerOrder(DrawList list)
    {
        int n = list.Items.Length;
        var keys = new long[n];
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            keys[i] = ((long)list.Items[i].Layer << 32) | (uint)i;
            order[i] = i;
        }

        Array.Sort(keys, order);
        return order;
    }
}
