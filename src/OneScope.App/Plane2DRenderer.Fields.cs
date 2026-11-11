using System.Numerics;
using OneScope.Sdk;
using UiColor = OneGame.UI.Color;

namespace OneScope.App;

internal static partial class Plane2DRenderer
{
    /// <summary>Splits per triangle edge when a per-vertex field is blended; the canvas has flat fills only, so the gradient is a fine mosaic.</summary>
    private const int FieldSplits = 4;

    /// <summary>Paints a field mesh: one flat colour per triangle for an element field, a blended mosaic for a vertex field.</summary>
    private static void FieldMesh(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect rect, Frame f, DrawList list, in DrawItem item, float edge)
    {
        FieldInfo field = list.FieldBindings[item.Field - 1].Field;
        ReadOnlySpan<Vector3> vertices = list.Vertices;
        ReadOnlySpan<int> indices = list.Indices;
        int cap = Math.Min(item.Count, 3 * 20000);
        for (int t = 0; t + 2 < cap; t += 3)
        {
            int at = item.First + t;
            Vector2 a = f.ToScreen(vertices[indices[at]]);
            Vector2 b = f.ToScreen(vertices[indices[at + 1]]);
            Vector2 c = f.ToScreen(vertices[indices[at + 2]]);
            float va = list.FieldValueAt(item, at);
            float vb = list.FieldValueAt(item, at + 1);
            float vc = list.FieldValueAt(item, at + 2);
            if (va == vb && vb == vc)
            {
                Triangle(ui, rect, a, b, c, ToUi(ColorMaps.Evaluate(field, va)), false, edge);
                continue;
            }

            for (int i = 0; i < FieldSplits; i++)
            {
                for (int j = 0; j < FieldSplits - i; j++)
                {
                    SubTriangle(ui, rect, field, a, b, c, va, vb, vc, i, j, i + 1, j, i, j + 1, edge);
                    if (j < FieldSplits - i - 1)
                    {
                        SubTriangle(ui, rect, field, a, b, c, va, vb, vc, i + 1, j, i + 1, j + 1, i, j + 1, edge);
                    }
                }
            }
        }
    }

    private static void SubTriangle(OneGame.Render2D.UiCanvas ui, OneGame.UI.Rect rect, in FieldInfo field, Vector2 a, Vector2 b, Vector2 c, float va, float vb, float vc,
        int i0, int j0, int i1, int j1, int i2, int j2, float edge)
    {
        const float n = FieldSplits;
        Vector2 P(int i, int j) => a + (((b - a) * (i / n)) + ((c - a) * (j / n)));
        float wb = (i0 + i1 + i2) / (3f * n);
        float wc = (j0 + j1 + j2) / (3f * n);
        float value = (va * (1 - wb - wc)) + (vb * wb) + (vc * wc);
        Triangle(ui, rect, P(i0, j0), P(i1, j1), P(i2, j2), ToUi(ColorMaps.Evaluate(field, value)), false, edge);
    }
}
