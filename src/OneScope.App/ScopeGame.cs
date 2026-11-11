using OneGame.Core;
using OneScope.Sdk;
using EntityId = OneScope.Sdk.EntityId;
using OneGame.Atlas;
using OneGame.Host;
using OneGame.Text;
using OneGame.UI;
using OneGame.UI.Kit;
using UiColor = OneGame.UI.Color;

namespace OneScope.App;

/// <summary>
/// The shell as a onegame program. The game has no entities: its "world" is the empty one, and everything the shell shows
/// lives in <see cref="ShellModel"/>. The UI tree draws the panels; <see cref="ScopeStage"/> draws the scene views and the timeline.
/// </summary>
internal sealed class ScopeGame : GameDefinition
{
    private const float RowHeight = 22;
    private readonly ShellModel _shell;
    private KitHost? _kit;
    private Action<Func<string, Element?>>? _attachFind;
    private readonly List<OverlayChoice> _overlayChoices = [];
    private (int View, int Source, int Version) _overlayKey = (-1, -1, -1);
    private int _dragView = -1;
    private float _lastX;
    private float _lastY;

    private sealed record OverlayChoice(int Id, string Name);

    public ScopeGame(ShellModel shell) => _shell = shell;

    /// <inheritdoc />
    public override string Name => "onescope";

    /// <inheritdoc />
    public override ulong Seed => 1;

    /// <inheritdoc />
    public override void RegisterEntities(EntityRegistry registry)
    {
    }

    /// <inheritdoc />
    public override void Setup(World world)
    {
    }

