using OneGame.Host;
using UiColor = OneGame.UI.Color;

namespace OneScope.App;

/// <summary>Draws rectangles and text in screen pixels on top of the finished frame, through the host's overlay canvas.</summary>
internal sealed class Painter
{
    private OverlayCanvas? _canvas;

    /// <summary>Starts a frame's painting on <paramref name="canvas"/>.</summary>
    public void Begin(OverlayCanvas canvas) => _canvas = canvas;

    public void Fill(float x, float y, float width, float height, UiColor color)
    {
        if (width > 0 && height > 0)
        {
            _canvas?.Fill(x, y, width, height, color);
        }
    }

    public void Text(string text, float x, float y, float size, UiColor color)
    {
        if (text.Length > 0)
        {
            _canvas?.Text(text, x, y, size, color);
        }
    }

    public float Measure(string text, float size) => _canvas?.Measure(text, size) ?? text.Length * size * 0.55f;
}
