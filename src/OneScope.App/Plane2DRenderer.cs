using System.Globalization;
using System.Numerics;
using OneGame.Render2D;
using OneGame.Render3D;
using OneScope.Sdk;
using UiColor = OneGame.UI.Color;

namespace OneScope.App;

/// <summary>A text label placed in screen pixels.</summary>
internal readonly record struct ScreenLabel(float X, float Y, string Text, UiColor Color, float Size);

/// <summary>Per-view state that survives between frames: the draw list, its labels and the 3D render target.</summary>
internal sealed class ViewCache : IDisposable
{
    public DrawList List { get; } = new();

    public List<ScreenLabel> Labels { get; } = [];

    public (int Shell, int View, int Width, int Height, int Kind, int Source) Key { get; set; } = (-1, -1, 0, 0, 0, 0);

    public OneGame.UI.Rect Rect { get; set; }

    public bool Visible { get; set; }

    public int[] Order { get; set; } = new int[256];

    public Scene3D? Scene { get; set; }

    public Offscreen3D? Target { get; set; }

    public DynamicMesh3D? Mesh { get; set; }

    public Texture2D? Texture { get; set; }

    public SolidBuilder Solids { get; } = new();

    public void Dispose()
    {
        Mesh?.Dispose();
        // The texture only wraps the target's image, so it goes first.
        Texture?.Dispose();
        Target?.Dispose();
        Mesh = null;
        Target = null;
        Texture = null;
    }
}

/// <summary>Grid spacing and number formatting shared by the 2D and 3D views.</summary>
internal static class GridMath
{
    /// <summary>The 1, 2 or 5 times a power of ten at or above <paramref name="raw"/>.</summary>
    public static float NiceStep(float raw)
    {
        if (!(raw > 0) || !float.IsFinite(raw))
        {
            return 1;
        }

        float pow = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float m = raw / pow;
        float nice = m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10;
        return nice * pow;
    }

    public static string Format(float value)
    {
        if (MathF.Abs(value) < 1e-6f)
        {
            return "0";
        }

        float abs = MathF.Abs(value);
        string text = abs >= 1e6f || abs < 1e-3f
            ? value.ToString("0.###e+0", CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);
        return text;
    }
}

/// <summary>Draws a 2D view with the 2D renderer's canvas: primitives are rotated, rounded rectangles clipped to the view.</summary>
internal static partial class Plane2DRenderer
{
    private static readonly UiColor Background = new(24, 26, 31);
    private static readonly UiColor GridMinor = new(44, 47, 55);
    private static readonly float[] HeadAngles = [0.45f, -0.45f];
    private static readonly UiColor GridAxis = new(92, 97, 110);

    /// <summary>The pixel scale and offset that map world units to screen pixels.</summary>
    private readonly record struct Frame(Vector2 Centre, Vector2 Target, float PixelsPerUnit)
    {
        public Vector2 ToScreen(Vector3 w) => new(Centre.X + ((w.X - Target.X) * PixelsPerUnit), Centre.Y - ((w.Y - Target.Y) * PixelsPerUnit));
    }

    public static void Rebuild(ViewCache cache, ViewState view, SourceSession source, OneGame.UI.Rect rect)
    {
        cache.List.Clear();
        ShellModel.DrawView(view, source, cache.List);
        cache.Labels.Clear();
        Frame f = MakeFrame(view, rect);
        if (view.Grid)
        {
            GridLabels(cache, view, source.Info, rect, f);
        }

        foreach (ref readonly DrawItem item in cache.List.Items)
        {
            if (item.Kind == DrawKind.Label)
            {
                Vector2 p = f.ToScreen(item.A);
                if (rect.Contains(p.X, p.Y))
                {
                    cache.Labels.Add(new ScreenLabel(p.X, p.Y - 7, new string(cache.List.TextOf(in item)), ToUi(item.Color), 12));
                }
            }
        }

        FieldLegend.AddLabels(cache, rect);
        SortByLayer(cache);
    }

