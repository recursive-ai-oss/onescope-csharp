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
internal sealed partial class ScopeEndpoint : IControlEndpoint, IDisposable
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
        Add("attach", "With no pid or socket, list the running programs that have a probe (pid, socket, module, library and format version). With one, connect to it: the live stream becomes a source that plays, scrubs and forks like a file, and its step numbers are the host's. Optional: dir (where to look for probes), keyframeInterval, moduleAssembly. The connection runs in the background: the call waits at most wait milliseconds (default 100) and then returns {attach, state:'connecting'} while the host takes its snapshot at its next step; sources lists it under attaching until it becomes a source (or fails, with its error). timeout (default 30000 ms) is how long the connection may take, capBytes (default 268435456) how many received record bytes the viewer keeps before it stops with state capped.",
            """{"type":"object","properties":{"pid":{"type":"integer"},"socket":{"type":"string"},"dir":{"type":"string"},"keyframeInterval":{"type":"integer","minimum":1},"moduleAssembly":{"type":"string"},"wait":{"type":"integer","minimum":0,"maximum":120000},"timeout":{"type":"integer","minimum":100,"maximum":600000},"capBytes":{"type":"integer","minimum":1024}}}""", true, Attach);
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
        Add("view", "List the scene views, or change one: add (2d or 3d), remove, source, camera (target, halfHeight, distance, yaw, pitch), frame (the moving bodies, not static or sensor extents), frameSelection (the selected entity), wireframe, grid, active.",
            """{"type":"object","properties":{"frameSelection":{"type":"boolean"},"index":{"type":"integer"},"add":{"type":"string","enum":["2d","3d"]},"remove":{"type":"boolean"},"source":{"type":"integer"},"target":{"type":"array","items":{"type":"number"}},"halfHeight":{"type":"number"},"distance":{"type":"number"},"yaw":{"type":"number"},"pitch":{"type":"number"},"frame":{"type":"boolean"},"wireframe":{"type":"boolean"},"grid":{"type":"boolean"},"active":{"type":"boolean"}}}""", true, ViewOp);
        Add("timeline", "Show or change the part of the time axis the timeline spans. fit: \"all\" (the whole axis, following it as it grows) or \"fork\" (the fork given by source, default the active source or its latest fork); from and to: show those steps; zoom: a factor above 1 shows fewer steps (around center, default the cursor); pan: slide the window by that many steps. With nothing, reports the window. Only the view changes, never a world.",
            """{"type":"object","properties":{"fit":{"type":"string","enum":["all","fork"]},"source":{"type":"integer"},"from":{"type":"integer"},"to":{"type":"integer"},"zoom":{"type":"number","exclusiveMinimum":0},"center":{"type":"integer"},"pan":{"type":"integer"}}}""", true, TimelineOp);
        Add("layout", "Show or hide panels: entities, inspector, timeline, log.",
            """{"type":"object","properties":{"entities":{"type":"boolean"},"inspector":{"type":"boolean"},"timeline":{"type":"boolean"},"log":{"type":"boolean"}}}""", true, Layout);
        Register("capture_frame", "Capture a PNG. view: capture one scene view alone, full size (default: the whole shell). size (360p, 720p, 1080p or WxH), scale, path (relative to the output folder), presented (the window's own frame).",
            """{"type":"object","properties":{"path":{"type":"string"},"size":{"type":"string"},"scale":{"type":"number"},"view":{"type":"integer"},"source":{"type":"integer"},"presented":{"type":"boolean"}}}""", false, CaptureFrame);
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

}
