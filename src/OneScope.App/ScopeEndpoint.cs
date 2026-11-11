using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>
/// The operation set of the shell, served over the CLI, the live socket (MCP) and the window alike.
/// onescope's own operations come first; a few of onegame's (window, UI inspection, input, quit) are forwarded unchanged.
/// Operations run on the thread that owns the game, between frames.
/// </summary>
internal sealed class ScopeEndpoint : IControlEndpoint, IDisposable
{
    private static readonly string[] Forwarded =
        ["info", "ui_tree", "ui_query", "ui_click", "ui_hit", "ui_scale", "input_pointer", "input_key", "input_text", "window", "render_graph", "quit"];

    private static readonly string[] CaptureFields = ["path", "width", "height", "scale", "sha256", "renderer"];

    private readonly ShellModel _shell;
    private readonly GameInstance _game;
    private readonly ControlSurface _surface;
    private readonly Dictionary<string, OpDef> _ops = new(StringComparer.Ordinal);
    private readonly List<ControlOp> _list = [];
    private readonly List<LoadedModule> _loaded = [];

    private sealed record OpDef(string Name, string Description, string Schema, bool Changes, Func<Args, ControlResult> Run);

    public ScopeEndpoint(ShellModel shell, GameInstance game, ControlSurface surface)
    {
        _shell = shell;
        _game = game;
        _surface = surface;
        Add("open", "Open a scene, snapshot or replay file with a loaded module. Returns the new source's declarations. Optional: module (id), moduleAssembly (path to load a module first), keyframeInterval, scan.",
            """{"type":"object","properties":{"path":{"type":"string"},"module":{"type":"string"},"moduleAssembly":{"type":"string"},"keyframeInterval":{"type":"integer","minimum":1},"scan":{"type":"boolean"}},"required":["path"]}""", true, Open);
        Add("sources", "List the open sources with their step, hash, keyframes and the shared time axis.", """{"type":"object","properties":{}}""", false, Sources);
        Add("seek", "Move every source to a step (time axis), or one source to a position on another axis. Returns the step and the exact world hash.",
            """{"type":"object","properties":{"step":{"type":"integer","minimum":0},"axis":{"type":"string"},"position":{"type":"integer","minimum":0},"source":{"type":"integer"}}}""", true, Seek);
        Add("step", "Step by delta (default 1; negative goes back) along the time axis, or along another axis for one source. Returns the step and hash.",
            """{"type":"object","properties":{"delta":{"type":"integer"},"axis":{"type":"string"},"source":{"type":"integer"}}}""", true, StepOp);
        Add("play", "Set the presentation speed in steps per second (0 pauses; negative plays backwards) and optionally run frames presentation frames at 60 per second right now. Speed never changes what any step computes.",
            """{"type":"object","properties":{"speed":{"type":"number"},"frames":{"type":"integer","minimum":0,"maximum":1000000},"loop":{"type":"boolean"}}}""", true, Play);
        Add("entities", "List a source's entities at the cursor: id, parent, kind, name. Optional: source, kind, limit.",
            """{"type":"object","properties":{"source":{"type":"integer"},"kind":{"type":"string"},"limit":{"type":"integer","minimum":1}}}""", false, Entities);
        Add("inspect", "Read one entity's properties at the cursor and select it in the shell.",
            """{"type":"object","properties":{"entity":{"type":"integer"},"source":{"type":"integer"}},"required":["entity"]}""", true, Inspect);
        Add("overlay", "List the overlays of a view, or switch one on or off. Also wireframe and grid for the view.",
            """{"type":"object","properties":{"view":{"type":"integer"},"overlay":{"type":"string"},"on":{"type":"boolean"},"wireframe":{"type":"boolean"},"grid":{"type":"boolean"}}}""", true, Overlay);
        Add("axes", "List a source's axes and where each cursor is: time (the step) and the others (position inside the step).",
            """{"type":"object","properties":{"source":{"type":"integer"}}}""", false, Axes);
        Add("findings", "List what the module found while the recording played: step, code, severity, entities, text, suggested change. Optional: source, from, to, severity, limit.",
            """{"type":"object","properties":{"source":{"type":"integer"},"from":{"type":"integer"},"to":{"type":"integer"},"severity":{"type":"string"},"limit":{"type":"integer","minimum":1}}}""", false, Findings);
        Add("view", "List the scene views, or change one: add (2d or 3d), remove, source, camera (target, halfHeight, distance, yaw, pitch), frame, wireframe, grid, active.",
            """{"type":"object","properties":{"index":{"type":"integer"},"add":{"type":"string","enum":["2d","3d"]},"remove":{"type":"boolean"},"source":{"type":"integer"},"target":{"type":"array","items":{"type":"number"}},"halfHeight":{"type":"number"},"distance":{"type":"number"},"yaw":{"type":"number"},"pitch":{"type":"number"},"frame":{"type":"boolean"},"wireframe":{"type":"boolean"},"grid":{"type":"boolean"},"active":{"type":"boolean"}}}""", true, ViewOp);
        Add("layout", "Show or hide panels: entities, inspector, timeline, log.",
            """{"type":"object","properties":{"entities":{"type":"boolean"},"inspector":{"type":"boolean"},"timeline":{"type":"boolean"},"log":{"type":"boolean"}}}""", true, Layout);
        Register("capture_frame", "Capture a PNG. view: capture one scene view alone, full size (default: the whole shell). size (360p, 720p, 1080p or WxH), scale, path (relative to the output folder), presented (the window's own frame).",
            """{"type":"object","properties":{"path":{"type":"string"},"size":{"type":"string"},"scale":{"type":"number"},"view":{"type":"integer"},"presented":{"type":"boolean"}}}""", false, CaptureFrame);
        Add("capture_sequence", "Capture count PNGs into dir, stepping the cursor by stride (along axis) between frames. Writes frame-NNNN.png and manifest.json with each frame's step, world hash and sha256.",
            """{"type":"object","properties":{"dir":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":3600},"stride":{"type":"integer","minimum":1},"axis":{"type":"string"},"source":{"type":"integer"},"size":{"type":"string"},"scale":{"type":"number"},"view":{"type":"integer"}},"required":["dir","count"]}""", true, CaptureSequence);

        foreach (string name in Forwarded)
        {
            ControlOp? op = surface.Operations.FirstOrDefault(o => o.Name == name);
            if (op is not null)
            {
                _list.Add(op);
            }
        }
    }

