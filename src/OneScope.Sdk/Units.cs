namespace OneScope.Sdk;

/// <summary>The length unit a source's world coordinates are expressed in.</summary>
/// <param name="Name">Short display name used on grids, rulers and labels, such as "m" or "mm".</param>
/// <param name="Metres">Metres per unit. Zero means the unit has no physical size (pixels, tiles).</param>
public readonly record struct LengthUnit(string Name, double Metres)
{
    /// <summary>Metres.</summary>
    public static LengthUnit Metre => new("m", 1.0);

    /// <summary>Millimetres.</summary>
    public static LengthUnit Millimetre => new("mm", 0.001);

    /// <summary>Centimetres.</summary>
    public static LengthUnit Centimetre => new("cm", 0.01);

    /// <summary>Kilometres.</summary>
    public static LengthUnit Kilometre => new("km", 1000.0);

    /// <summary>Inches.</summary>
    public static LengthUnit Inch => new("in", 0.0254);

    /// <summary>Feet.</summary>
    public static LengthUnit Foot => new("ft", 0.3048);

    /// <summary>Screen pixels or tiles: a unit with no physical size.</summary>
    public static LengthUnit Pixel => new("px", 0.0);

    /// <summary>True when <see cref="Metres"/> is positive, so the unit can be converted to metres.</summary>
    public bool HasPhysicalSize => Metres > 0.0;
}