    /// <inheritdoc />
    public override void DefineFonts(FontLibrary fonts)
    {
        using Stream? stream = typeof(ScopeGame).Assembly.GetManifestResourceStream("NotoSans-Regular.ttf");
        if (stream is null)
        {
            return;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        fonts.Add(copy.ToArray());
    }

    /// <inheritdoc />
    public override void DefineArt(ArtAtlas art)
    {
        foreach (var a in KitArt.Assets)
        {
            art.AddSvg(a.Id, a.Svg, new NineSlice(a.Slice, a.Slice, a.Slice, a.Slice));
        }
    }

    /// <inheritdoc />
    public override void Style(UiTree ui)
    {
        _kit = new KitHost(ui);
        ui.AddStyleSheet(new StyleSheet()
            .Rule(".shell", s => s.Background(UiColor.Hex("#15171c")))
            .Rule(".panel", s => s.Background(UiColor.Hex("#1b1d23")))
            .Rule(".panel-title", s => s.Background(UiColor.Hex("#23262d")).TextColor(UiColor.Hex("#9aa3b5")).FontSize(Length.Px(11)))
            .Rule(".shell", s => s.WhiteSpace(WhiteSpace.NoWrap))
            .Rule(".mono", s => s.FontSize(Length.Px(12))));
    }

    /// <inheritdoc />
    public override IRenderStage CreateRenderStage(HostResources resources)
    {
        var stage = new ScopeStage(resources, _shell);
        _attachFind = find => stage.Find = find;
        return stage;
    }

    /// <inheritdoc />
    public override void DefineScreens(ScreenSet screens) =>
        screens.Add("main", game =>
        {
            Element root = Build(game);
            _attachFind?.Invoke(id => game.Ui.Find(id));

            return _kit!.AttachTo(root);
        });

    private Element Build(GameInstance game)
    {
        ShellModel shell = _shell;
        Element toolbar = Ui.Row(
                Ui.Label("onescope").FontSize(Length.Px(14)).TextColor(UiColor.Hex("#dfe4ee")),
                Ui.Label("").Named("source-label").BindText(() => shell.Version, _ => SourceText()).Margin(0, 10),
                UiKit.Button("|<", () => shell.Seek(shell.MinSteps), "seek-start"),
                UiKit.Button("<", () => Nudge(-1), "step-back"),
                UiKit.ToggleButton("Play", () => shell.SpeedMilliStepsPerSecond != 0, on => shell.SetSpeed(on ? 60_000 : 0), "play"),
                UiKit.Button(">", () => Nudge(1), "step-forward"),
                UiKit.Button(">|", () => shell.Seek(shell.MaxSteps), "seek-end"),
                UiKit.Button("Fork", ForkActive, "fork"),
                UiKit.ToggleButton("Live", () => shell.Sources.Any(x => x.Live is not null && x.Follow), on => SetFollow(on), "follow-live").BindVisible(() => shell.HasLive),
                Ui.Label("").Named("step-label").BindText(() => shell.Version, _ => $"step {shell.Step} / {shell.MaxSteps}").Margin(0, 10),
                Ui.Each(OverlayChoices, c => UiKit.ToggleButton(c.Name, () => OverlayOn(c.Id), on => SetOverlay(c.Id, on), "overlay/" + c.Id)),
                Ui.Spacer(),
                UiKit.ToggleButton("Wire", () => ActiveView()?.Wireframe == true, on => SetView(v => v.Wireframe = on), "wire"),
                UiKit.ToggleButton("Grid", () => ActiveView()?.Grid == true, on => SetView(v => v.Grid = on), "grid"),
                UiKit.ToggleButton("Entities", () => shell.Panels.Entities, on => { shell.Panels.Entities = on; shell.Touch(); }, "panel-entities"),
                UiKit.ToggleButton("Inspector", () => shell.Panels.Inspector, on => { shell.Panels.Inspector = on; shell.Touch(); }, "panel-inspector"),
                UiKit.ToggleButton("Timeline", () => shell.Panels.Timeline, on => { shell.Panels.Timeline = on; shell.Touch(); }, "panel-timeline"),
                UiKit.ToggleButton("Log", () => shell.Panels.Log, on => { shell.Panels.Log = on; shell.Touch(); }, "panel-log"))
            .Wrap().Shrink(0).Align(Align.Center).Gap(Length.Px(6)).Padding(Length.Px(6)).Class("panel").Named("toolbar")
            .BindVisible(() => shell.Solo < 0);

        ListView<TreeRow> tree = UiKit.TextList(() => shell.Tree(), r => r.Text, Length.Px(RowHeight), (row, _) => shell.Select(row.Entity.Id), null, "entities");
        tree.OnUpdate(_ => SyncSelection(tree));
        Element entities = Ui.Column(Title("Entities"), tree.Grow(1).Style(st => st.MinHeight(Length.Px(0))))
            .Width(Length.Px(230)).Class("panel").Named("entities-panel")
            .BindVisible(() => shell.Solo < 0 && shell.Panels.Entities);

        var views = Ui.Row().Grow(1).MinWidth(0).Gap(Length.Px(2));
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            Element v = Ui.Box().Grow(1).MinWidth(0).Named(ScopeStage.ViewId(index))
                .BindVisible(() => index < shell.Views.Count && (shell.Solo < 0 || shell.Solo == index));
            v.On(UiEventKind.PointerDown, (e, ev) => ViewPointer(index, e, ev));
            v.On(UiEventKind.PointerMove, (e, ev) => ViewPointer(index, e, ev));
            v.On(UiEventKind.PointerUp, (e, ev) => ViewPointer(index, e, ev));
            v.On(UiEventKind.Wheel, (e, ev) => ViewWheel(index, ev));
            views.Add(v);
        }

        TableView<InspectRow> props = UiKit.Table<InspectRow>(
            [
                new TableColumn<InspectRow>("Group", r => r.Group, ColumnWidth.Fixed(Length.Px(70))),
                new TableColumn<InspectRow>("Property", r => r.Name, ColumnWidth.Flex(1)),
                new TableColumn<InspectRow>("Value", r => r.Value, ColumnWidth.Flex(1.4f)),
            ],
            () => shell.Inspected(),
            Length.Px(RowHeight),
            id: "inspector",
            version: () => shell.Version);
        Element inspector = Ui.Column(Title("Inspector").BindText(() => shell.Selected.Value, id => id == 0 ? "Inspector" : "Inspector  ·  entity " + id), props.Grow(1).Style(st => st.MinHeight(Length.Px(0))))
            .Width(Length.Px(320)).Class("panel").Named("inspector-panel")
            .BindVisible(() => shell.Solo < 0 && shell.Panels.Inspector);

        Element main = Ui.Row(entities, views, inspector).Grow(1).Style(st => st.MinHeight(Length.Px(0))).Gap(Length.Px(2));

        Element timeline = Ui.Box().Height(Length.Px(190)).Shrink(0).Class("panel").Named("timeline").Focusable()
            .BindVisible(() => shell.Solo < 0 && shell.Panels.Timeline);
        timeline.On(UiEventKind.PointerDown, (e, ev) => TimelinePointer(e, ev, true));
        timeline.On(UiEventKind.PointerMove, (e, ev) => TimelinePointer(e, ev, false));
        timeline.On(UiEventKind.KeyDown, (e, ev) => TimelineKey(ev));

        ListView<LogRow> log = UiKit.TextList(() => shell.Log(), r => LogText(r), Length.Px(RowHeight), null, null, "log");
        Element logPanel = Ui.Column(Title("Event log"), log.Grow(1).Style(st => st.MinHeight(Length.Px(0))))
            .Height(Length.Px(130)).Shrink(0).Class("panel").Named("log-panel")
            .BindVisible(() => shell.Solo < 0 && shell.Panels.Log);

        return Ui.Column(toolbar, main, timeline, logPanel).Class("shell").Style(s => s.Anchor(AnchorPoint.Fill)).Gap(Length.Px(2));
    }

