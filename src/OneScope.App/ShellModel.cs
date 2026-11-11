using System.Numerics;
using OneGame.Control;
using OneScope.Attach;
using RecursiveAI.OnePhysics.Scope;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Whether a scene view looks at the world as a plane (x right, y up) or as a space (y up, orbit camera).</summary>
internal enum ViewKind
{
    Plane2D,
    Space3D,
}

/// <summary>One scene view: which source it shows, which overlays are on, and where its camera is. Camera values are view-side only.</summary>
internal sealed class ViewState
{
    /// <summary>The index of the source the view draws.</summary>
    public int Source { get; set; }

    public ViewKind Kind { get; set; }

    /// <summary>Bit i is overlay id i of the source.</summary>
    public ulong Overlays { get; set; }

    public bool Wireframe { get; set; }

    public bool Grid { get; set; } = true;

    /// <summary>2D: world point at the view's centre. 3D: the orbit target.</summary>
    public Vector3 Target { get; set; }

    /// <summary>2D: world units from the centre to the top edge.</summary>
    public float HalfHeight { get; set; } = 1;

    /// <summary>3D: orbit distance in world units.</summary>
    public float Distance { get; set; } = 1;

    /// <summary>3D: degrees around the vertical axis.</summary>
    public float Yaw { get; set; } = 35;

    /// <summary>3D: degrees above the horizon.</summary>
    public float Pitch { get; set; } = 25;

    /// <summary>Raised by every change, so renderers can tell a view is unchanged.</summary>
    public int Version { get; private set; }

    public void Touch() => Version++;
}

/// <summary>An opened file: the module, the source it produced and its history.</summary>
internal sealed class SourceSession
{
    public SourceSession(int index, string path, IScopeModule module, IScopeSource source, History history, int keyframeInterval, SourceSession? parent = null, Branch? branch = null, LiveSession? live = null)
    {
        Live = live;
        Follow = live is not null;
        Parent = parent;
        Branch = branch;
        KeyframeInterval = keyframeInterval;
        Index = index;
        Path = path;
        Module = module;
        Source = source;
        History = history;
    }

    public int Index { get; }

    /// <summary>The source this one was forked from, or null for an opened file.</summary>
    public SourceSession? Parent { get; }

    /// <summary>The fork behind this source, or null for an opened file (which pokes can never change).</summary>
    public Branch? Branch { get; }

    public bool IsFork => Branch is not null;

    /// <summary>The connection to a running program, or null for an opened file or a fork. Its recording grows as the program steps.</summary>
    public LiveSession? Live { get; }

    /// <summary>For a live source: whether the shared cursor follows the program's newest step as records arrive. Scrubbing back turns it off.</summary>
    public bool Follow { get; set; }

    /// <summary>What ended a live source's stream, once it has ended; null while it streams.</summary>
    public string? LiveEnded { get; set; }

    /// <summary>Whether a live source stopped because the viewer reached its cap on received records.</summary>
    public bool LiveCapped { get; set; }

    /// <summary>The opened file at the root of this source's family of forks.</summary>
    public SourceSession Root => Parent?.Root ?? this;

    /// <summary>How the source is named in the views and the timeline.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The poke the app calls when an entity is dragged, if the module declares one.</summary>
    public PokeInfo? DragPoke
    {
        get
        {
            foreach (PokeInfo p in Info.Pokes)
            {
                if (p.Role == PokeRole.DragToPoint && p.Params.Length == 2 && p.Params[0].Kind == ValueKind.Entity && p.Params[1].Kind == ValueKind.Vector)
                {
                    return p;
                }
            }

            return null;
        }
    }

    public int KeyframeInterval { get; }

    public string Path { get; }

    public IScopeModule Module { get; }

    public IScopeSource Source { get; }

    public History History { get; }

    public SourceInfo Info => Source.Info;
}

/// <summary>One line of the event log.</summary>
internal readonly record struct LogRow(long Step, int Source, string Kind, string Text);

/// <summary>One row of the entity tree.</summary>
internal readonly record struct TreeRow(EntityInfo Entity, int Depth, string Text);

/// <summary>A row of the inspector.</summary>
internal readonly record struct InspectRow(string Group, string Name, string Value);

/// <summary>Which panels the shell shows.</summary>
internal sealed class PanelState
{
    public bool Entities { get; set; } = true;

    public bool Inspector { get; set; } = true;

