using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
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
        Add("sources", "List the open sources with their step, hash, keyframes and the shared time axis. A live source (an attached program) shows its stream state, whether the cursor follows it and its first and newest step; a source whose file recorded hashes shows whether replaying matched them.", """{"type":"object","properties":{}}""", false, Sources);
        Add("attach", "With no pid or socket, list the running programs that have a probe (pid, socket, module, library and format version). With one, connect to it: the live stream becomes a source that plays, scrubs and forks like a file, and its step numbers are the host's. Optional: dir (where to look for probes), keyframeInterval, moduleAssembly.",
            """{"type":"object","properties":{"pid":{"type":"integer"},"socket":{"type":"string"},"dir":{"type":"string"},"keyframeInterval":{"type":"integer","minimum":1},"moduleAssembly":{"type":"string"}}}""", true, Attach);
        Add("follow", "Make the shared cursor follow a live source's newest step (on) or stay where it is (off). Seeking back from the newest step turns following off by itself; seeking to the newest step turns it on. With no 'on', just reports.",
            """{"type":"object","properties":{"source":{"type":"integer"},"on":{"type":"boolean"}}}""", true, Follow);
        Add("session_save", "Save the session to a .oscope file inside the output folder: opened files by reference (checked by SHA-256 on reopening), forks and live sources embedded with their received history, the cursor, views, cameras, panels, selection and log. embed:true embeds opened files too. Never overwrites an opened file.",
            """{"type":"object","properties":{"path":{"type":"string"},"embed":{"type":"boolean"}},"required":["path"]}""", false, SessionSave);
        Add("session_open", "Replace everything open with the sources, forks (with their poke replays), cursor, views and cameras of a .oscope file. Forks can be poked and stepped again. Nothing changes if the file can't be opened. Optional: moduleAssembly.",
            """{"type":"object","properties":{"path":{"type":"string"},"moduleAssembly":{"type":"string"}},"required":["path"]}""", true, SessionOpen);
        Add("seek", "Move every source to a step (time axis), or one source to a position on another axis. Returns the step and the exact world hash.",
            """{"type":"object","properties":{"step":{"type":"integer","minimum":0},"axis":{"type":"string"},"position":{"type":"integer","minimum":0},"source":{"type":"integer"}}}""", true, Seek);
        Add("step", "Step by delta (default 1; negative goes back) along the time axis, or along another axis for one source. On a fork at the end of its recording (the source argument, default the active source), going forward runs new steps in the fork with its queued pokes. Returns the step and hash.",
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
        Add("fork", "Fork a source at a step (default: the shared cursor). The fork is a new source with its own recording: poke and step it, and the opened file is never changed. Returns the fork's source number (also its branch id), where it diverges from its parent, and the view it got.",
            """{"type":"object","properties":{"source":{"type":"integer"},"step":{"type":"integer","minimum":0},"view":{"type":"boolean"}}}""", true, ForkOp);
        Add("pick", "The entity under a window pixel, and the world point there, in a scene view (default: the view that contains the pixel). Same hit test as a pointer press. Use project for the opposite direction.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"view":{"type":"integer"}},"required":["x","y"]}""", false, PickOp);
        Add("project", "Where an entity is in a scene view, in window pixels (what to give input_pointer to press, drag or release on it). Default view: the first one that shows the source.",
            """{"type":"object","properties":{"entity":{"type":"integer"},"view":{"type":"integer"},"source":{"type":"integer"}},"required":["entity"]}""", false, ProjectOp);
        Add("poke", "Queue a poke the module declares on a fork; it takes effect on the fork's next step. args is an object by parameter name (entity ids as integers, vectors as [x,y,z]) or an array in parameter order. With no poke name, lists the declared pokes. Refused on an opened file (read_only).",
            """{"type":"object","properties":{"source":{"type":"integer"},"poke":{"type":"string"},"args":{"type":["object","array"]},"step":{"type":"boolean"}}}""", true, PokeOp);
        Add("diff", "Compare a fork with its parent (or any source of the same module) at a step: the first differing entity and field in the module's order, or none. Also reports the divergence step: the first step whose input differs.",
            """{"type":"object","properties":{"source":{"type":"integer"},"other":{"type":"integer"},"step":{"type":"integer","minimum":0}}}""", true, DiffOp);
        Add("replay_save", "Write a source's replay (a fork's recording with its pokes, or an opened file's) to a new file inside the output folder. Never overwrites an opened file.",
            """{"type":"object","properties":{"source":{"type":"integer"},"path":{"type":"string"}},"required":["path"]}""", false, ReplaySave);
        Add("view", "List the scene views, or change one: add (2d or 3d), remove, source, camera (target, halfHeight, distance, yaw, pitch), frame, wireframe, grid, active.",
            """{"type":"object","properties":{"index":{"type":"integer"},"add":{"type":"string","enum":["2d","3d"]},"remove":{"type":"boolean"},"source":{"type":"integer"},"target":{"type":"array","items":{"type":"number"}},"halfHeight":{"type":"number"},"distance":{"type":"number"},"yaw":{"type":"number"},"pitch":{"type":"number"},"frame":{"type":"boolean"},"wireframe":{"type":"boolean"},"grid":{"type":"boolean"},"active":{"type":"boolean"}}}""", true, ViewOp);
        Add("layout", "Show or hide panels: entities, inspector, timeline, log.",
            """{"type":"object","properties":{"entities":{"type":"boolean"},"inspector":{"type":"boolean"},"timeline":{"type":"boolean"},"log":{"type":"boolean"}}}""", true, Layout);
        Register("capture_frame", "Capture a PNG. view: capture one scene view alone, full size (default: the whole shell). size (360p, 720p, 1080p or WxH), scale, path (relative to the output folder), presented (the window's own frame).",
            """{"type":"object","properties":{"path":{"type":"string"},"size":{"type":"string"},"scale":{"type":"number"},"view":{"type":"integer"},"source":{"type":"integer"},"presented":{"type":"boolean"}}}""", false, CaptureFrame);
        Add("capture_sequence", "Capture count PNGs into dir, stepping the cursor by stride (along axis) between frames. Writes frame-NNNN.png and manifest.json with each frame's step, world hash and sha256.",
            """{"type":"object","properties":{"dir":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":3600},"stride":{"type":"integer","minimum":1},"axis":{"type":"string"},"source":{"type":"integer"},"size":{"type":"string"},"scale":{"type":"number"},"view":{"type":"integer"}},"required":["dir","count"]}""", true, CaptureSequence);

        ContractOps.Register(_shell, Add);

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
            _shell.PumpLive();
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

    private SourceSession SourceArg(Args a) => _shell.Source(a.Int("source") ?? a.Int("branch") ?? _shell.ActiveSource);

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
        w.WriteNumber("startStep", s.History.StartStep);
        w.WriteNumber("steps", s.History.EndStep);
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
            if (p.Role != PokeRole.None)
            {
                w.WriteString("role", p.Role == PokeRole.DragToPoint ? "drag" : p.Role.ToString().ToLowerInvariant());
            }

            w.WriteStartArray("params");
            foreach (ParamInfo q in p.Params)
            {
                w.WriteStartObject();
                w.WriteString("name", q.Name);
                w.WriteString("kind", q.Kind.ToString().ToLowerInvariant());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        ContractOps.WriteSourceExtras(w, s);
        WriteForkInfo(w, s);
    }

    /// <summary>For a fork: its parent, where it was forked, where it diverges and how much it holds. Nothing for an opened file.</summary>
    private static void WriteForkInfo(Utf8JsonWriter w, SourceSession s)
    {
        w.WriteString("label", s.Label);
        if (s.Branch is not { } b || s.Parent is null)
        {
            return;
        }

        w.WriteStartObject("fork");
        w.WriteNumber("parent", s.Parent.Index);
        w.WriteNumber("forkStep", b.ForkStep);
        if (b.DivergenceStep is long d)
        {
            w.WriteNumber("divergence", d);
        }
        else
        {
            w.WriteNull("divergence");
        }

        long poked = 0;
        for (long i = b.ForkStep; i < s.History.EndStep; i++)
        {
            if (!s.History.Recording.InputAt(i).IsEmpty)
            {
                poked++;
            }
        }

        w.WriteNumber("pokedSteps", poked);
        w.WriteNumber("pending", b.PendingPokes);
        w.WriteEndObject();
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
            w.WriteNumber("length", isTime ? s.History.EndStep : a.PositionCount);
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
        w.WriteNumber("start", _shell.MinSteps);
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

        if (a.RequiredString("path").EndsWith(".oscope", StringComparison.OrdinalIgnoreCase))
        {
            return SessionOpen(a);
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
            w.WriteNumber("start", _shell.MinSteps);
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
                WriteLive(w, s);
                WriteHashCheck(w, s);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static void WriteLive(Utf8JsonWriter w, SourceSession s)
    {
        if (s.Live is not { } live)
        {
            return;
        }

        w.WriteStartObject("live");
        w.WriteString("state", s.LiveEnded is null ? "streaming" : live.State.ToString().ToLowerInvariant());
        if (s.LiveEnded is not null)
        {
            w.WriteString("endedBecause", s.LiveEnded);
        }

        w.WriteBoolean("follow", s.Follow);
        w.WriteNumber("hostPid", live.Handshake.ProcessId);
        w.WriteString("library", live.Handshake.Info.LibraryVersion);
        w.WriteNumber("format", live.Handshake.Info.FormatVersion);
        w.WriteNumber("firstStep", s.History.StartStep);
        w.WriteNumber("newestStep", s.History.EndStep);
        w.WriteEndObject();
    }

    private static void WriteHashCheck(Utf8JsonWriter w, SourceSession s)
    {
        if (s.History.Recording.ExpectedHashes.IsEmpty)
        {
            return;
        }

        w.WriteStartObject("recordedHashes");
        w.WriteNumber("through", s.History.StartStep + s.History.Recording.ExpectedHashes.Length - 1);
        w.WriteString("matches", s.History.FirstHashMismatch is null ? (s.History.ScannedThrough >= s.History.EndStep ? "all" : "so far") : "no");
        if (s.History.FirstHashMismatch is long m)
        {
            w.WriteNumber("firstMismatch", m);
        }

        w.WriteEndObject();
    }

    private string Attach(Args a)
    {
        string? assembly = a.String("moduleAssembly");
        if (assembly is not null)
        {
            LoadedModule loaded = ModuleLoader.Load(assembly);
            _loaded.Add(loaded);
            _shell.AddModule(loaded.Module);
        }

        if (a.String("dir") is string dir)
        {
            _shell.ProbeDir = dir;
        }

        int? pid = a.Int("pid");
        string? socket = a.String("socket");
        if (pid is null && socket is null)
        {
            ProbeListing listing = _shell.ListProbes();
            return JsonOut.Build(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("probes");
                foreach (ProbeEntry e in listing.Running)
                {
                    w.WriteStartObject();
                    w.WriteNumber("pid", e.ProcessId);
                    w.WriteString("socket", e.SocketPath);
                    w.WriteString("module", e.Handshake.Info.ModuleId);
                    w.WriteString("library", e.Handshake.Info.LibraryVersion);
                    w.WriteNumber("format", e.Handshake.Info.FormatVersion);
                    w.WriteBoolean("moduleLoaded", _shell.Modules.Any(m => m.Id == e.Handshake.Info.ModuleId));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                WriteStrings(w, "removedStale", listing.Removed);
                WriteStrings(w, "unreadable", listing.Unreadable);
                w.WriteEndObject();
            });
        }

        SourceSession s = _shell.AttachLive(pid, socket, a.Int("keyframeInterval") ?? 0);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            WriteSourceInfo(w, s);
            w.WriteNumber("step", s.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
            w.WriteNumber("keyframeInterval", s.KeyframeInterval);
            WriteLive(w, s);
            w.WriteNumber("views", _shell.Views.Count);
            w.WriteEndObject();
        });
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (string v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    private string Follow(Args a)
    {
        RequireSources();
        SourceSession s = a.Int("source") is int i ? _shell.Source(i) : _shell.Sources.FirstOrDefault(x => x.Live is not null) ?? throw new ControlException("bad_args", "No source is live; attach to a program first.");
        if (a.Bool("on") is bool on)
        {
            _shell.SetFollow(s, on);
        }
        else if (s.Live is null)
        {
            throw new ControlException("bad_args", $"Source {s.Index} ('{s.Label}') is not a live source.");
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            WriteLive(w, s);
            w.WriteNumber("step", _shell.Step);
            w.WriteEndObject();
        });
    }

    private string SessionSave(Args a)
    {
        RequireSources();
        string relative = a.RequiredString("path");
        string full = ResolveInside(relative);
        foreach (SourceSession other in _shell.Sources)
        {
            if (string.Equals(Path.GetFullPath(other.Path), full, StringComparison.Ordinal))
            {
                throw new ControlException("rejected", $"'{relative}' is the file source {other.Index} was opened from; save the session to a new file.");
            }
        }

        SessionFile file = _shell.BuildSession(a.Bool("embed") ?? false);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        try
        {
            file.Write(buffer);
        }
        catch (Exception ex) when (ex is ScopeFormatException or ArgumentOutOfRangeException)
        {
            throw new ControlException("rejected", $"The session can't be saved: {ex.Message}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, buffer.WrittenSpan);
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan));
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("path", full);
            w.WriteNumber("bytes", buffer.WrittenCount);
            w.WriteString("sha256", sha);
            w.WriteNumber("cursor", file.CursorStep);
            w.WriteStartArray("sources");
            for (int i = 0; i < file.Sources.Count; i++)
            {
                SessionSource e = file.Sources[i];
                w.WriteStartObject();
                w.WriteNumber("source", i);
                w.WriteString("name", e.Name);
                w.WriteString("kind", e.Kind == SessionSourceKind.Referenced ? "referenced" : "embedded");
                w.WriteBoolean("live", _shell.Sources[i].Live is not null);
                if (e.ParentIndex >= 0)
                {
                    w.WriteNumber("parent", e.ParentIndex);
                    w.WriteNumber("forkStep", e.ForkStep);
                }

                w.WriteNumber("bytes", e.Kind == SessionSourceKind.Embedded ? e.Replay.Length : 0);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("layoutBytes", file.Layout.Length);
            w.WriteEndObject();
        });
    }

    private string SessionOpen(Args a)
    {
        string? assembly = a.String("moduleAssembly");
        if (assembly is not null)
        {
            LoadedModule loaded = ModuleLoader.Load(assembly);
            _loaded.Add(loaded);
            _shell.AddModule(loaded.Module);
        }

        string path = a.RequiredString("path");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ControlException("not_found", $"Session file '{path}' does not exist; expected a .oscope file.");
        }

        SessionFile file;
        try
        {
            file = SessionFile.Load(full, new SessionLimits(ShellModel.MaxFileBytes * 2, 256, 16 << 20));
        }
        catch (Exception ex) when (ex is ScopeFormatException or IOException or UnauthorizedAccessException)
        {
            throw new ControlException("bad_session", $"'{path}' is not a usable session file: {ex.Message}");
        }

        _shell.OpenSession(file, full);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("path", full);
            w.WriteNumber("step", _shell.Step);
            w.WriteNumber("views", _shell.Views.Count);
            w.WriteStartArray("sources");
            foreach (SourceSession s in _shell.Sources)
            {
                w.WriteStartObject();
                w.WriteNumber("source", s.Index);
                w.WriteString("label", s.Label);
                w.WriteString("module", s.Info.ModuleId);
                w.WriteNumber("startStep", s.History.StartStep);
                w.WriteNumber("steps", s.History.EndStep);
                w.WriteNumber("step", s.History.CurrentStep);
                w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
                if (s.Branch is { } b && s.Parent is not null)
                {
                    w.WriteNumber("parent", s.Parent.Index);
                    w.WriteNumber("forkStep", b.ForkStep);
                    if (b.DivergenceStep is long d)
                    {
                        w.WriteNumber("divergence", d);
                    }
                    else
                    {
                        w.WriteNull("divergence");
                    }
                }

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
            long step = a.Long("position") ?? a.Long("step") ?? throw new ControlException("bad_args", $"seek needs 'step' (an integer from {_shell.MinSteps} to {_shell.MaxSteps}).");
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
            // Stepping forward on a fork that is at the end of its recording runs new steps in it (carrying queued pokes);
            // everything else moves the shared cursor.
            if (delta > 1_000_000 || delta < -1_000_000_000)
            {
                throw new ControlException("bad_args", "Argument 'delta' is too large; step forward at most 1000000 at a time.");
            }

            _shell.StepTime(delta, SourceArg(a));
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

    private string ForkOp(Args a)
    {
        RequireSources();
        SourceSession parent = SourceArg(a);
        long step = a.Long("step") ?? Math.Clamp(_shell.Step, parent.History.StartStep, parent.History.EndStep);
        SourceSession fork = _shell.Fork(parent, step, a.Bool("view") ?? true);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("branch", fork.Index);
            WriteSourceInfo(w, fork);
            w.WriteNumber("step", fork.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(fork.History.StateHash));
            w.WriteNumber("views", _shell.Views.Count);
            w.WriteEndObject();
        });
    }

    /// <summary>The window rectangle of scene view <paramref name="index"/>; the view must be on screen, which takes a frame after it was added.</summary>
    private OneGame.UI.Rect ViewRect(int index)
    {
        _shell.View(index);
        if (_game.Ui.Find(ScopeStage.ViewId(index)) is not { IsEffectivelyVisible: true } element)
        {
            throw new ControlException("rejected", $"View {index} is not on screen; show it (view, layout) and capture a frame so it is laid out.");
        }

        return ViewPicking.Round(element.AbsoluteBounds);
    }

    private string PickOp(Args a)
    {
        RequireSources();
        double x = a.Double("x") ?? throw new ControlException("bad_args", "Argument 'x' is required (window pixels).");
        double y = a.Double("y") ?? throw new ControlException("bad_args", "Argument 'y' is required (window pixels).");
        int? given = a.Int("view");
        int index = -1;
        if (given is int g)
        {
            index = g;
        }
        else
        {
            for (int i = 0; i < _shell.Views.Count && index < 0; i++)
            {
                if (_game.Ui.Find(ScopeStage.ViewId(i)) is { IsEffectivelyVisible: true } e && ViewPicking.Round(e.AbsoluteBounds).Contains((float)x, (float)y))
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                throw new ControlException("not_found", $"No scene view is under window pixel ({x}, {y}).");
            }
        }

        ViewState view = _shell.View(index);
        ViewPicking.Hit hit = ViewPicking.Pick(_shell, view, ViewRect(index), (float)x, (float)y);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", index);
            w.WriteNumber("source", view.Source);
            if (hit.Entity.IsSome)
            {
                w.WriteNumber("entity", hit.Entity.Value);
            }
            else
            {
                w.WriteNull("entity");
            }

            w.WriteStartArray("point");
            w.WriteNumberValue(hit.Point.X);
            w.WriteNumberValue(hit.Point.Y);
            w.WriteNumberValue(hit.Point.Z);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string ProjectOp(Args a)
    {
        RequireSources();
        long raw = a.RequiredLong("entity");
        int index = a.Int("view") ?? -1;
        if (index < 0)
        {
            int wanted = SourceArg(a).Index;
            for (int i = 0; i < _shell.Views.Count && index < 0; i++)
            {
                index = _shell.Views[i].Source == wanted ? i : -1;
            }

            if (index < 0)
            {
                throw new ControlException("not_found", $"No view shows source {wanted}.");
            }
        }

        ViewState view = _shell.View(index);
        OneGame.UI.Rect rect = ViewRect(index);
        if (raw <= 0 || !ViewPicking.Project(_shell, view, rect, new EntityId((ulong)raw), out Vector2 screen, out Vector3 world))
        {
            throw new ControlException("not_found", $"View {index} (source {view.Source}) draws nothing for entity {raw} at step {_shell.Source(view.Source).History.CurrentStep}; check the entity and that its overlay is on.");
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", index);
            w.WriteNumber("entity", raw);
            w.WriteNumber("x", Math.Round(screen.X, 2));
            w.WriteNumber("y", Math.Round(screen.Y, 2));
            w.WriteBoolean("onScreen", rect.Contains(screen.X, screen.Y));
            w.WriteStartArray("world");
            w.WriteNumberValue(world.X);
            w.WriteNumberValue(world.Y);
            w.WriteNumberValue(world.Z);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private string PokeOp(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        string? name = a.String("poke");
        if (name is null)
        {
            return JsonOut.Build(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("source", s.Index);
                w.WriteBoolean("fork", s.IsFork);
                WriteSourceInfo(w, s);
                w.WriteEndObject();
            });
        }

        // An opened file refuses before anything else is looked at, so the error is the same whatever was asked.
        ShellModel.RequireFork(s);
        PokeInfo? info = null;
        foreach (PokeInfo p in s.Info.Pokes)
        {
            if (string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                info = p;
                break;
            }
        }

        if (info is not { } poke)
        {
            throw new ControlException("not_found", $"Source {s.Index} declares no poke '{name}'; its pokes are {string.Join(", ", s.Info.Pokes.Select(x => x.Id))}.");
        }

        ScopeValue[] values = ParsePokeArgs(poke, a.Element("args"));
        long before = s.History.CurrentStep;
        _shell.Poke(s, new PokeCall(poke.Id, values));
        if (a.Bool("step") == true)
        {
            _shell.StepFork(s, 1);
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteString("poke", poke.Id);
            w.WriteNumber("appliesAtStep", before);
            w.WriteNumber("pending", s.Branch!.PendingPokes);
            w.WriteNumber("step", s.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
            w.WriteEndObject();
        });
    }

    private static ScopeValue[] ParsePokeArgs(PokeInfo poke, JsonElement? args)
    {
        ParamInfo[] ps = poke.Params;
        var values = new ScopeValue[ps.Length];
        JsonElement none = default;
        JsonElement given = args ?? none;
        if (given.ValueKind == JsonValueKind.Array)
        {
            if (given.GetArrayLength() != ps.Length)
            {
                throw new ControlException("bad_args", $"Poke '{poke.Id}' takes {ps.Length} arguments ({ParamList(ps)}), got {given.GetArrayLength()}.");
            }

            int i = 0;
            foreach (JsonElement e in given.EnumerateArray())
            {
                values[i] = ToValue(poke, ps[i], e);
                i++;
            }

            return values;
        }

        if (given.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            throw new ControlException("bad_args", $"Argument 'args' must be an object by parameter name or an array in parameter order: {ParamList(ps)}.");
        }

        for (int i = 0; i < ps.Length; i++)
        {
            if (given.ValueKind != JsonValueKind.Object || !given.TryGetProperty(ps[i].Name, out JsonElement e))
            {
                throw new ControlException("bad_args", $"Poke '{poke.Id}' needs argument '{ps[i].Name}' ({ps[i].Kind.ToString().ToLowerInvariant()}); its arguments are {ParamList(ps)}.");
            }

            values[i] = ToValue(poke, ps[i], e);
        }

        if (given.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty extra in given.EnumerateObject())
            {
                if (!ps.Any(p => p.Name == extra.Name))
                {
                    throw new ControlException("bad_args", $"Poke '{poke.Id}' has no argument '{extra.Name}'; its arguments are {ParamList(ps)}.");
                }
            }
        }

        return values;
    }

    private static string ParamList(ParamInfo[] ps) => ps.Length == 0 ? "none" : string.Join(", ", ps.Select(p => $"{p.Name}: {p.Kind.ToString().ToLowerInvariant()}"));

    private static ScopeValue ToValue(PokeInfo poke, ParamInfo param, JsonElement e)
    {
        string where = $"Poke '{poke.Id}' argument '{param.Name}'";
        switch (param.Kind)
        {
            case ValueKind.Bool when e.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return ScopeValue.OfBool(e.GetBoolean());
            case ValueKind.Integer when e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long l):
                return ScopeValue.OfInteger(l);
            case ValueKind.Number when e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d) && double.IsFinite(d):
                return ScopeValue.OfNumber(d);
            case ValueKind.Text when e.ValueKind == JsonValueKind.String:
                return ScopeValue.OfText(e.GetString()!);
            case ValueKind.Entity when e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long id) && id > 0:
                return ScopeValue.OfEntity(new EntityId((ulong)id));
            case ValueKind.Vector when e.ValueKind == JsonValueKind.Array && e.GetArrayLength() is 2 or 3:
                {
                    Span<float> c = stackalloc float[3];
                    int i = 0;
                    foreach (JsonElement x in e.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out double v) || !double.IsFinite(v))
                        {
                            throw new ControlException("bad_args", $"{where} must be an array of 2 or 3 finite numbers.");
                        }

                        c[i++] = (float)v;
                    }

                    return ScopeValue.OfVector(new Vector3(c[0], c[1], c[2]));
                }

            default:
                string expected = param.Kind switch
                {
                    ValueKind.Bool => "true or false",
                    ValueKind.Integer => "an integer",
                    ValueKind.Number => "a finite number",
                    ValueKind.Text => "a string",
                    ValueKind.Entity => "a positive entity id (an integer)",
                    ValueKind.Vector => "an array of 2 or 3 numbers",
                    _ => param.Kind.ToString(),
                };
                throw new ControlException("bad_args", $"{where} must be {expected}.");
        }
    }

    private string DiffOp(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        SourceSession other = a.Int("other") is int o
            ? _shell.Source(o)
            : s.Parent ?? throw new ControlException("bad_args", $"Source {s.Index} is an opened file with no parent; name the source to compare with in 'other'.");
        long step = a.Long("step") ?? Math.Clamp(_shell.Step, Math.Max(s.History.StartStep, other.History.StartStep), Math.Min(s.History.EndStep, other.History.EndStep));
        Difference? diff = _shell.Diff(s, other, step, out ulong hashA, out ulong hashB);
        long? divergence = s.Branch is { } b && ReferenceEquals(s.Parent, other) ? b.DivergenceStep : null;
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("other", other.Index);
            w.WriteNumber("step", step);
            w.WriteBoolean("equal", diff is null);
            w.WriteString("hash", JsonOut.Hex(hashA));
            w.WriteString("otherHash", JsonOut.Hex(hashB));
            if (divergence is long d)
            {
                w.WriteNumber("divergence", d);
            }
            else
            {
                w.WriteNull("divergence");
            }

            if (diff is { } x)
            {
                w.WriteStartObject("difference");
                w.WriteNumber("entity", x.Entity.Value);
                w.WriteString("field", x.Field);
                w.WriteString("value", x.Left);
                w.WriteString("otherValue", x.Right);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("difference");
            }

            w.WriteEndObject();
        });
    }

    private string ReplaySave(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        string relative = a.RequiredString("path");
        string full = ResolveInside(relative);
        foreach (SourceSession other in _shell.Sources)
        {
            if (string.Equals(Path.GetFullPath(other.Path), full, StringComparison.Ordinal))
            {
                throw new ControlException("rejected", $"'{relative}' is the file source {other.Index} was opened from; save to a new file.");
            }
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        s.Source.WriteReplay(buffer);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, buffer.WrittenSpan);
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan));
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteString("path", full);
            w.WriteNumber("bytes", buffer.WrittenCount);
            w.WriteString("sha256", sha);
            w.WriteNumber("steps", s.History.EndStep);
            if (s.Branch?.DivergenceStep is long d)
            {
                w.WriteNumber("divergence", d);
            }
            else
            {
                w.WriteNull("divergence");
            }

            w.WriteEndObject();
        });
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
        if (view is null && (a.Has("source") || a.Has("branch")))
        {
            // Capture a source (a fork, say) by name: its first view, full size.
            int wanted = SourceArg(a).Index;
            for (int i = 0; i < _shell.Views.Count && view is null; i++)
            {
                view = _shell.Views[i].Source == wanted ? i : null;
            }

            if (view is null)
            {
                throw new ControlException("not_found", $"No view shows source {wanted}; add one with the view operation (add, then source).");
            }
        }

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
