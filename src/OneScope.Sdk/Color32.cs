namespace OneScope.Sdk;

/// <summary>A non-premultiplied 8-bit-per-channel colour.</summary>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
/// <param name="A">Alpha, 0 (clear) to 255 (opaque).</param>
public readonly record struct Color32(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Opaque white.</summary>
    public static Color32 White => new(255, 255, 255);

    /// <summary>Opaque mid grey.</summary>
    public static Color32 Grey => new(128, 128, 128);
}