    public bool Timeline { get; set; } = true;

    public bool Log { get; set; } = true;
}

/// <summary>
/// Everything the shell is showing, without any drawing: the opened sources on one shared time axis, the scene views,
/// the selection, the event log and the playback speed. Operations, the UI and the renderers all read and change this one object.
/// Playback speed is presentation only: it counts presentation frames, never a clock, so it can't decide a world's state.
/// </summary>
internal sealed partial class ShellModel
{
    /// <summary>Largest file <c>open</c> reads.</summary>
    public const long MaxFileBytes = 512L << 20;

    /// <summary>A recording this short or shorter is scanned on open so the whole timeline is filled.</summary>
    public const long ScanLimit = 200_000;

    /// <summary>Presentation frames per second that playback speed is counted in.</summary>
    public const int PresentationRate = 60;

    private readonly List<IScopeModule> _modules = [];
    private readonly List<SourceSession> _sources = [];
    private readonly List<ViewState> _views = [];
    private readonly List<LogRow> _log = [];
    private readonly List<TreeRow> _tree = [];
    private readonly List<EntityInfo> _entityScratch = [];
    private readonly PropertyList _props = new();
    private readonly List<InspectRow> _inspect = [];
    private readonly Dictionary<ulong, int> _depth = [];
    private int _treeVersion = -1;
    private int _logVersion = -1;
    private int _inspectVersion = -1;
    private long _playAccumulator;

    public ShellModel()
    {
        Views = _views;
    }

    /// <summary>Raised by every state change a panel or view could show.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<SourceSession> Sources => _sources;

    public IReadOnlyList<ViewState> Views { get; }

    public PanelState Panels { get; } = new();

    /// <summary>The shared time cursor: the step every source is moved to (each clamps to its own length).</summary>
    public long Step { get; private set; }

    /// <summary>Which source the entity tree, inspector and timeline show.</summary>
    public int ActiveSource { get; private set; }

    /// <summary>Which view the toolbar's overlay toggles apply to.</summary>
    public int ActiveView { get; set; }

    public EntityId Selected { get; private set; }

    /// <summary>Playback speed in thousandths of a step per presentation frame ×60, that is, millisteps per second. 0 is paused.</summary>
    public long SpeedMilliStepsPerSecond { get; private set; }

    public bool Loop { get; set; }

    /// <summary>Set while a capture shows one view alone at full size, or -1.</summary>
    public int Solo { get; set; } = -1;

    public IReadOnlyList<IScopeModule> Modules => _modules;

    /// <summary>The last step any source has reached: the end of the shared time axis.</summary>
    public long MaxSteps
    {
        get
        {
            long max = 0;
            foreach (SourceSession s in _sources)
            {
                max = Math.Max(max, s.History.EndStep);
            }

            return max;
        }
    }

    /// <summary>The earliest step any source starts at: the start of the shared time axis (0 unless every source is a live attach).</summary>
    public long MinSteps
    {
        get
        {
            long min = long.MaxValue;
            foreach (SourceSession s in _sources)
            {
                min = Math.Min(min, s.History.StartStep);
            }

            return min == long.MaxValue ? 0 : min;
        }
    }

    public void Touch() => Version++;

    public void AddView(ViewState view)
    {
        _views.Add(view);
        Touch();
    }

    public void RemoveView(int index)
    {
        _views.RemoveAt(index);
        ActiveView = Math.Min(ActiveView, _views.Count - 1);
        Touch();
    }

    public void AddModule(IScopeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules.Add(module);
    }

    /// <summary>Adds the modules that ship inside the app: today the OnePhysics module.</summary>
    public void AddBundledModules() => AddModule(new OnePhysicsModule());

    public SourceSession Source(int index)
    {
        if ((uint)index >= (uint)_sources.Count)
        {
            throw new ControlException("not_found", _sources.Count == 0
                ? "No source is open; call open first."
                : $"Source {index} does not exist; sources are numbered 0 to {_sources.Count - 1}.");
        }

        return _sources[index];
    }

    public ViewState View(int index)
    {
        if ((uint)index >= (uint)_views.Count)
        {
            throw new ControlException("not_found", $"View {index} does not exist; views are numbered 0 to {_views.Count - 1}.");
        }

        return _views[index];
    }

