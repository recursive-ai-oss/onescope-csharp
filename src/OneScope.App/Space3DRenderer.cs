using System.Numerics;
using OneGame.Gpu;
using OneGame.Render2D;
using OneGame.Render3D;
using OneScope.Sdk;
using UiColor = OneGame.UI.Color;

namespace OneScope.App;

/// <summary>
/// Draws a 3D view with onegame's 3D renderer into an offscreen target and hands the pixels to the 2D renderer, which places them in the
/// shell's layout. Solids are one mesh; lines, points, arrows, wireframes and the grid are the renderer's debug lines.
/// </summary>
internal sealed class Space3DRenderer : IDisposable
{
    private static readonly Vector4 GridColor = new(0.09f, 0.1f, 0.12f, 1f);
    private readonly Renderer3D _renderer;
    private readonly Material3D _solid;

    public Space3DRenderer(GpuDevice device)
    {
        _renderer = new Renderer3D(device);
        _solid = _renderer.CreateMaterial("scope-solid", MaterialAlpha3D.Opaque, doubleSided: true);
        _solid.Style = MaterialStyle3D.Flat;
    }

    /// <summary>The orbit camera for a view and an aspect ratio, as the renderer will use it.</summary>
    public static Camera3D MakeCamera(ViewState view)
    {
        Camera3D cam = Camera3D.Orbit(view.Target, view.Distance, view.Yaw, view.Pitch);
        cam.YFov = 0.7f;
        cam.Near = Math.Max(view.Distance / 400f, 1e-6f);
        cam.Far = view.Distance * 400f;
        return cam;
    }

    /// <summary>Rebuilds the cache's scene from the source at its cursor and renders it. Returns the texture holding the picture.</summary>
    public Texture2D Render(Renderer2D r2, ViewCache cache, ViewState view, SourceSession source, OneGame.UI.Rect rect)
    {
        int w = Math.Clamp((int)MathF.Round(rect.Width), 1, 4096);
        int h = Math.Clamp((int)MathF.Round(rect.Height), 1, 4096);
        if (cache.Target is null || cache.Target.Width != w || cache.Target.Height != h)
        {
            cache.Target?.Dispose();
            cache.Target = new Offscreen3D(_renderer, w, h);
        }

        if (cache.Scene is null)
        {
            cache.Scene = new Scene3D { Quality = QualityLevel.Medium };
            cache.Scene.Environment.Background = BackgroundMode.Color;
            cache.Scene.Environment.BackgroundColor = new Vector3(GridColor.X, GridColor.Y, GridColor.Z);
            cache.Scene.Post.ToneMapper = ToneMapper.None;
            cache.Scene.Post.Bloom = false;
        }

        Scene3D scene = cache.Scene;
        scene.Camera = MakeCamera(view);
        DrawList list = cache.List;
        list.Clear();
        source.History.View.Draw(list, new DrawRequest(view.Overlays));
        cache.Labels.Clear();
        SolidBuilder solids = cache.Solids;
        solids.Clear();
        scene.Debug.Clear();
        Matrix4x4 viewProj = scene.Camera.View * scene.Camera.Projection(w / (float)h);

        if (view.Grid)
        {
            Grid(scene, cache, view, source.Info, viewProj, rect);
        }

        foreach (ref readonly DrawItem item in list.Items)
        {
            AddItem(scene, solids, list, item, view.Wireframe || (item.Flags & DrawFlags.Wireframe) != 0 || item.Color.A < 255);
            if (item.Kind == DrawKind.Label && Project(viewProj, item.A, rect, out Vector2 p))
            {
                cache.Labels.Add(new ScreenLabel(p.X, p.Y - 7, new string(list.TextOf(in item)), Plane2DRenderer.ToUi(item.Color), 12));
            }
        }

        scene.Models.Clear();
        if (solids.Indices.Length > 0)
        {
            if (cache.Mesh is null)
            {
                cache.Mesh = _renderer.CreateMesh(_solid, solids.Vertices, solids.Indices, "scope-solids");
            }
            else
            {
                cache.Mesh.Update(solids.Vertices, solids.Indices);
            }

            scene.Add(cache.Mesh.Model);
        }

        byte[] pixels = cache.Target.Capture(scene);
        if (cache.Texture is null)
        {
            cache.Texture = r2.CreateTexture(w, h, pixels);
        }
        else
        {
            r2.ReplaceTexture(cache.Texture, w, h, pixels);
        }

        return cache.Texture;
    }