    /// <summary>Keeps a loaded module so it is unloaded with the endpoint.</summary>
    public void Track(LoadedModule module) => _loaded.Add(module);

    /// <summary>Unloads the modules this endpoint loaded.</summary>
    public void Dispose()
    {
        foreach (LoadedModule m in _loaded)
        {
            m.Dispose();
        }

        _loaded.Clear();
    }

    /// <inheritdoc />
    public string Instructions =>
        "onescope: open a scene, snapshot or replay (open), move through time (seek, step, play), read the world (entities, inspect, axes, findings), "
        + "set up the views (view, overlay, layout) and take pictures (capture_frame, capture_sequence). Results are JSON; a step index is the only time.";

    /// <inheritdoc />
    public IReadOnlyList<ControlOp> Operations => _list;

    /// <summary>Modules loaded by <c>open</c> with <c>moduleAssembly</c>; disposed with the endpoint's owner.</summary>
    public IReadOnlyList<LoadedModule> LoadedModules => _loaded;

    /// <inheritdoc />
    public ControlResult Invoke(string op, string? argsJson = null)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (_ops.TryGetValue(op, out OpDef? def))
        {
            Args args = Args.Parse(argsJson);
            return def.Run(args);
        }

        if (Array.IndexOf(Forwarded, op) >= 0)
        {
            return _surface.Invoke(op, argsJson);
        }

