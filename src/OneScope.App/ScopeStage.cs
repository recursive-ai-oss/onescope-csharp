using OneGame.Host;
using OneGame.Render2D;
using OneGame.UI;
using UiColor = OneGame.UI.Color;
using UiRect = OneGame.UI.Rect;

namespace OneScope.App;

/// <summary>
/// The shell's render stage: onegame's 2D stage draws the panels from the UI tree, then this draws what the UI can't:
/// the scene views (2D with the canvas, 3D through the 3D renderer), and, as the frame's overlay, the labels and the timeline.
/// </summary>
internal sealed class ScopeStage : Render2DStage, IFrameOverlay
{
    private readonly ShellModel _shell;
    private readonly List<ViewCache> _caches = [];
    private Space3DRenderer? _space;
    private Renderer2D? _owner;
    private readonly Painter _painter;
    private IFrameOverlay? _inner;

    public ScopeStage(HostResources resources, ShellModel shell)
        : base(resources)
    {
        _shell = shell;
        _painter = new Painter();
    }

    /// <summary>
    /// Makes this stage the frame overlay. The control layer installs onegame's own debug panel as the overlay when it is created,
    /// so this runs after it and keeps that panel drawing on top of the shell's labels and timeline.
    /// </summary>
    public void Attach()
    {
        if (!ReferenceEquals(Overlay, this))
        {
            _inner = Overlay;
            Overlay = this;
        }
    }

    /// <summary>Finds a UI element by id; set once the screen exists.</summary>
    public Func<string, Element?>? Find { get; set; }

    /// <inheritdoc />
    public override string Name => "onescope";

    /// <inheritdoc />
    protected override void RendererCreated(Renderer2D renderer)
    {
        base.RendererCreated(renderer);
        _space ??= new Space3DRenderer(renderer.Device);
    }

    /// <inheritdoc />
    protected override void DrawOverUi(Renderer2D renderer, in FrameInfo frame)
    {
        base.DrawOverUi(renderer, frame);
        if (!ReferenceEquals(_owner, renderer))
        {
            // Textures belong to one renderer; a new renderer (another target format) means starting the views over.
            foreach (ViewCache c in _caches)
            {
                c.Dispose();
                c.Key = (-1, -1, 0, 0, 0, 0);
            }

            _owner = renderer;
        }

        while (_caches.Count < _shell.Views.Count)
        {
            _caches.Add(new ViewCache());
        }

        for (int i = 0; i < _caches.Count; i++)
        {
            ViewCache cache = _caches[i];
            cache.Visible = false;
            if (i >= _shell.Views.Count || Find?.Invoke(ViewId(i)) is not { } element || !element.IsEffectivelyVisible)
            {
                continue;
            }

            UiRect bounds = element.AbsoluteBounds;
            var rect = new UiRect(MathF.Round(bounds.X), MathF.Round(bounds.Y), MathF.Round(bounds.Width), MathF.Round(bounds.Height));
            if (rect.Width < 4 || rect.Height < 4)
            {
                continue;
            }

            ViewState view = _shell.Views[i];
            SourceSession source = _shell.Source(view.Source);
            var key = (_shell.Version, view.Version, (int)rect.Width, (int)rect.Height, (int)view.Kind, view.Source);
            cache.Visible = true;
            bool moved = cache.Rect != rect;
            cache.Rect = rect;
            if (view.Kind == ViewKind.Plane2D)
            {
                if (key != cache.Key || moved)
                {
                    Plane2DRenderer.Rebuild(cache, view, source, rect);
                    cache.Key = key;
                }

                Plane2DRenderer.Draw(renderer.Ui, rect, view, cache);
            }
            else if (_space is not null)
            {
                if (key != cache.Key || cache.Texture is null || moved)
                {
                    cache.Texture = _space.Render(renderer, cache, view, source, rect);
                    cache.Key = key;
                }

                renderer.Ui.Image(rect, cache.Texture, UiColor.White);
            }
        }

    }

    /// <summary>The id of the UI element that holds scene view <paramref name="index"/>.</summary>
    public static string ViewId(int index) => index switch
    {
        0 => "view/0",
        1 => "view/1",
        2 => "view/2",
        _ => "view/3",
    };

    /// <inheritdoc />
    void IFrameOverlay.Draw(OverlayCanvas overlay)
    {
        _painter.Begin(overlay);
        Painter canvas = _painter;
        for (int i = 0; i < _caches.Count && i < _shell.Views.Count; i++)
        {
            ViewCache cache = _caches[i];
            if (!cache.Visible)
            {
                continue;
            }

            foreach (ScreenLabel l in cache.Labels)
            {
                canvas.Text(l.Text, l.X, l.Y, l.Size, l.Color);
            }

            if (_shell.Solo < 0)
            {
                ViewState view = _shell.Views[i];
                SourceSession source = _shell.Source(view.Source);
                UiRect r = cache.Rect;
                string title = $"{i}  {(view.Kind == ViewKind.Space3D ? "3D" : "2D")}  {source.Info.Name}";
                canvas.Fill(r.X, r.Y, Math.Min(canvas.Measure(title, 11) + 14, r.Width), 18, new UiColor(0, 0, 0, 150));
                canvas.Text(title, r.X + 6, r.Y + 3, 11, UiColor.White);
                if (i == _shell.ActiveView)
                {
                    UiColor accent = new(90, 150, 255);
                    canvas.Fill(r.X, r.Y, r.Width, 2, accent);
                    canvas.Fill(r.X, r.Bottom - 2, r.Width, 2, accent);
                    canvas.Fill(r.X, r.Y, 2, r.Height, accent);
                    canvas.Fill(r.Right - 2, r.Y, 2, r.Height, accent);
                }
            }
        }

        if (_shell.Solo < 0 && _shell.Panels.Timeline && Find?.Invoke("timeline") is { } timeline && timeline.IsEffectivelyVisible)
        {
            TimelineRenderer.Draw(canvas, timeline.AbsoluteBounds, _shell);
        }

        _inner?.Draw(overlay);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (ViewCache c in _caches)
        {
            c.Dispose();
        }

        _space?.Dispose();
        base.Dispose();
    }
}