    private static bool Project(Matrix4x4 viewProj, Vector3 world, OneGame.UI.Rect rect, out Vector2 screen)
    {
        Vector4 c = Vector4.Transform(new Vector4(world, 1), viewProj);
        if (c.W <= 1e-6f)
        {
            screen = default;
            return false;
        }

        float x = c.X / c.W;
        float y = c.Y / c.W;
        screen = new Vector2(rect.X + ((x * 0.5f) + 0.5f) * rect.Width, rect.Y + ((1 - ((y * 0.5f) + 0.5f)) * rect.Height));
        return rect.Contains(screen.X, screen.Y);
    }

    private static void Grid(Scene3D scene, ViewCache cache, ViewState view, SourceInfo info, Matrix4x4 viewProj, OneGame.UI.Rect rect)
    {
        float step = GridMath.NiceStep(view.Distance / 8);
        int n = 10;
        float cx = MathF.Round(view.Target.X / step) * step;
        float cz = MathF.Round(view.Target.Z / step) * step;
        var minor = new Vector4(0.2f, 0.22f, 0.27f, 1f);
        for (int i = -n; i <= n; i++)
        {
            float x = cx + (i * step);
            float z = cz + (i * step);
            Vector4 cX = MathF.Abs(x) < step * 0.01f ? new Vector4(0.8f, 0.25f, 0.25f, 1f) : minor;
            Vector4 cZ = MathF.Abs(z) < step * 0.01f ? new Vector4(0.25f, 0.45f, 0.85f, 1f) : minor;
            scene.Debug.Line(new Vector3(x, 0, cz - (n * step)), new Vector3(x, 0, cz + (n * step)), cX, 1f);
            scene.Debug.Line(new Vector3(cx - (n * step), 0, z), new Vector3(cx + (n * step), 0, z), cZ, 1f);
        }

        var ink = new UiColor(140, 146, 160);
        for (int i = -n; i <= n; i += 2)
        {
            float x = cx + (i * step);
            if (Project(viewProj, new Vector3(x, 0, cz), rect, out Vector2 p))
            {
                cache.Labels.Add(new ScreenLabel(p.X + 2, p.Y, GridMath.Format(x), ink, 11));
            }

            float z = cz + (i * step);
            if (i != 0 && Project(viewProj, new Vector3(cx, 0, z), rect, out Vector2 q))
            {
                cache.Labels.Add(new ScreenLabel(q.X + 2, q.Y, GridMath.Format(z), ink, 11));
            }
        }

        scene.Debug.Line(Vector3.Zero, new Vector3(0, step * 2, 0), new Vector4(0.3f, 0.8f, 0.35f, 1f), 2f);
        cache.Labels.Add(new ScreenLabel(rect.X + 6, rect.Bottom - 18, $"grid {GridMath.Format(step)} {info.Unit.Name}", new UiColor(170, 176, 190), 11));
    }