    /// <summary>Opens a file with the first module that accepts it.</summary>
    /// <exception cref="ControlException"><c>not_found</c>, <c>too_large</c>, <c>no_module</c> or <c>bad_format</c>.</exception>
    public SourceSession Open(string path, string? moduleId, int keyframeInterval, bool? scan)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ControlException("not_found", $"File '{path}' does not exist; expected a scene, snapshot or replay file a loaded module can open.");
        }

        long length = new FileInfo(full).Length;
        if (length > MaxFileBytes)
        {
            throw new ControlException("too_large", $"File '{path}' is {length} bytes; the limit is {MaxFileBytes}.");
        }

        if (_modules.Count == 0)
        {
            throw new ControlException("no_module", $"No module is loaded, so '{path}' can't be opened; start with --module <assembly> to load one.");
        }

        byte[] bytes = File.ReadAllBytes(full);
        string name = Path.GetFileName(full);
        var problems = new List<string>();
        IScopeSource? source = null;
        IScopeModule? used = null;
        foreach (IScopeModule module in _modules)
        {
            if (moduleId is not null && module.Id != moduleId)
            {
                continue;
            }

            try
            {
                source = module.Open(bytes, name);
                used = module;
                break;
            }
            catch (ScopeFormatException ex)
            {
                problems.Add($"{module.Id}: {ex.Message}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or OverflowException or InvalidDataException)
            {
                problems.Add($"{module.Id}: {ex.Message}");
            }
        }

        if (source is null || used is null)
        {
            string tried = moduleId is not null && problems.Count == 0
                ? $"no loaded module has id '{moduleId}'"
                : string.Join("; ", problems);
            throw new ControlException("bad_format", $"'{path}' is not a file any loaded module can open ({tried}).");
        }

        var options = new ScopeHistoryOptions { KeyframeInterval = keyframeInterval > 0 ? keyframeInterval : ScopeHistoryOptions.DefaultKeyframeInterval };
        History history;
        try
        {
            history = new History(source, options);
            if (scan ?? history.StepCount <= ScanLimit)
            {
                history.Scan();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ScopeFormatException)
        {
            throw new ControlException("bad_format", $"'{path}' opened, but its recording did not play: {ex.Message}");
        }

        var session = new SourceSession(_sources.Count, full, used, source, history, options.KeyframeInterval) { Label = name };
        AddSession(session);
        AddLog(session.Index, "shell", $"opened {name} ({used.Id}, {source.Info.Dimensions}D, {history.StepCount} steps)");
        Touch();
        return session;
    }

    /// <summary>Adds a source with the default views: a 3D source gets 3D and 2D views, a 2D source the reverse.</summary>
    private void AddSession(SourceSession session)
    {
        _sources.Add(session);
        if (_sources.Count == 1)
        {
            ActiveSource = 0;
        }

        session.History.Seek(Math.Clamp(Step, session.History.StartStep, session.History.EndStep));
        AddDefaultViews(session);
    }

    private void AddDefaultViews(SourceSession session)
    {
        IScopeSource source = session.Source;
        var view = new ViewState { Source = session.Index, Kind = source.Info.Dimensions == 3 ? ViewKind.Space3D : ViewKind.Plane2D, Overlays = DefaultOverlays(source.Info) };
        _views.Add(view);
        FrameView(view);
        if (source.Info.Dimensions == 3 || _views.Count == 1)
        {
            // A second view of the other kind, so a 2D and a 3D look at the same source side by side.
            var other = new ViewState { Source = session.Index, Kind = view.Kind == ViewKind.Space3D ? ViewKind.Plane2D : ViewKind.Space3D, Overlays = view.Overlays };
            _views.Add(other);
            FrameView(other);
        }
    }

    public static ulong DefaultOverlays(SourceInfo info)
    {
        ulong mask = 0;
        foreach (OverlayInfo o in info.Overlays)
        {
            if (o.DefaultOn && (uint)o.Id < 64)
            {
                mask |= 1UL << o.Id;
            }
        }

        return mask;
    }

    /// <summary>
    /// Places a view's camera on what matters: the selected entity when <paramref name="selection"/> is set and the source draws it, otherwise the
    /// bodies that move. Static and sensor entities (a ground plane, a trigger zone) and drawings that belong to no entity (bounds, grids) are left out
    /// while anything else is drawn, so a huge floor doesn't shrink the bodies to dots. A module that tags nothing is framed without items far larger than
    /// the typical one. Only what the view's overlays draw counts.
    /// </summary>
    public void FrameView(ViewState view, bool selection = false)
    {
        SourceSession s = Source(view.Source);
        var list = new DrawList();

        // What the view shows decides the frame; a view with every overlay off is framed on everything the source can draw.
        s.History.View.Draw(list, new DrawRequest(view.Overlays));
        if (list.Items.Length == 0)
        {
            s.History.View.Draw(list, new DrawRequest(ulong.MaxValue));
        }

        var kinds = new Dictionary<ulong, string>();
        var entities = new List<EntityInfo>();
        s.History.View.CollectEntities(entities);
        foreach (EntityInfo e in entities)
        {
            kinds[e.Id.Value] = e.Kind;
        }

        Vector3 min = default;
        Vector3 max = default;
        bool found = false;
        if (selection && Selected.IsSome && ReferenceEquals(Source(ActiveSource).Root, s.Root))
        {
            EntityId wanted = Selected;
            found = DrawBounds.TryCompute(list, item => item.Entity == wanted, out min, out max);
        }

        if (!found)
        {
            found = DrawBounds.TryCompute(list, item => item.Entity.IsSome && kinds.TryGetValue(item.Entity.Value, out string? k) && k is not ("static" or "sensor"), out min, out max)
                || DrawBounds.TryComputeWithoutScenery(list, out min, out max);
        }

        if (!found)
        {
            min = Vector3.Zero;
            max = Vector3.One;
        }

        Vector3 centre = (min + max) / 2;
        Vector3 size = max - min;
        if (view.Kind == ViewKind.Plane2D)
        {
            view.Target = new Vector3(centre.X, centre.Y, 0);
            view.HalfHeight = Math.Max(Math.Max(size.X, size.Y), 1e-3f) * 0.56f;
        }
        else
        {
            view.Target = centre;
            float radius = Math.Max(size.Length() / 2, 1e-3f);
            view.Distance = radius / MathF.Sin(0.7f / 2) * 1.15f;
        }

        view.Touch();
    }

    /// <summary>The entity to highlight in a view: the selection, when the view shows the active source or a fork of the same file.</summary>
    public EntityId HighlightFor(ViewState view) =>
        ActiveSource < _sources.Count && view.Source < _sources.Count && ReferenceEquals(_sources[ActiveSource].Root, _sources[view.Source].Root)
            ? Selected
            : EntityId.None;

    /// <summary>The most scene views the shell lays out.</summary>
    public const int MaxViews = 4;

    /// <summary>
    /// Forks <paramref name="parent"/> at <paramref name="step"/>. The fork is a new source with its own recording and history, so it shows up in
    /// every operation that takes a source. The shared time cursor moves to the fork step, and the fork becomes the active source.
    /// </summary>
    public SourceSession Fork(SourceSession parent, long step, bool addView)
    {
        if (step < parent.History.StartStep || step > parent.History.EndStep)
        {
            throw new ControlException("bad_args", $"Source {parent.Index} runs from step {parent.History.StartStep} to {parent.History.EndStep}; fork at a step in that range.");
        }

        Branch branch = parent.History.Fork(step);
        int ordinal = 1 + _sources.Count(x => x.Branch is not null && ReferenceEquals(x.Root, parent.Root));
        var fork = new SourceSession(_sources.Count, parent.Path, parent.Module, branch.History.Source, branch.History, parent.KeyframeInterval, parent, branch)
        {
            Label = $"{parent.Root.Label} fork {ordinal} @{step}",
        };
        _sources.Add(fork);
        ActiveSource = fork.Index;
        if (addView && _views.Count < MaxViews)
        {
            ViewState? like = _views.FirstOrDefault(v => v.Source == parent.Index) ?? _views.FirstOrDefault();
            var view = new ViewState { Source = fork.Index, Kind = like?.Kind ?? ViewKind.Plane2D, Overlays = like?.Overlays ?? DefaultOverlays(fork.Info) };
            if (like is not null)
            {
                view.Target = like.Target;
                view.HalfHeight = like.HalfHeight;
                view.Distance = like.Distance;
                view.Yaw = like.Yaw;
                view.Pitch = like.Pitch;
                view.Wireframe = like.Wireframe;
                view.Grid = like.Grid;
            }

            _views.Add(view);
        }

        AddLog(fork.Index, "fork", $"{fork.Label} from source {parent.Index}");
        if (step != Step)
        {
            Seek(step);
        }
        else
        {
            Touch();
        }

        return fork;
    }

    /// <summary>
    /// Queues a poke on a fork for its next step.
    /// </summary>
    /// <exception cref="ControlException"><c>read_only</c> for an opened file; <c>rejected</c> when the fork's cursor is not at its end; <c>bad_args</c> when the module refuses the poke.</exception>
    public void Poke(SourceSession source, in PokeCall call)
    {
        Branch branch = RequireFork(source);
        if (source.History.CurrentStep != source.History.EndStep)
        {
            throw new ControlException("rejected", $"Source {source.Index} is at step {source.History.CurrentStep} but its recording ends at {source.History.EndStep}; pokes apply at the end. Seek to step {source.History.EndStep} first.");
        }

        try
        {
            branch.Poke(call);
        }
        catch (ArgumentException ex)
        {
            throw new ControlException("bad_args", $"Poke '{call.Poke}' was refused: {ex.Message}");
        }

        Touch();
    }

    /// <summary>
    /// Runs <paramref name="count"/> steps of a fork at the end of its recording; the first one carries the queued pokes. The shared cursor follows the fork.
    /// </summary>
    public void StepFork(SourceSession source, long count)
    {
        Branch branch = RequireFork(source);
        for (long i = 0; i < count; i++)
        {
            branch.Step();
        }

        Seek(source.History.CurrentStep);
    }

    /// <summary>The source's recording takes pokes only on forks.</summary>
    public static Branch RequireFork(SourceSession source) =>
        source.Branch ?? throw new ControlException("read_only", $"Source {source.Index} ('{source.Label}') is an opened file and can't be poked; fork it first (the fork operation) and poke the fork.");

    /// <summary>
    /// Moves along the time axis by <paramref name="delta"/>. When the target is a fork with its cursor at the end of its recording, going forward runs
    /// new steps in the fork (carrying queued pokes); otherwise it is a seek of every source.
    /// </summary>
    public void StepTime(long delta, SourceSession? target)
    {
        if (delta > 0 && target is { IsFork: true } fork && fork.History.CurrentStep == fork.History.EndStep)
        {
            StepFork(fork, delta);
            return;
        }

        Seek(Math.Clamp(Step + delta, MinSteps, MaxSteps));
    }

    /// <summary>Compares a fork with another source at a step. Both sources keep the cursors the shell gave them.</summary>
    public Difference? Diff(SourceSession a, SourceSession b, long step, out ulong hashA, out ulong hashB)
    {
        long from = Math.Max(a.History.StartStep, b.History.StartStep);
        long limit = Math.Min(a.History.EndStep, b.History.EndStep);
        if (step < from || step > limit)
        {
            throw new ControlException("bad_args", $"Step {step} is outside both recordings: source {a.Index} runs {a.History.StartStep} to {a.History.EndStep} and source {b.Index} runs {b.History.StartStep} to {b.History.EndStep}; use {from} to {limit}.");
        }

        if (!ReferenceEquals(a.Module, b.Module))
        {
            throw new ControlException("bad_args", $"Sources {a.Index} and {b.Index} come from different modules ({a.Info.ModuleId}, {b.Info.ModuleId}) and can't be compared.");
        }

        try
        {
            Difference? found = a.History.Diff(b.History, step);
            hashA = a.History.StateHash;
            hashB = b.History.StateHash;
            return found;
        }
        finally
        {
            Seek(Step);
        }
    }

    /// <summary>Moves every source to <paramref name="step"/> (each clamped to its own length) and clears any axis view.</summary>
    public void Seek(long step)
    {
        SeekCore(step);

        // Scrubbing off the newest step of a live source stops following; coming back to it follows again.
        foreach (SourceSession s in _sources)
        {
            if (s.Live is not null)
            {
                s.Follow = step >= s.History.EndStep;
            }
        }
    }

    private void SeekCore(long step)
    {
        if (step < MinSteps || step > MaxSteps)
        {
            throw new ControlException("bad_args", $"Step {step} is outside the time axis; use {MinSteps} to {MaxSteps}.");
        }

        Step = step;
        foreach (SourceSession s in _sources)
        {
            s.History.Seek(Math.Clamp(step, s.History.StartStep, s.History.EndStep));
        }

        Touch();
    }

    /// <summary>Seeks a source along a non-time axis, leaving the time cursor alone.</summary>
    public void SeekAxis(int source, string axis, long position)
    {
        SourceSession s = Source(source);
        if (axis != AxisIds.Time && !s.Info.Axes.Any(a => a.Id == axis))
        {
            throw new ControlException("bad_args", $"Source {source} has no axis '{axis}'; its axes are {string.Join(", ", s.Info.Axes.Select(a => a.Id))}.");
        }

        try
        {
            s.History.Seek(axis, position);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ControlException("bad_args", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new ControlException("rejected", ex.Message);
        }

        Touch();
    }

    public void SelectSource(int index)
    {
        Source(index);
        if (ActiveSource != index)
        {
            ActiveSource = index;
            Selected = EntityId.None;
            Touch();
        }
    }

    public void Select(EntityId id)
    {
        if (Selected != id)
        {
            Selected = id;
            Touch();
        }
    }

    public void SetSpeed(long milliStepsPerSecond)
    {
        SpeedMilliStepsPerSecond = milliStepsPerSecond;
        _playAccumulator = 0;
        Touch();
    }

    /// <summary>Advances playback by presentation frames. Returns the number of steps moved.</summary>
    public long AdvancePlayback(int frames)
    {
        long moved = 0;
        for (int i = 0; i < frames && SpeedMilliStepsPerSecond != 0; i++)
        {
            _playAccumulator += SpeedMilliStepsPerSecond;
            long whole = _playAccumulator / (1000L * PresentationRate);
            _playAccumulator -= whole * 1000L * PresentationRate;
            if (whole == 0)
            {
                continue;
            }

            long target = Step + whole;
            long max = MaxSteps;
            long min = MinSteps;
            if (target >= max || target <= min)
            {
                if (Loop && max > min)
                {
                    target = target <= min ? max : min;
                }
                else
                {
                    target = Math.Clamp(target, min, max);
                    SpeedMilliStepsPerSecond = 0;
                }
            }

            moved += Math.Abs(target - Step);
            Seek(target);
        }

        return moved;
    }

    public void AddLog(int source, string kind, string text)
    {
        _log.Add(new LogRow(Step, source, kind, text));
        if (_log.Count > 1000)
        {
            _log.RemoveRange(0, 500);
        }

        _logVersion = -1;
    }

    /// <summary>The entity tree of the active source at the cursor, parents first.</summary>
    public IReadOnlyList<TreeRow> Tree()
    {
        if (_treeVersion == Version)
        {
            return _tree;
        }

        _tree.Clear();
        _depth.Clear();
        if (_sources.Count > 0)
        {
            SourceSession s = Source(ActiveSource);
            s.History.View.CollectEntities(_entityScratch);
            foreach (EntityInfo e in _entityScratch)
            {
                int depth = e.Parent.IsSome && _depth.TryGetValue(e.Parent.Value, out int d) ? d + 1 : 0;
                _depth[e.Id.Value] = depth;
                _tree.Add(new TreeRow(e, depth, string.Concat(new string(' ', depth * 2), e.Name, "  ·  ", e.Kind)));
            }
        }

        _treeVersion = Version;
        return _tree;
    }

    /// <summary>The selected entity's properties, or empty.</summary>
    public IReadOnlyList<InspectRow> Inspected()
    {
        if (_inspectVersion == Version)
        {
            return _inspect;
        }

        _inspect.Clear();
        if (_sources.Count > 0 && Selected.IsSome && Source(ActiveSource).History.View.Inspect(Selected, _props))
        {
            foreach (Property p in _props.Rows)
            {
                _inspect.Add(new InspectRow(p.Group, p.Name, p.Value.ToString()));
            }
        }

        _inspectVersion = Version;
        return _inspect;
    }

    /// <summary>The log, newest first: shell actions and the events, markers and findings of every source up to the cursor.</summary>
    public IReadOnlyList<LogRow> Log()
    {
        if (_logVersion == Version)
        {
            return _logCache;
        }

        var rows = new List<LogRow>();
        foreach (SourceSession s in _sources)
        {
            long limit = Math.Min(Step, s.History.EndStep);
            foreach (TrackData t in s.History.Tracks)
            {
                if (t.Info.Kind == TrackKind.Series)
                {
                    continue;
                }

                int end = UpperBound(t.Samples, limit);
                for (int i = Math.Max(0, end - 100); i < end; i++)
                {
                    TrackSample sample = t.Samples[i];
                    string who = sample.Entity.IsSome ? $" #{sample.Entity}" : string.Empty;
                    rows.Add(new LogRow(sample.Step, s.Index, t.Info.Kind == TrackKind.Event ? "event" : "marker", $"{t.Info.Name}{who}: {sample.Text}"));
                }
            }

            foreach (Finding f in s.History.Findings)
            {
                if (f.Step <= limit)
                {
                    rows.Add(new LogRow(f.Step, s.Index, "finding", $"{f.Severity} {f.Code}: {f.Text}"));
                }
            }
        }

        foreach (LogRow r in _log)
        {
            rows.Add(r);
        }

        rows.Sort(static (a, b) => b.Step != a.Step ? b.Step.CompareTo(a.Step) : string.CompareOrdinal(a.Text, b.Text));
        if (rows.Count > 200)
        {
            rows.RemoveRange(200, rows.Count - 200);
        }

        _logCache = rows;
        _logVersion = Version;
        return rows;
    }

    private List<LogRow> _logCache = [];

    private static int UpperBound(IReadOnlyList<TrackSample> samples, long step)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].Step <= step)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>Bounding boxes of draw items, used to place a view's camera.</summary>
internal static class DrawBounds
{
    /// <summary>An item bigger than this many times the median item's extent counts as scenery (a ground plane), not content.</summary>
    public const float SceneryFactor = 8;

    /// <summary>The box around the items <paramref name="accept"/> takes. False when none is accepted or none has a finite position.</summary>
    public static bool TryCompute(DrawList list, Func<DrawItem, bool> accept, out Vector3 min, out Vector3 max)
    {
        Vector3 lo = new(float.MaxValue);
        Vector3 hi = new(float.MinValue);
        bool any = false;
        foreach (ref readonly DrawItem item in list.Items)
        {
            if (accept(item) && ItemBounds(list, item, out Vector3 a, out Vector3 b))
            {
                lo = Vector3.Min(lo, a);
                hi = Vector3.Max(hi, b);
                any = true;
            }
        }

        min = lo;
        max = hi;
        return any;
    }

    /// <summary>
    /// The box around the items that are not scenery, for modules that don't tag what they draw: items much larger than the median item are left out,
    /// so a floor several times the size of everything on it doesn't set the frame.
    /// </summary>
    public static bool TryComputeWithoutScenery(DrawList list, out Vector3 min, out Vector3 max)
    {
        var sizes = new List<float>();
        foreach (ref readonly DrawItem item in list.Items)
        {
            if (ItemBounds(list, item, out Vector3 a, out Vector3 b))
            {
                sizes.Add((b - a).Length());
            }
        }

        if (sizes.Count == 0)
        {
            min = max = default;
            return false;
        }

        sizes.Sort();
        float limit = Math.Max(sizes[sizes.Count / 2], 1e-6f) * SceneryFactor;
        return TryCompute(list, item => ItemBounds(list, item, out Vector3 a, out Vector3 b) && (b - a).Length() <= limit, out min, out max);
    }

    private static bool ItemBounds(DrawList list, in DrawItem item, out Vector3 lo, out Vector3 hi)
    {
        Vector3 l = new(float.MaxValue);
        Vector3 h = new(float.MinValue);
        bool any = false;
        void Add(Vector3 p, float r = 0)
        {
            if (!float.IsFinite(p.X + p.Y + p.Z))
            {
                return;
            }

            l = Vector3.Min(l, p - new Vector3(r));
            h = Vector3.Max(h, p + new Vector3(r));
            any = true;
        }

        switch (item.Kind)
        {
            case DrawKind.Line:
            case DrawKind.Arrow:
                Add(item.A);
                Add(item.B);
                break;
            case DrawKind.Point:
            case DrawKind.Label:
                Add(item.A);
                break;
            case DrawKind.Triangle:
                Add(item.A);
                Add(item.B);
                Add(item.C);
                break;
            case DrawKind.Box:
                Add(item.A, item.Size.Length());
                break;
            case DrawKind.Sphere:
                Add(item.A, item.Size.X);
                break;
            case DrawKind.Capsule:
            case DrawKind.Cylinder:
                Add(item.A, item.Size.X);
                Add(item.B, item.Size.X);
                break;
            case DrawKind.Mesh:
                for (int i = item.First; i < item.First + item.Count; i++)
                {
                    Add(list.Vertices[list.Indices[i]]);
                }

                break;
        }

        lo = l;
        hi = h;
        return any;
    }
}
