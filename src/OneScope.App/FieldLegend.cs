using System.Globalization;
using OneScope.Sdk;
using UiColor = OneGame.UI.Color;

namespace OneScope.App;

/// <summary>The colour-bar legend of the scalar fields a view paints: name, map and range, stacked in the view's bottom-right corner.</summary>
internal static class FieldLegend
{
    private const float Width = 150;
    private const float Block = 44;
    private const int Strips = 30;

    [ThreadStatic]
    private static FieldInfo[]? _scratch;

    private static FieldInfo[] Scratch => _scratch ??= new FieldInfo[4];

    /// <summary>The distinct fields in a draw list, in the order they first appear.</summary>
    public static int Distinct(DrawList list, Span<FieldInfo> into)
    {
        int n = 0;
        foreach (FieldBinding b in list.FieldBindings)
        {
            bool seen = false;
            for (int i = 0; i < n; i++)
            {
                seen |= into[i].Id == b.Field.Id;
            }

            if (!seen && n < into.Length)
            {
                into[n++] = b.Field;
            }
        }

        return n;
    }

    private static OneGame.UI.Rect BlockRect(OneGame.UI.Rect view, int index) =>
        new(view.Right - Width - 12, view.Bottom - 12 - ((index + 1) * Block), Width, Block - 6);

    /// <summary>Adds the legend's text to the view's labels.</summary>
    public static void AddLabels(ViewCache cache, OneGame.UI.Rect rect)
    {
        Span<FieldInfo> fields = Scratch;
        int n = Distinct(cache.List, fields);
        for (int i = 0; i < n; i++)
        {
            FieldInfo f = fields[i];
            OneGame.UI.Rect r = BlockRect(rect, i);
            string title = f.Unit.Length > 0 ? $"{f.Name} ({f.Unit})" : f.Name;
            UiColor ink = new(230, 234, 242);
            cache.Labels.Add(new ScreenLabel(r.X + 4, r.Y + 2, title, ink, 11));
            cache.Labels.Add(new ScreenLabel(r.X + 4, r.Y + 28, GridMath.Format((float)f.Min), ink, 11));
            string max = GridMath.Format((float)f.Max);
            cache.Labels.Add(new ScreenLabel(r.Right - 4 - (max.Length * 6.2f), r.Y + 28, max, ink, 11));
        }
    }

    /// <summary>Draws the legend's panels and colour bars.</summary>
    public static void DrawBars(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect rect, ViewCache cache)
    {
        if (!cache.Visible || cache.List.FieldBindings.Count == 0)
        {
            return;
        }

        Span<FieldInfo> fields = Scratch;
        int n = Distinct(cache.List, fields);
        for (int i = 0; i < n; i++)
        {
            FieldInfo f = fields[i];
            OneGame.UI.Rect r = BlockRect(rect, i);
            ui.Fill(r, new UiColor(0, 0, 0, 170));
            float x0 = r.X + 4;
            float w = r.Width - 8;
            for (int s = 0; s < Strips; s++)
            {
                Color32 c = ColorMaps.Evaluate(f.Map, (s + 0.5f) / Strips);
                ui.Fill(new OneGame.UI.Rect(x0 + (w * s / Strips), r.Y + 16, (w / Strips) + 0.5f, 10), Plane2DRenderer.ToUi(c));
            }
        }
    }
}