    private static void AddItem(Scene3D scene, SolidBuilder solids, DrawList list, in DrawItem item, bool wire)
    {
        Vector4 colour = SolidBuilder.Linear(item.Color);
        DebugDepth3D mode = (item.Flags & DrawFlags.OnTop) != 0 ? DebugDepth3D.AlwaysOnTop : DebugDepth3D.DepthTested;
        DebugDraw3D dbg = scene.Debug;
        switch (item.Kind)
        {
            case DrawKind.Line:
                dbg.Line(item.A, item.B, colour, 1.5f, DebugSize3D.Pixels, mode);
                break;
            case DrawKind.Point:
                dbg.Point(item.A, colour, Math.Max(item.Size.X, 1), DebugSize3D.Pixels, mode);
                break;
            case DrawKind.Arrow:
                dbg.Arrow(item.A, item.B, colour, 2f, DebugSize3D.Pixels, mode);
                break;
            case DrawKind.Triangle:
                if (wire)
                {
                    dbg.Line(item.A, item.B, colour, 1.2f, DebugSize3D.Pixels, mode);
                    dbg.Line(item.B, item.C, colour, 1.2f, DebugSize3D.Pixels, mode);
                    dbg.Line(item.C, item.A, colour, 1.2f, DebugSize3D.Pixels, mode);
                }
                else
                {
                    solids.Triangle(item.A, item.B, item.C, colour);
                }

                break;
            case DrawKind.Mesh:
                if (wire)
                {
                    ReadOnlySpan<Vector3> v = list.Vertices;
                    ReadOnlySpan<int> idx = list.Indices;
                    for (int t = 0; t + 2 < item.Count; t += 3)
                    {
                        Vector3 a = v[idx[item.First + t]], b = v[idx[item.First + t + 1]], c = v[idx[item.First + t + 2]];
                        dbg.Line(a, b, colour, 1.2f, DebugSize3D.Pixels, mode);
                        dbg.Line(b, c, colour, 1.2f, DebugSize3D.Pixels, mode);
                        dbg.Line(c, a, colour, 1.2f, DebugSize3D.Pixels, mode);
                    }
                }
                else
                {
                    solids.Mesh(list.Vertices, list.Indices, item.First, item.Count, colour);
                }

                break;
            case DrawKind.Box:
                if (wire)
                {
                    BoxEdges(dbg, item, colour, mode);
                }
                else
                {
                    solids.Box(item.A, item.Size, item.Rotation, colour);
                }

                break;
            case DrawKind.Sphere:
                if (wire)
                {
                    Circle(dbg, item.A, Vector3.UnitX, Vector3.UnitY, item.Size.X, colour, mode);
                    Circle(dbg, item.A, Vector3.UnitY, Vector3.UnitZ, item.Size.X, colour, mode);
                    Circle(dbg, item.A, Vector3.UnitX, Vector3.UnitZ, item.Size.X, colour, mode);
                }
                else
                {
                    solids.Sphere(item.A, item.Size.X, colour);
                }

                break;
            case DrawKind.Capsule:
            case DrawKind.Cylinder:
                if (wire)
                {
                    SolidBuilder.Axis(item.A, item.B, out _, out Vector3 u, out Vector3 w);
                    Circle(dbg, item.A, u, w, item.Size.X, colour, mode);
                    Circle(dbg, item.B, u, w, item.Size.X, colour, mode);
                    foreach (Vector3 s in new[] { u, -u, w, -w })
                    {
                        dbg.Line(item.A + (s * item.Size.X), item.B + (s * item.Size.X), colour, 1.2f, DebugSize3D.Pixels, mode);
                    }
                }
                else if (item.Kind == DrawKind.Capsule)
                {
                    solids.Capsule(item.A, item.B, item.Size.X, colour);
                }
                else
                {
                    solids.Cylinder(item.A, item.B, item.Size.X, colour);
                }

                break;
        }
    }

    private static void BoxEdges(DebugDraw3D dbg, in DrawItem item, Vector4 colour, DebugDepth3D mode)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? -item.Size.X : item.Size.X, (i & 2) == 0 ? -item.Size.Y : item.Size.Y, (i & 4) == 0 ? -item.Size.Z : item.Size.Z);
            corners[i] = item.A + Vector3.Transform(local, item.Rotation);
        }

        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    dbg.Line(corners[i], corners[i | bit], colour, 1.2f, DebugSize3D.Pixels, mode);
                }
            }
        }
    }

    private static void Circle(DebugDraw3D dbg, Vector3 centre, Vector3 u, Vector3 v, float radius, Vector4 colour, DebugDepth3D mode)
    {
        const int Steps = 24;
        Vector3 prev = centre + (u * radius);
        for (int i = 1; i <= Steps; i++)
        {
            float t = 2 * MathF.PI * i / Steps;
            Vector3 next = centre + (((u * MathF.Cos(t)) + (v * MathF.Sin(t))) * radius);
            dbg.Line(prev, next, colour, 1.2f, DebugSize3D.Pixels, mode);
            prev = next;
        }
    }

    public void Dispose() => _renderer.Dispose();
}