    private int _selectionVersion = -1;

    /// <summary>Highlights the shell's selected entity in the tree when it was chosen elsewhere (the inspect operation, a click in a view).</summary>
    private void SyncSelection(ListView<TreeRow> tree)
    {
        if (_selectionVersion == _shell.Version)
        {
            return;
        }

        _selectionVersion = _shell.Version;
        IReadOnlyList<TreeRow> rows = _shell.Tree();
        int index = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Entity.Id == _shell.Selected)
            {
                index = i;
                break;
            }
        }

        if (index >= 0 && tree.SelectedIndex != index)
        {
            tree.Select(index);
            tree.ScrollToIndex(index);
        }
    }

    private static Element Title(string text) => Ui.Label(text).Class("panel-title").Padding(Length.Px(4), Length.Px(8));

    private string SourceText()
    {
        if (_shell.Sources.Count == 0)
        {
            return "no source open";
        }

        SourceSession s = _shell.Sources[Math.Min(_shell.ActiveSource, _shell.Sources.Count - 1)];
        string live = s.Live is null ? string.Empty : s.LiveEnded is null ? (s.Follow ? "  ·  live" : "  ·  live (paused)") : "  ·  stream ended";
        return $"{s.Label}  ·  {s.Info.Dimensions}D  ·  {s.Info.Unit.Name}{live}";
    }

    private static string LogText(LogRow r) => $"{r.Step,7}  {r.Kind,-7} {r.Text}";

    private void Nudge(long delta)
    {
        if (_shell.Sources.Count > 0)
        {
            RunOp(() => _shell.StepTime(delta, _shell.Sources[Math.Min(_shell.ActiveSource, _shell.Sources.Count - 1)]));
        }
    }

    /// <summary>The toolbar's live toggle: follow the newest step of every live source, or stay where the cursor is.</summary>
    private void SetFollow(bool on)
    {
        foreach (SourceSession s in _shell.Sources.Where(x => x.Live is not null).ToList())
        {
            RunOp(() => _shell.SetFollow(s, on));
        }
    }

    /// <summary>Forks the active source at the shared cursor.</summary>
    private void ForkActive()
    {
        if (_shell.Sources.Count > 0)
        {
            SourceSession s = _shell.Sources[Math.Min(_shell.ActiveSource, _shell.Sources.Count - 1)];
            RunOp(() => _shell.Fork(s, Math.Clamp(_shell.Step, s.History.StartStep, s.History.EndStep), addView: true));
        }
    }

    /// <summary>Runs a shell change from the UI; a refusal goes to the event log instead of ending the frame.</summary>
    private void RunOp(Action change)
    {
        try
        {
            change();
        }
        catch (OneGame.Control.ControlException ex)
        {
            _shell.AddLog(_shell.ActiveSource, "error", ex.Message);
            _shell.Touch();
        }
    }

    private ViewState? ActiveView() => _shell.ActiveView < _shell.Views.Count ? _shell.Views[_shell.ActiveView] : null;

    private void SetView(Action<ViewState> change)
    {
        if (ActiveView() is { } v)
        {
            change(v);
            v.Touch();
            _shell.Touch();
        }
    }

    private IReadOnlyList<OverlayChoice> OverlayChoices()
    {
        ViewState? view = ActiveView();
        var key = (_shell.ActiveView, view?.Source ?? -1, _shell.Sources.Count);
        if (key != _overlayKey)
        {
            _overlayKey = key;
            _overlayChoices.Clear();
            if (view is not null)
            {
                foreach (var o in _shell.Source(view.Source).Info.Overlays)
                {
                    _overlayChoices.Add(new OverlayChoice(o.Id, o.Name));
                }
            }
        }

        return _overlayChoices;
    }

    private bool OverlayOn(int id) => ActiveView() is { } v && (uint)id < 64 && (v.Overlays & (1UL << id)) != 0;

    private void SetOverlay(int id, bool on) => SetView(v => v.Overlays = on ? v.Overlays | (1UL << id) : v.Overlays & ~(1UL << id));

    private bool ViewPointer(int index, Element element, UiEvent ev)
    {
        if (index >= _shell.Views.Count)
        {
            return false;
        }

        ViewState view = _shell.Views[index];
        OneGame.UI.Rect rect = ViewPicking.Round(element.AbsoluteBounds);
        switch (ev.Kind)
        {
            case UiEventKind.PointerDown:
                _shell.ActiveView = index;
                _lastX = ev.X;
                _lastY = ev.Y;
                element.Tree?.CapturePointer(element);
                _dragView = index;
                _drag = null;
                if (view.Source < _shell.Sources.Count)
                {
                    BeginPick(view, rect, ev);
                }

                _shell.Touch();
                return true;
            case UiEventKind.PointerMove when _drag is { } drag && _dragView == index:
                MoveDragged(drag, view, rect, ev);
                return true;
            case UiEventKind.PointerMove when _dragView == index:
                float dx = ev.X - _lastX;
                float dy = ev.Y - _lastY;
                _lastX = ev.X;
                _lastY = ev.Y;
                if (view.Kind == ViewKind.Plane2D)
                {
                    float ppu = element.AbsoluteBounds.Height / (2 * Math.Max(view.HalfHeight, 1e-9f));
                    view.Target -= new System.Numerics.Vector3(dx / ppu, -dy / ppu, 0);
                }
                else
                {
                    view.Yaw -= dx * 0.4f;
                    view.Pitch = Math.Clamp(view.Pitch + (dy * 0.4f), -89f, 89f);
                }

                view.Touch();
                _shell.Touch();
                return true;
            case UiEventKind.PointerUp:
                _dragView = -1;
                _drag = null;
                return true;
        }

        return false;
    }

    /// <summary>What a pointer drag on a body in a fork is doing: which fork, which body, and the plane the pointer moves it on.</summary>
    private sealed record Dragged(SourceSession Fork, PokeInfo Poke, EntityId Entity, System.Numerics.Vector3 PlaneOrigin, System.Numerics.Vector3 PlaneNormal);

    private Dragged? _drag;

    /// <summary>
    /// A press selects the entity under the pointer. On a fork whose module declares a drag poke, it also starts dragging that entity;
    /// anywhere else the press starts a camera drag.
    /// </summary>
    private void BeginPick(ViewState view, OneGame.UI.Rect rect, UiEvent ev)
    {
        SourceSession source = _shell.Source(view.Source);
        ViewPicking.Hit hit = ViewPicking.Pick(_shell, view, rect, ev.X, ev.Y);
        _shell.SelectSource(view.Source);
        if (!hit.Entity.IsSome)
        {
            return;
        }

        _shell.Select(hit.Entity);
        if (source.IsFork && source.DragPoke is { } poke)
        {
            // Dragging pokes at the end of the fork's recording, so a fork scrubbed back first runs forward to its end.
            RunOp(() => _shell.Seek(source.History.EndStep));
            _drag = new Dragged(source, poke, hit.Entity, hit.Point, hit.PlaneNormal);
        }
    }

    /// <summary>One pointer move of a drag is one step of the fork with the module's drag poke aimed at the pointer.</summary>
    private void MoveDragged(Dragged drag, ViewState view, OneGame.UI.Rect rect, UiEvent ev)
    {
        if (ev.X == _lastX && ev.Y == _lastY)
        {
            // The pointer has not moved (a release at the spot of the last move reports it again): no new step.
            return;
        }

        _lastX = ev.X;
        _lastY = ev.Y;
        System.Numerics.Vector3? at = ViewPicking.PointOnPlane(view, rect, ev.X, ev.Y, drag.PlaneOrigin, drag.PlaneNormal);
        if (at is null)
        {
            return;
        }

        ScopeValue[] args = [ScopeValue.OfEntity(drag.Entity), ScopeValue.OfVector(at.Value)];
        try
        {
            _shell.Poke(drag.Fork, new PokeCall(drag.Poke.Id, args));
            _shell.StepFork(drag.Fork, 1);
        }
        catch (OneGame.Control.ControlException ex)
        {
            _shell.AddLog(drag.Fork.Index, "error", ex.Message);
            _shell.Touch();
            _drag = null;
        }
    }

    private bool ViewWheel(int index, UiEvent ev)
    {
        if (index >= _shell.Views.Count)
        {
            return false;
        }

        ViewState view = _shell.Views[index];
        float factor = MathF.Pow(0.9f, ev.DeltaY);
        if (view.Kind == ViewKind.Plane2D)
        {
            view.HalfHeight = Math.Max(view.HalfHeight * factor, 1e-6f);
        }
        else
        {
            view.Distance = Math.Max(view.Distance * factor, 1e-6f);
        }

        view.Touch();
        _shell.Touch();
        return true;
    }

    private bool TimelinePointer(Element element, UiEvent ev, bool down)
    {
        if (down)
        {
            element.Tree?.CapturePointer(element);
        }
        else if (!element.IsPressed)
        {
            return false;
        }

        if (_shell.Sources.Count > 0)
        {
            _shell.Seek(TimelineLayout.StepAt(element.AbsoluteBounds, ev.X, new TimeRange(_shell.MinSteps, _shell.MaxSteps)));
        }

        return true;
    }

    private bool TimelineKey(UiEvent ev)
    {
        long big = ev.Modifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (ev.Key)
        {
            case "ArrowLeft":
                Nudge(-big);
                return true;
            case "ArrowRight":
                Nudge(big);
                return true;
            case "Home":
                Nudge(_shell.MinSteps - _shell.MaxSteps);
                return true;
            case "End":
                Nudge(_shell.MaxSteps);
                return true;
            case " ":
                _shell.SetSpeed(_shell.SpeedMilliStepsPerSecond == 0 ? 60_000 : 0);
                return true;
        }

        return false;
    }
}
