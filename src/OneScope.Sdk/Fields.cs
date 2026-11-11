namespace OneScope.Sdk;

/// <summary>A named way to turn a number in a range into a colour. The shell's legend draws the same map.</summary>
public enum ColorMap : byte
{
    /// <summary>Dark purple through teal to yellow. Reads well in grey scale; the usual choice.</summary>
    Viridis = 0,

    /// <summary>Dark through red and orange to pale yellow. For magnitudes such as stress or temperature.</summary>
    Heat,

    /// <summary>Blue through light grey to red. For values with a meaningful middle such as signed residuals.</summary>
    CoolWarm,

    /// <summary>Dark to light grey.</summary>
    Grayscale,
}

/// <summary>Whether a field has one value per mesh vertex or one per mesh triangle.</summary>
public enum FieldDomain : byte
{
    /// <summary>One value per vertex; the colour is blended across each triangle.</summary>
    Vertex = 0,

    /// <summary>One value per triangle, in index order (triangle k uses indices 3k to 3k+2); the triangle is one flat colour.</summary>
    Element,
}

/// <summary>
/// A scalar field a source can draw: stress, wall thickness, a residual, a density. A source declares its fields in
/// <see cref="SourceInfo.Fields"/>; a draw call names the field it paints.
/// </summary>
/// <param name="Id">Stable id, unique within the source. The <c>field</c> operation and <see cref="IScopeWorld.TryReadField"/> use it.</param>
/// <param name="Name">Display name, shown on the legend.</param>
/// <param name="Map">The colour map.</param>
/// <param name="Min">The value drawn with the first colour of the map. Values below it are clamped.</param>
/// <param name="Max">The value drawn with the last colour of the map. Values above it are clamped. When it is not above <paramref name="Min"/> every value uses the middle of the map.</param>
/// <param name="Unit">Text shown next to the range, such as "MPa". May be empty.</param>
public readonly record struct FieldInfo(string Id, string Name, ColorMap Map, double Min, double Max, string Unit = "");

/// <summary>Colour maps, evaluated the same way by the app, the legend and the module authors' own tests.</summary>
public static class ColorMaps
{
    /// <summary>The colour of a value that is not a number: a field with no data there.</summary>
    public static Color32 NoData => new(110, 110, 110);

    private static readonly Color32[] ViridisStops = [new(68, 1, 84), new(59, 82, 139), new(33, 145, 140), new(94, 201, 98), new(253, 231, 37)];
    private static readonly Color32[] HeatStops = [new(22, 18, 40), new(150, 25, 40), new(235, 100, 30), new(250, 190, 50), new(255, 250, 205)];
    private static readonly Color32[] CoolWarmStops = [new(59, 76, 192), new(141, 176, 254), new(221, 221, 221), new(244, 154, 123), new(180, 4, 38)];
    private static readonly Color32[] GrayStops = [new(24, 24, 24), new(236, 236, 236)];

    /// <summary>The colour of position <paramref name="t"/> along a map.</summary>
    /// <param name="map">The map. An undefined value is treated as <see cref="ColorMap.Viridis"/>.</param>
    /// <param name="t">0 (first colour) to 1 (last). Values outside are clamped; NaN gives <see cref="NoData"/>.</param>
    public static Color32 Evaluate(ColorMap map, float t)
    {
        if (float.IsNaN(t))
        {
            return NoData;
        }

        Color32[] stops = map switch
        {
            ColorMap.Heat => HeatStops,
            ColorMap.CoolWarm => CoolWarmStops,
            ColorMap.Grayscale => GrayStops,
            _ => ViridisStops,
        };
        float x = Math.Clamp(t, 0f, 1f) * (stops.Length - 1);
        int i = Math.Min((int)x, stops.Length - 2);
        float f = x - i;
        Color32 a = stops[i];
        Color32 b = stops[i + 1];
        return new Color32(Lerp(a.R, b.R, f), Lerp(a.G, b.G, f), Lerp(a.B, b.B, f));
    }

    /// <summary>The colour a field gives to a value: the value is placed between <see cref="FieldInfo.Min"/> and <see cref="FieldInfo.Max"/>.</summary>
    public static Color32 Evaluate(in FieldInfo field, float value) => Evaluate(field.Map, Normalize(field, value));

    /// <summary>Where a value falls in the field's range: 0 at <see cref="FieldInfo.Min"/>, 1 at <see cref="FieldInfo.Max"/>, 0.5 when the range is empty, NaN for NaN.</summary>
    public static float Normalize(in FieldInfo field, float value)
    {
        if (float.IsNaN(value))
        {
            return float.NaN;
        }

        double span = field.Max - field.Min;
        return span > 0 && double.IsFinite(span) ? (float)Math.Clamp((value - field.Min) / span, 0.0, 1.0) : 0.5f;
    }

    private static byte Lerp(byte a, byte b, float f) => (byte)Math.Clamp((int)MathF.Round(a + ((b - a) * f)), 0, 255);
}

/// <summary>One field painted by a <see cref="DrawList"/> in the current frame: which field, and where its values are.</summary>
/// <param name="Field">The field's declaration, so the legend can show it.</param>
/// <param name="Domain">Whether values are per vertex or per triangle (meshes); points have one value each.</param>
/// <param name="ValueStart">Index of the first value in <see cref="DrawList.FieldValues"/>.</param>
/// <param name="ValueCount">The number of values.</param>
/// <param name="VertexBase">For a per-vertex mesh, the index in <see cref="DrawList.Vertices"/> of the mesh's first vertex.</param>
public readonly record struct FieldBinding(FieldInfo Field, FieldDomain Domain, int ValueStart, int ValueCount, int VertexBase);