        throw new ControlException("unknown_op", $"There is no operation '{op}'. Known: {string.Join(", ", _list.Select(o => o.Name))}.");
    }

    /// <inheritdoc />
    public ValueTask<ControlResult> InvokeAsync(string op, string? argsJson = null) => ValueTask.FromResult(Invoke(op, argsJson));

    /// <summary>Runs an operation and returns its JSON text.</summary>
    public string Call(string op, string? argsJson = null) => Invoke(op, argsJson).Json;

    private void Add(string name, string description, string schema, bool changes, Func<Args, string> run) =>
        Register(name, description, schema, changes, a => new ControlResult(run(a)));

    private void Register(string name, string description, string schema, bool changes, Func<Args, ControlResult> run)
    {
        _ops[name] = new OpDef(name, description, schema, changes, run);
        _list.Add(new ControlOp(name, description, schema, changes));
    }

    // ---- shared pieces -------------------------------------------------------------------------------------------------

    private SourceSession SourceArg(Args a) => _shell.Source(a.Int("source") ?? _shell.ActiveSource);

    private static void WriteSourceInfo(Utf8JsonWriter w, SourceSession s)
    {
        SourceInfo info = s.Info;
        w.WriteNumber("source", s.Index);
        w.WriteString("name", info.Name);
        w.WriteString("path", s.Path);
        w.WriteString("module", info.ModuleId);
        w.WriteNumber("dimensions", info.Dimensions);
        w.WriteStartObject("unit");
        w.WriteString("name", info.Unit.Name);
        w.WriteNumber("metres", info.Unit.Metres);
        w.WriteEndObject();
        w.WriteNumber("steps", s.History.StepCount);
        WriteAxisList(w, "axes", s);
        w.WriteStartArray("overlays");
        foreach (OverlayInfo o in info.Overlays)
        {
            w.WriteStartObject();
            w.WriteNumber("id", o.Id);
            w.WriteString("name", o.Name);
            w.WriteBoolean("defaultOn", o.DefaultOn);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("tracks");
        foreach (TrackInfo t in info.Tracks)
        {
            w.WriteStartObject();
            w.WriteString("id", t.Id);
            w.WriteString("name", t.Name);
            w.WriteString("kind", t.Kind.ToString().ToLowerInvariant());
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("pokes");
        foreach (PokeInfo p in info.Pokes)
        {
            w.WriteStartObject();
            w.WriteString("id", p.Id);
            w.WriteString("name", p.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteAxisList(Utf8JsonWriter w, string property, SourceSession s)
    {
        w.WriteStartArray(property);
        foreach (AxisInfo a in s.Info.Axes)
        {
            w.WriteStartObject();
            w.WriteString("id", a.Id);
            w.WriteString("name", a.Name);
            bool isTime = a.Id == AxisIds.Time;
            w.WriteNumber("length", isTime ? s.History.StepCount : a.PositionCount);
            w.WriteNumber("position", isTime ? s.History.CurrentStep : s.History.ViewAxis == a.Id ? s.History.ViewPosition : 0);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteCursor(Utf8JsonWriter w, SourceSession s)
    {
        w.WriteNumber("source", s.Index);
        w.WriteNumber("step", s.History.CurrentStep);
        w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
        if (s.History.ViewAxis != AxisIds.Time && s.History.ViewPosition > 0)
        {
            w.WriteString("viewAxis", s.History.ViewAxis);
            w.WriteNumber("viewPosition", s.History.ViewPosition);
            w.WriteString("viewHash", JsonOut.Hex(s.History.View.Hash()));
        }
    }

    private string Cursor() => JsonOut.Build(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("step", _shell.Step);
        w.WriteNumber("steps", _shell.MaxSteps);
        w.WriteStartArray("sources");
        foreach (SourceSession s in _shell.Sources)
        {
            w.WriteStartObject();
            WriteCursor(w, s);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    // ---- operations ----------------------------------------------------------------------------------------------------

    private string Open(Args a)
    {
        string? assembly = a.String("moduleAssembly");
        if (assembly is not null)
        {
            LoadedModule loaded = ModuleLoader.Load(assembly);
            _loaded.Add(loaded);
            _shell.AddModule(loaded.Module);
        }

        SourceSession s = _shell.Open(a.RequiredString("path"), a.String("module"), a.Int("keyframeInterval") ?? 0, a.Bool("scan"));
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            WriteSourceInfo(w, s);
            w.WriteNumber("step", s.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
            w.WriteNumber("keyframeInterval", s.KeyframeInterval);
            w.WriteNumber("scannedThrough", s.History.ScannedThrough);
            w.WriteNumber("views", _shell.Views.Count);
            w.WriteEndObject();
        });
    }

    private string Sources(Args a)
    {
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("step", _shell.Step);
            w.WriteNumber("steps", _shell.MaxSteps);
            w.WriteNumber("speed", _shell.SpeedMilliStepsPerSecond / 1000.0);
            w.WriteStartArray("sources");
            foreach (SourceSession s in _shell.Sources)
            {
                w.WriteStartObject();
                WriteSourceInfo(w, s);
                w.WriteNumber("step", s.History.CurrentStep);
                w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
                w.WriteNumber("keyframes", s.History.KeyframeCount);
                w.WriteNumber("keyframeBytes", s.History.KeyframeBytes);
                w.WriteNumber("scannedThrough", s.History.ScannedThrough);
                w.WriteNumber("findings", s.History.Findings.Count);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string Seek(Args a)
    {
        string axis = a.String("axis") ?? AxisIds.Time;
        if (axis == AxisIds.Time)
        {
            long step = a.Long("position") ?? a.Long("step") ?? throw new ControlException("bad_args", "seek needs 'step' (an integer from 0 to the step count).");
            RequireSources();
            _shell.Seek(step);
        }
        else
        {
            long position = a.Long("position") ?? throw new ControlException("bad_args", $"seek on axis '{axis}' needs 'position'.");
            RequireSources();
            _shell.SeekAxis(a.Int("source") ?? _shell.ActiveSource, axis, position);
        }

        return Cursor();
    }

    private string StepOp(Args a)
    {
        RequireSources();
        long delta = a.Long("delta") ?? 1;
        string axis = a.String("axis") ?? AxisIds.Time;
        if (axis == AxisIds.Time)
        {
            long target = Math.Clamp(_shell.Step + delta, 0, _shell.MaxSteps);
            _shell.Seek(target);
        }
        else
        {
            SourceSession s = SourceArg(a);
            if (!s.Info.Axes.Any(x => x.Id == axis))
            {
                throw new ControlException("bad_args", $"Source {s.Index} has no axis '{axis}'.");
            }

            try
            {
                s.History.Step(axis, delta);
            }
            catch (InvalidOperationException ex)
            {
                throw new ControlException("rejected", ex.Message);
            }

            _shell.Touch();
        }

        return Cursor();
    }

    private string Play(Args a)
    {
        double? speed = a.Double("speed");
        if (speed is not null)
        {
            if (Math.Abs(speed.Value) > 1e6)
            {
                throw new ControlException("bad_args", "Argument 'speed' must be between -1000000 and 1000000 steps per second.");
            }

            _shell.SetSpeed((long)Math.Round(speed.Value * 1000));
        }

        if (a.Bool("loop") is bool loop)
        {
            _shell.Loop = loop;
        }

        int frames = a.Int("frames") ?? 0;
        if (frames is < 0 or > 1_000_000)
        {
            throw new ControlException("bad_args", "Argument 'frames' must be 0 to 1000000.");
        }

        long moved = _shell.AdvancePlayback(frames);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("speed", _shell.SpeedMilliStepsPerSecond / 1000.0);
            w.WriteBoolean("playing", _shell.SpeedMilliStepsPerSecond != 0);
            w.WriteBoolean("loop", _shell.Loop);
            w.WriteNumber("frames", frames);
            w.WriteNumber("moved", moved);
            w.WriteNumber("step", _shell.Step);
            w.WriteStartArray("hashes");
            foreach (SourceSession s in _shell.Sources)
            {
                w.WriteStringValue(JsonOut.Hex(s.History.StateHash));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string Entities(Args a)
    {
        SourceSession s = SourceArg(a);
        string? kind = a.String("kind");
        int limit = Math.Clamp(a.Int("limit") ?? 1000, 1, 100_000);
        var all = new List<EntityInfo>();
        s.History.View.CollectEntities(all);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("step", s.History.CurrentStep);
            int count = 0;
            int matched = 0;
            w.WriteStartArray("entities");
            foreach (EntityInfo e in all)
            {
                if (kind is not null && e.Kind != kind)
                {
                    continue;
                }

                matched++;
                if (count >= limit)
                {
                    continue;
                }

                count++;
                w.WriteStartObject();
                w.WriteNumber("id", e.Id.Value);
                if (e.Parent.IsSome)
                {
                    w.WriteNumber("parent", e.Parent.Value);
                }
                else
                {
                    w.WriteNull("parent");
                }

                w.WriteString("kind", e.Kind);
                w.WriteString("name", e.Name);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("count", matched);
            w.WriteBoolean("truncated", matched > count);
            w.WriteEndObject();
        });
    }

    private string Inspect(Args a)
    {
        SourceSession s = SourceArg(a);
        long raw = a.RequiredLong("entity");
        if (raw <= 0)
        {
            throw new ControlException("bad_args", "Argument 'entity' must be a positive entity id.");
        }

        var id = new EntityId((ulong)raw);
        var props = new PropertyList();
        if (!s.History.View.Inspect(id, props))
        {
            throw new ControlException("not_found", $"Source {s.Index} has no entity {raw} at step {s.History.CurrentStep}.");
        }

        var all = new List<EntityInfo>();
        s.History.View.CollectEntities(all);
        EntityInfo info = all.FirstOrDefault(e => e.Id == id);
        _shell.SelectSource(s.Index);
        _shell.Select(id);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("step", s.History.CurrentStep);
            w.WriteNumber("entity", raw);
            w.WriteString("kind", info.Kind ?? string.Empty);
            w.WriteString("name", info.Name ?? string.Empty);
            w.WriteStartArray("properties");
            foreach (Property p in props.Rows)
            {
                w.WriteStartObject();
                w.WriteString("group", p.Group);
                w.WriteString("name", p.Name);
                w.WriteString("kind", p.Value.Kind.ToString().ToLowerInvariant());
                w.WritePropertyName("value");
                WriteValue(w, p.Value);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static void WriteValue(Utf8JsonWriter w, ScopeValue v)
    {
        switch (v.Kind)
        {
            case ValueKind.Bool:
                w.WriteBooleanValue(v.GetBool());
                break;
            case ValueKind.Integer:
                w.WriteNumberValue(v.GetInteger());
                break;
            case ValueKind.Number:
                double d = v.GetNumber();
                if (double.IsFinite(d))
                {
                    w.WriteNumberValue(d);
                }
                else
                {
                    w.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case ValueKind.Vector:
                Vector3 vec = v.GetVector();
                w.WriteStartArray();
                w.WriteNumberValue(vec.X);
                w.WriteNumberValue(vec.Y);
                w.WriteNumberValue(vec.Z);
                w.WriteEndArray();
                break;
            case ValueKind.Entity:
                w.WriteNumberValue(v.GetEntity().Value);
                break;
            case ValueKind.Text:
                w.WriteStringValue(v.GetText());
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    private string Overlay(Args a)
    {
        RequireSources();
        int viewIndex = a.Int("view") ?? _shell.ActiveView;
        ViewState view = _shell.View(viewIndex);
        SourceInfo info = _shell.Source(view.Source).Info;
        string? which = a.NameOrNumber("overlay");
        if (which is not null)
        {
            OverlayInfo? found = null;
            foreach (OverlayInfo o in info.Overlays)
            {
                if (string.Equals(o.Name, which, StringComparison.OrdinalIgnoreCase) || o.Id.ToString(CultureInfo.InvariantCulture) == which)
                {
                    found = o;
                    break;
                }
            }

            if (found is null)
            {
                throw new ControlException("not_found", $"Source {view.Source} has no overlay '{which}'; it declares {string.Join(", ", info.Overlays.Select(o => $"{o.Id}={o.Name}"))}.");
            }

            bool on = a.Bool("on") ?? ((view.Overlays & (1UL << found.Value.Id)) == 0);
            view.Overlays = on ? view.Overlays | (1UL << found.Value.Id) : view.Overlays & ~(1UL << found.Value.Id);
        }

        if (a.Bool("wireframe") is bool wire)
        {
            view.Wireframe = wire;
        }

        if (a.Bool("grid") is bool grid)
        {
            view.Grid = grid;
        }

        view.Touch();
        _shell.Touch();
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", viewIndex);
            w.WriteString("kind", view.Kind == ViewKind.Space3D ? "3d" : "2d");
            w.WriteBoolean("wireframe", view.Wireframe);
            w.WriteBoolean("grid", view.Grid);
            w.WriteStartArray("overlays");
            foreach (OverlayInfo o in info.Overlays)
            {
                w.WriteStartObject();
                w.WriteNumber("id", o.Id);
                w.WriteString("name", o.Name);
                w.WriteBoolean("on", (uint)o.Id < 64 && (view.Overlays & (1UL << o.Id)) != 0);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string Axes(Args a)
    {
        SourceSession s = SourceArg(a);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("step", s.History.CurrentStep);
            WriteAxisList(w, "axes", s);
            w.WriteEndObject();
        });
    }

    private string Findings(Args a)
    {
        SourceSession s = SourceArg(a);
        long from = a.Long("from") ?? 0;
        long to = a.Long("to") ?? long.MaxValue;
        string? severity = a.String("severity");
        int limit = Math.Clamp(a.Int("limit") ?? 1000, 1, 100_000);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("scannedThrough", s.History.ScannedThrough);
            int count = 0;
            int matched = 0;
            w.WriteStartArray("findings");
            foreach (Finding f in s.History.Findings)
            {
                if (f.Step < from || f.Step > to || (severity is not null && !string.Equals(f.Severity.ToString(), severity, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                matched++;
                if (count >= limit)
                {
                    continue;
                }

                count++;
                w.WriteStartObject();
                w.WriteNumber("step", f.Step);
                w.WriteString("code", f.Code);
                w.WriteString("severity", f.Severity.ToString().ToLowerInvariant());
                w.WriteStartArray("entities");
                foreach (EntityId e in f.Entities)
                {
                    w.WriteNumberValue(e.Value);
                }

                w.WriteEndArray();
                w.WriteString("text", f.Text);
                if (f.SuggestedChange is null)
                {
                    w.WriteNull("suggestedChange");
                }
                else
                {
                    w.WriteString("suggestedChange", f.SuggestedChange);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("count", matched);
            w.WriteBoolean("truncated", matched > count);
            w.WriteEndObject();
        });
    }

    private string ViewOp(Args a)
    {
        RequireSources();
        if (a.String("add") is string add)
        {
            ViewKind kind = add switch
            {
                "2d" => ViewKind.Plane2D,
                "3d" => ViewKind.Space3D,
                _ => throw new ControlException("bad_args", "Argument 'add' must be \"2d\" or \"3d\"."),
            };
            if (_shell.Views.Count >= 4)
            {
                throw new ControlException("rejected", "The shell shows at most 4 views.");
            }

            int src = a.Int("source") ?? _shell.ActiveSource;
            SourceSession s = _shell.Source(src);
            var view = new ViewState { Source = src, Kind = kind, Overlays = ShellModel.DefaultOverlays(s.Info) };
            _shell.AddView(view);
            _shell.FrameView(view);
            _shell.Touch();
        }
        else if (a.Has("index"))
        {
            int index = a.Int("index") ?? 0;
            ViewState view = _shell.View(index);
            if (a.Bool("remove") == true)
            {
                if (_shell.Views.Count == 1)
                {
                    throw new ControlException("rejected", "The last view can't be removed.");
                }

                _shell.RemoveView(index);
                _shell.ActiveView = Math.Min(_shell.ActiveView, _shell.Views.Count - 1);
            }
            else
            {
                if (a.Int("source") is int src)
                {
                    _shell.Source(src);
                    view.Source = src;
                }

                if (a.Vector("target") is Vector3 target)
                {
                    view.Target = target;
                }

                if (a.Double("halfHeight") is double hh)
                {
                    view.HalfHeight = (float)Math.Max(hh, 1e-6);
                }

                if (a.Double("distance") is double dist)
                {
                    view.Distance = (float)Math.Max(dist, 1e-6);
                }

                if (a.Double("yaw") is double yaw)
                {
                    view.Yaw = (float)yaw;
                }

                if (a.Double("pitch") is double pitch)
                {
                    view.Pitch = (float)Math.Clamp(pitch, -89, 89);
                }

                if (a.Bool("wireframe") is bool wire)
                {
                    view.Wireframe = wire;
                }

                if (a.Bool("grid") is bool grid)
                {
                    view.Grid = grid;
                }

                if (a.Bool("frame") == true)
                {
                    _shell.FrameView(view);
                }

                if (a.Bool("active") == true)
                {
                    _shell.ActiveView = index;
                }

                view.Touch();
                _shell.Touch();
            }
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("active", _shell.ActiveView);
            w.WriteStartArray("views");
            for (int i = 0; i < _shell.Views.Count; i++)
            {
                ViewState v = _shell.Views[i];
                w.WriteStartObject();
                w.WriteNumber("index", i);
                w.WriteString("kind", v.Kind == ViewKind.Space3D ? "3d" : "2d");
                w.WriteNumber("source", v.Source);
                w.WriteBoolean("wireframe", v.Wireframe);
                w.WriteBoolean("grid", v.Grid);
                w.WritePropertyName("target");
                w.WriteStartArray();
                w.WriteNumberValue(v.Target.X);
                w.WriteNumberValue(v.Target.Y);
                w.WriteNumberValue(v.Target.Z);
                w.WriteEndArray();
                if (v.Kind == ViewKind.Plane2D)
                {
                    w.WriteNumber("halfHeight", v.HalfHeight);
                }
                else
                {
                    w.WriteNumber("distance", v.Distance);
                    w.WriteNumber("yaw", v.Yaw);
                    w.WriteNumber("pitch", v.Pitch);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string Layout(Args a)
    {
        PanelState p = _shell.Panels;
        p.Entities = a.Bool("entities") ?? p.Entities;
        p.Inspector = a.Bool("inspector") ?? p.Inspector;
        p.Timeline = a.Bool("timeline") ?? p.Timeline;
        p.Log = a.Bool("log") ?? p.Log;
        _shell.Touch();
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("entities", p.Entities);
            w.WriteBoolean("inspector", p.Inspector);
            w.WriteBoolean("timeline", p.Timeline);
            w.WriteBoolean("log", p.Log);
            w.WriteEndObject();
        });
    }

    private void RequireSources()
    {
        if (_shell.Sources.Count == 0)
        {
            throw new ControlException("no_source", "No source is open; call open first.");
        }
    }

    // ---- captures ------------------------------------------------------------------------------------------------------

    private ControlResult CaptureFrame(Args a)
    {
        int? view = a.Int("view");
        if (view is int v)
        {
            _shell.View(v);
        }

        string forwarded = ForwardCaptureArgs(a, null);
        ControlResult result = CaptureOne(view, forwarded, a.Bool("presented") == true);
        // The picture travels with the JSON, so an agent sees it as an image over MCP.
        return new ControlResult(ReshapeCapture(result.Json, view), result.Png);
    }

    private ControlResult CaptureOne(int? view, string forwardedJson, bool presented)
    {
        int previousSolo = _shell.Solo;
        try
        {
            _shell.Solo = view ?? -1;
            _shell.Touch();
            if (presented)
            {
                // The window is drawn by the realtime loop; give it the changed layout before reading the swapchain back.
                _game.Redraw();
            }

            return _surface.Invoke("capture_frame", forwardedJson);
        }
        finally
        {
            _shell.Solo = previousSolo;
            _shell.Touch();
        }
    }

    private static string ForwardCaptureArgs(Args a, string? path)
    {
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            if ((path ?? a.String("path")) is string p)
            {
                w.WriteString("path", p);
            }

            if (a.String("size") is string size)
            {
                w.WriteString("size", size);
            }

            if (a.Double("scale") is double scale)
            {
                w.WriteNumber("scale", scale);
            }

            if (a.Bool("presented") == true)
            {
                w.WriteBoolean("presented", true);
            }

            w.WriteEndObject();
        });
    }

    private string ReshapeCapture(string json, int? view)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            foreach (string name in CaptureFields)
            {
                if (r.TryGetProperty(name, out JsonElement v))
                {
                    w.WritePropertyName(name);
                    v.WriteTo(w);
                }
            }

            w.WriteString("target", view is int i ? $"view/{i}" : "shell");
            w.WriteNumber("step", _shell.Step);
            w.WriteStartArray("hashes");
            foreach (SourceSession s in _shell.Sources)
            {
                w.WriteStringValue(JsonOut.Hex(s.History.StateHash));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string CaptureSequence(Args a)
    {
        string dir = a.RequiredString("dir");
        int count = a.Int("count") ?? throw new ControlException("bad_args", "Argument 'count' is required (1 to 3600).");
        if (count is < 1 or > 3600)
        {
            throw new ControlException("bad_args", "Argument 'count' must be 1 to 3600.");
        }

        long stride = a.Long("stride") ?? 1;
        if (stride < 1)
        {
            throw new ControlException("bad_args", "Argument 'stride' must be at least 1.");
        }

        string axis = a.String("axis") ?? AxisIds.Time;
        int? view = a.Int("view");
        if (view is int v)
        {
            _shell.View(v);
        }

        RequireSources();
        string outDir = ResolveInside(dir);
        Directory.CreateDirectory(outDir);
        var frames = new List<(int Index, string File, long Step, string Hash, string Sha, string Renderer, string Axis, long Position)>();
        bool ended = false;
        string renderer = string.Empty;
        for (int i = 0; i < count; i++)
        {
            string file = string.Create(CultureInfo.InvariantCulture, $"frame-{i:D4}.png");
            string rel = dir.TrimEnd('/', '\\') + "/" + file;
            ControlResult result = CaptureOne(view, ForwardCaptureArgs(a, rel), false);
            using JsonDocument doc = JsonDocument.Parse(result.Json);
            string sha = doc.RootElement.GetProperty("sha256").GetString() ?? string.Empty;
            renderer = doc.RootElement.GetProperty("renderer").GetString() ?? string.Empty;
            SourceSession s = SourceArg(a);
            frames.Add((i, file, _shell.Step, JsonOut.Hex(s.History.StateHash), sha, renderer, axis, axis == AxisIds.Time ? _shell.Step : s.History.ViewPosition));
            if (i + 1 < count)
            {
                if (axis == AxisIds.Time)
                {
                    if (_shell.Step >= _shell.MaxSteps)
                    {
                        ended = true;
                        break;
                    }

                    _shell.Seek(Math.Min(_shell.Step + stride, _shell.MaxSteps));
                }
                else
                {
                    if (!s.History.Step(axis, stride))
                    {
                        ended = true;
                        break;
                    }

                    _shell.Touch();
                }
            }
        }

        string manifest = JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("renderer", renderer);
            w.WriteString("axis", axis);
            w.WriteNumber("stride", stride);
            w.WriteBoolean("ended", ended);
            w.WriteStartArray("frames");
            foreach (var f in frames)
            {
                w.WriteStartObject();
                w.WriteNumber("frame", f.Index);
                w.WriteString("file", f.File);
                w.WriteNumber("step", f.Step);
                w.WriteNumber("position", f.Position);
                w.WriteString("hash", f.Hash);
                w.WriteString("sha256", f.Sha);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("dir", outDir);
            w.WriteNumber("frames", frames.Count);
            w.WriteBoolean("ended", ended);
            w.WriteString("manifest", Path.Combine(outDir, "manifest.json"));
            w.WriteString("renderer", renderer);
            w.WriteNumber("step", _shell.Step);
            w.WriteEndObject();
        });
    }

    private string ResolveInside(string relative)
    {
        string root = Path.GetFullPath(_surface.OutputDirectory);
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ControlException("path_outside", $"'{relative}' must be a relative folder inside the output folder.");
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ControlException("path_outside", $"'{relative}' leaves the output folder.");
        }

        return full;
    }
}