    private static Frame MakeFrame(ViewState view, OneGame.UI.Rect rect) =>
        new(new Vector2(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)), new Vector2(view.Target.X, view.Target.Y), Math.Min(rect.Width, rect.Height) / (2 * Math.Max(view.HalfHeight, 1e-9f)));

    private static void SortByLayer(ViewCache cache)
    {
        int n = cache.List.Items.Length;
        if (cache.Order.Length < n)
        {
            cache.Order = new int[Math.Max(n, cache.Order.Length * 2)];
        }

        long[] keys = new long[n];
        for (int i = 0; i < n; i++)
        {
            // Layer first, then the module's own order, so equal layers keep the order they were drawn in.
            keys[i] = ((long)cache.List.Items[i].Layer << 32) | (uint)i;
            cache.Order[i] = i;
        }

        Array.Sort(keys, cache.Order, 0, n);
    }

    private static void GridLabels(ViewCache cache, ViewState view, SourceInfo info, OneGame.UI.Rect rect, Frame f)
    {
        float step = GridMath.NiceStep(60 / f.PixelsPerUnit);
        float left = f.Target.X - (rect.Width / 2 / f.PixelsPerUnit);
        float right = f.Target.X + (rect.Width / 2 / f.PixelsPerUnit);
        float bottom = f.Target.Y - (rect.Height / 2 / f.PixelsPerUnit);
        float top = f.Target.Y + (rect.Height / 2 / f.PixelsPerUnit);
        UiColor ink = new(140, 146, 160);
        for (float x = MathF.Ceiling(left / step) * step; x <= right; x += step)
        {
            cache.Labels.Add(new ScreenLabel(f.ToScreen(new Vector3(x, 0, 0)).X + 3, rect.Y + 22, GridMath.Format(x), ink, 11));
        }

        for (float y = MathF.Ceiling(bottom / step) * step; y <= top; y += step)
        {
            cache.Labels.Add(new ScreenLabel(rect.X + 4, f.ToScreen(new Vector3(0, y, 0)).Y - 13, GridMath.Format(y), ink, 11));
        }

        cache.Labels.Add(new ScreenLabel(rect.X + 6, rect.Bottom - 18, $"grid {GridMath.Format(step)} {info.Unit.Name}", new UiColor(170, 176, 190), 11));
    }

    public static void Draw(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect rect, ViewState view, ViewCache cache, EntityId selected = default)
    {
        Frame f = MakeFrame(view, rect);
        ui.PushClip(rect);
        ui.Fill(rect, Background);
        if (view.Grid)
        {
            DrawGrid(ui, rect, f);
        }

        DrawList list = cache.List;
        ReadOnlySpan<DrawItem> items = list.Items;
        for (int k = 0; k < items.Length; k++)
        {
            ref readonly DrawItem item = ref items[cache.Order[k]];
            DrawOne(ui, rect, f, list, item, ToUi(item.Color), view.Wireframe || (item.Flags & DrawFlags.Wireframe) != 0, 1.2f);
        }

        if (selected.IsSome)
        {
            // The selection is outlined on top of everything, thick and in one colour, so it reads in any state colour.
            for (int k = 0; k < items.Length; k++)
            {
                ref readonly DrawItem item = ref items[cache.Order[k]];
                if (item.Entity == selected && item.Kind != DrawKind.Label)
                {
                    DrawOne(ui, rect, f, list, item, Highlight, true, 3f);
                }
            }
        }

        ui.PopClip();
    }

    /// <summary>The colour the selected entity is outlined in.</summary>
    internal static readonly UiColor Highlight = new(255, 214, 64);

    private static void DrawOne(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect rect, Frame f, DrawList list, in DrawItem item, UiColor color, bool wire, float edge)
    {
        switch (item.Kind)
        {
            case DrawKind.Line:
                Line(ui, f.ToScreen(item.A), f.ToScreen(item.B), Math.Max(1.5f, edge), color);
                break;
            case DrawKind.Point:
                {
                    Vector2 p = f.ToScreen(item.A);
                    float r = Math.Max(item.Size.X, 1) / 2;
                    ui.Fill(new OneGame.UI.Rect(p.X - r, p.Y - r, r * 2, r * 2), color, new OneGame.UI.CornerRadii(r, r, r, r));
                    break;
                }

            case DrawKind.Triangle:
                Triangle(ui, rect, f.ToScreen(item.A), f.ToScreen(item.B), f.ToScreen(item.C), color, wire, edge);
                break;
            case DrawKind.Mesh:
                {
                    if (item.Field != 0 && !wire)
                    {
                        FieldMesh(ui, rect, f, list, item, edge);
                        break;
                    }

                    ReadOnlySpan<Vector3> vertices = list.Vertices;
                    ReadOnlySpan<int> indices = list.Indices;
                    int cap = Math.Min(item.Count, 3 * 20000);
                    for (int t = 0; t + 2 < cap; t += 3)
                    {
                        Triangle(ui, rect, f.ToScreen(vertices[indices[item.First + t]]), f.ToScreen(vertices[indices[item.First + t + 1]]), f.ToScreen(vertices[indices[item.First + t + 2]]), color, wire, edge);
                    }

                    break;
                }

            case DrawKind.Box:
                Box(ui, f, item, color, wire, edge);
                break;
            case DrawKind.Sphere:
                {
                    Vector2 p = f.ToScreen(item.A);
                    float r = item.Size.X * f.PixelsPerUnit;
                    var rr = new OneGame.UI.CornerRadii(r, r, r, r);
                    var box = new OneGame.UI.Rect(p.X - r, p.Y - r, r * 2, r * 2);
                    if (wire)
                    {
                        ui.Border(box, color, new OneGame.UI.Edges(edge, edge, edge, edge), rr);
                    }
                    else
                    {
                        ui.Fill(box, color, rr);
                    }

                    break;
                }

            case DrawKind.Capsule:
            case DrawKind.Cylinder:
                Bar(ui, f.ToScreen(item.A), f.ToScreen(item.B), item.Size.X * f.PixelsPerUnit, item.Kind == DrawKind.Capsule, color, wire, edge);
                break;
            case DrawKind.Arrow:
                Arrow(ui, f.ToScreen(item.A), f.ToScreen(item.B), Math.Max(item.Size.X * f.PixelsPerUnit, 6), color);
                break;
        }
    }

    private static void DrawGrid(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect rect, Frame f)
    {
        float step = GridMath.NiceStep(60 / f.PixelsPerUnit);
        float left = f.Target.X - (rect.Width / 2 / f.PixelsPerUnit);
        float right = f.Target.X + (rect.Width / 2 / f.PixelsPerUnit);
        float bottom = f.Target.Y - (rect.Height / 2 / f.PixelsPerUnit);
        float top = f.Target.Y + (rect.Height / 2 / f.PixelsPerUnit);
        for (float x = MathF.Ceiling(left / step) * step; x <= right; x += step)
        {
            float sx = MathF.Round(f.ToScreen(new Vector3(x, 0, 0)).X);
            ui.Fill(new OneGame.UI.Rect(sx, rect.Y, 1, rect.Height), MathF.Abs(x) < step * 0.01f ? GridAxis : GridMinor);
        }

        for (float y = MathF.Ceiling(bottom / step) * step; y <= top; y += step)
        {
            float sy = MathF.Round(f.ToScreen(new Vector3(0, y, 0)).Y);
            ui.Fill(new OneGame.UI.Rect(rect.X, sy, rect.Width, 1), MathF.Abs(y) < step * 0.01f ? GridAxis : GridMinor);
        }
    }

    private static void Line(OneGame.Render2D.UiCanvas ui, Vector2 a, Vector2 b, float thickness, UiColor color)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (!float.IsFinite(len))
        {
            return;
        }

        Vector2 mid = (a + b) / 2;
        ui.PushTransform(Matrix3x2.CreateRotation(MathF.Atan2(d.Y, d.X), mid));
        ui.Fill(new OneGame.UI.Rect(mid.X - (len / 2), mid.Y - (thickness / 2), Math.Max(len, 0.01f), thickness), color);
        ui.PopTransform();
    }

    private static void Triangle(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect clip, Vector2 a, Vector2 b, Vector2 c, UiColor color, bool wire, float edge)
    {
        if (wire)
        {
            Line(ui, a, b, edge, color);
            Line(ui, b, c, edge, color);
            Line(ui, c, a, edge, color);
            return;
        }

        // Filled by horizontal one-pixel strips: the canvas has rectangles, not triangles.
        float minY = Math.Max(MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), clip.Y);
        float maxY = Math.Min(MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), clip.Bottom);
        for (float y = minY; y < maxY; y++)
        {
            float sy = y + 0.5f;
            float lo = float.MaxValue;
            float hi = float.MinValue;
            Cross(a, b, sy, ref lo, ref hi);
            Cross(b, c, sy, ref lo, ref hi);
            Cross(c, a, sy, ref lo, ref hi);
            if (hi > lo)
            {
                ui.Fill(new OneGame.UI.Rect(lo, y, hi - lo, 1), color);
            }
        }
    }

    private static void Cross(Vector2 p, Vector2 q, float y, ref float lo, ref float hi)
    {
        if ((p.Y <= y && q.Y > y) || (q.Y <= y && p.Y > y))
        {
            float x = p.X + ((y - p.Y) / (q.Y - p.Y) * (q.X - p.X));
            lo = Math.Min(lo, x);
            hi = Math.Max(hi, x);
        }
    }

    private static void Box(OneGame.Render2D.UiCanvas ui, Frame f, in DrawItem item, UiColor color, bool wire, float edge)
    {
        Quaternion q = item.Rotation;
        float angle = MathF.Atan2(2 * ((q.W * q.Z) + (q.X * q.Y)), 1 - (2 * ((q.Y * q.Y) + (q.Z * q.Z))));
        Vector2 c = f.ToScreen(item.A);
        float hw = item.Size.X * f.PixelsPerUnit;
        float hh = item.Size.Y * f.PixelsPerUnit;
        ui.PushTransform(Matrix3x2.CreateRotation(-angle, c));
        var box = new OneGame.UI.Rect(c.X - hw, c.Y - hh, hw * 2, Math.Max(hh * 2, 1));
        if (wire)
        {
            ui.Border(box, color, new OneGame.UI.Edges(edge, edge, edge, edge));
        }
        else
        {
            ui.Fill(box, color);
        }

        ui.PopTransform();
    }

    private static void Bar(OneGame.Render2D.UiCanvas ui, Vector2 a, Vector2 b, float radius, bool rounded, UiColor color, bool wire, float edge)
    {
        Vector2 d = b - a;
        float len = d.Length();
        Vector2 mid = (a + b) / 2;
        float length = len + (rounded ? radius * 2 : 0);
        ui.PushTransform(Matrix3x2.CreateRotation(MathF.Atan2(d.Y, d.X), mid));
        var box = new OneGame.UI.Rect(mid.X - (length / 2), mid.Y - radius, Math.Max(length, 0.01f), Math.Max(radius * 2, 0.01f));
        var radii = rounded ? new OneGame.UI.CornerRadii(radius, radius, radius, radius) : default;
        if (wire)
        {
            ui.Border(box, color, new OneGame.UI.Edges(edge, edge, edge, edge), radii);
        }
        else
        {
            ui.Fill(box, color, radii);
        }

        ui.PopTransform();
    }

    private static void Arrow(OneGame.Render2D.UiCanvas ui, Vector2 from, Vector2 to, float head, UiColor color)
    {
        Line(ui, from, to, 1.5f, color);
        Vector2 d = to - from;
        float len = d.Length();
        if (len < 1e-3f)
        {
            return;
        }

        Vector2 dir = d / len;
        float h = Math.Min(head, len);
        foreach (float side in HeadAngles)
        {
            float ca = MathF.Cos(side);
            float sa = MathF.Sin(side);
            var back = new Vector2((-dir.X * ca) - (-dir.Y * sa), (-dir.X * sa) + (-dir.Y * ca));
            Line(ui, to, to + (back * h), 1.5f, color);
        }
    }

    internal static UiColor ToUi(Color32 c) => new(c.R, c.G, c.B, c.A);
}
