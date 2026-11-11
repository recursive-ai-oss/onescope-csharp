using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneGame.Live;
using OneGame.Platform;
using OneScope.Attach;

namespace OneScope.App;

/// <summary>The <c>onescope</c> command.</summary>
internal static class Program
{
    private const string Help = """
        onescope: watch a deterministic world. Open a scene, snapshot or replay, scrub its history, inspect it, capture it.

        Start the shell
          onescope open <file> [options]       open <file> in the shell and run until it is closed
            --module <dll>                     load a module assembly (repeatable); needed for any file format a module owns
            --headless                         no window: render offscreen (captures work the same)
            --live [path]                      serve the operations over a Unix socket (MCP, one JSON-RPC message per line)
            --run "<op> [json]"                run an operation after opening (repeatable); each result is printed as JSON
            --size WxH                         window or capture size (default 1280x720)
            --backend <name> --display <name>  window system and display (x11 or wayland), as in onegame
            --artifacts <dir>                  where captures go (default ./artifacts)
            --keyframe <n>                     keyframe interval in steps (default 64)
            --no-wait                          with --run: exit after the operations instead of staying open (headless only)

        Watch a running program (one that called the probe)
          onescope attach                      list running programs with a probe, as JSON
          onescope attach <pid|socket> [options]   open the shell on its live stream (same options as open; --dir <probe dir>)
          onescope open <session.oscope>       reopen a saved session (sources, forks with their pokes, views, cameras)

        Drive a running shell (same JSON as the operations)
          onescope <operation> [json | --name value ...] --socket <path>
          onescope call <operation> [json] --socket <path>
          onescope mcp --socket <path>         stdio MCP bridge for agents

        Operations: open, attach, follow, session_save, session_open, sources, seek, step, play, entities, inspect, overlay, axes, findings, fork,
                    poke, diff, compare, replay_save, pick, project, view, layout, capture_frame, capture_sequence, plus info, ui_tree, ui_query,
                    ui_click, window, quit.
        Results are JSON. Errors are {"ok":false,"error":{"code","message"}} and exit code 1.
        Exit codes: 0 ok, 1 an operation failed, 2 usage, 4 could not run.
        """;

    private static readonly string[] KnownOps =
        ["open", "attach", "follow", "session_save", "session_open", "sources", "seek", "step", "play", "entities", "inspect", "overlay", "axes", "findings", "fork", "pick", "project", "poke", "diff", "compare", "replay_save", "view", "layout", "capture_frame", "capture_sequence", "info", "ui_tree", "ui_query", "ui_click", "ui_hit", "ui_scale", "input_pointer", "input_key", "input_text", "window", "render_graph", "quit"];

    internal static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            output.WriteLine(Help);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var opts = Options.Parse(args);
            if (opts.Socket is not null || opts.Verb == "mcp" || opts.Verb == "call")
            {
                return opts.Verb == "mcp" ? Bridge(opts, output, error) : CallRemote(opts, output, error);
            }

            if (opts.Verb == "attach" && opts.Positional.Count == 0)
            {
                output.WriteLine(ListProbes(opts.Value("dir")));
                return 0;
            }

            if (opts.Verb is "open" or "attach")
            {
                return RunShell(opts, output, error);
            }

            error.WriteLine(Fail("usage", $"Unknown command '{opts.Verb}'. Run onescope --help."));
            return 2;
        }
        catch (ControlException ex)
        {
            output.WriteLine(Fail(ex.Code, ex.Message));
            return ex.Code == "usage" ? 2 : 1;
        }
    }

    private static string Fail(string code, string message) =>
        JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", false);
            w.WriteStartObject("error");
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    // ---- the shell ----------------------------------------------------------------------------------------------------

    private static int RunShell(Options o, TextWriter output, TextWriter error)
    {
        bool attach = o.Verb == "attach";
        if (o.Positional.Count != 1)
        {
            throw new ControlException("usage", attach ? "attach takes one process id or socket path: onescope attach <pid|socket> [options]; with none it lists the running probes." : "open takes one file: onescope open <file> [options].");
        }

        (int width, int height) = ParseSize(o.Value("size") ?? "1280x720");
        string artifacts = Path.GetFullPath(o.Value("artifacts") ?? "artifacts");
        bool headless = o.Flag("headless");
        var shell = new ShellModel();
        shell.AddBundledModules();
        var game = new ScopeGame(shell);
        PlatformBackendKind backend = default;
        if (o.Value("backend") is string b && !Enum.TryParse(b, ignoreCase: true, out backend))
        {
            throw new ControlException("usage", $"--backend must be one of {string.Join(", ", Enum.GetNames<PlatformBackendKind>())}, not '{b}'.");
        }

        using GameInstance instance = GameInstance.Create(game, new GameOptions { Headless = headless, Width = width, Height = height, Backend = backend, DisplayName = o.Value("display") });
        var surface = new ControlSurface(instance, artifacts);
        (instance.Stage as ScopeStage)?.Attach();
        var endpoint = new ScopeEndpoint(shell, instance, surface);
        try
        {
            foreach (string module in o.Values("module"))
            {
                LoadedModule loaded = ModuleLoader.Load(module);
                shell.AddModule(loaded.Module);
                endpoint.Track(loaded);
            }

            shell.ProbeDir = o.Value("dir");
            int exit = 0;
            string openArgs = JsonOut.Build(w =>
            {
                w.WriteStartObject();
                if (attach && int.TryParse(o.Positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                {
                    w.WriteNumber("pid", pid);
                }
                else
                {
                    w.WriteString(attach ? "socket" : "path", o.Positional[0]);
                }

                if (attach)
                {
                    // Nothing is drawing yet at start-up, so the command line waits for the host's snapshot instead of returning a pending state.
                    w.WriteNumber("wait", ShellModel.DefaultAttachTimeoutMilliseconds);
                }

                if (o.Value("keyframe") is string k && int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out int interval))
                {
                    w.WriteNumber("keyframeInterval", interval);
                }

                w.WriteEndObject();
            });
            exit = RunOp(endpoint, attach ? "attach" : "open", openArgs, output);
            foreach (string run in o.Values("run"))
            {
                if (exit != 0)
                {
                    break;
                }

                string trimmed = run.Trim();
                int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
                string op = space < 0 ? trimmed : trimmed[..space];
                string? json = space < 0 ? null : trimmed[(space + 1)..];
                exit = RunOp(endpoint, op, json, output);
            }

            if (exit != 0)
            {
                return exit;
            }

            string? live = o.LiveValue;
            bool serve = live is not null || (!headless && !o.Flag("no-wait"));
            if (!serve)
            {
                return 0;
            }

            using var server = new LiveServer(endpoint);
            if (live is not null)
            {
                string path = live.Length > 0 ? live : LiveServer.DefaultPath();
                server.Start(path);
                error.WriteLine($"onescope: serving operations on {path}");
            }

            if (headless)
            {
                while (!instance.QuitRequested)
                {
                    server.Pump();
                    shell.PumpLive();
                    server.WaitForWork(10);
                }
            }
            else
            {
                instance.RunRealtime(0, () =>
                {
                    server.Pump();
                    shell.PumpLive();
                    shell.AdvancePlayback(1);
                });
            }

            return 0;
        }
        finally
        {
            shell.CloseLive();
            endpoint.Dispose();
        }
    }

    /// <summary>Lists running probes as JSON without starting a shell: the same result the attach operation gives with no arguments.</summary>
    private static string ListProbes(string? dir)
    {
        ProbeListing listing = ProbeFinder.List(dir);
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
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("removedStale");
            foreach (string r in listing.Removed)
            {
                w.WriteStringValue(r);
            }

            w.WriteEndArray();
            w.WriteStartArray("unreadable");
            foreach (string u in listing.Unreadable)
            {
                w.WriteStringValue(u);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static int RunOp(ScopeEndpoint endpoint, string op, string? json, TextWriter output)
    {
        try
        {
            output.WriteLine(endpoint.Call(op, json));
            return 0;
        }
        catch (ControlException ex)
        {
            output.WriteLine(Fail(ex.Code, ex.Message));
            return 1;
        }
    }

    private static (int Width, int Height) ParseSize(string text)
    {
        string[] parts = text.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h) || w is < 64 or > 8192 || h is < 64 or > 8192)
        {
            throw new ControlException("usage", $"--size must be WxH between 64x64 and 8192x8192, not '{text}'.");
        }

        return (w, h);
    }

    // ---- talking to a running shell -----------------------------------------------------------------------------------

    private static int CallRemote(Options o, TextWriter output, TextWriter error)
    {
        string socket = o.Socket ?? throw new ControlException("usage", "This command needs --socket <path> of a running onescope.");
        string op = o.Verb == "call" ? (o.Positional.Count > 0 ? o.Positional[0] : throw new ControlException("usage", "call needs an operation name.")) : o.Verb;
        List<string> rest = o.Verb == "call" ? o.Positional.Skip(1).ToList() : o.Positional;
        if (o.Verb != "call" && Array.IndexOf(KnownOps, op) < 0)
        {
            throw new ControlException("usage", $"Unknown command '{op}'. Run onescope --help.");
        }

        string args = rest.Count > 0 && rest[0].TrimStart().StartsWith('{') ? rest[0] : o.ArgsAsJson();
        using Socket s = Connect(socket);
        using var stream = new NetworkStream(s);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        writer.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"onescope-cli","version":"0"}}}""");
        reader.ReadLine();
        string request = JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", 2);
            w.WriteString("method", "tools/call");
            w.WriteStartObject("params");
            w.WriteString("name", op);
            w.WritePropertyName("arguments");
            using JsonDocument doc = JsonDocument.Parse(args);
            doc.RootElement.WriteTo(w);
            w.WriteEndObject();
            w.WriteEndObject();
        });
        writer.WriteLine(request);
        string? line = reader.ReadLine() ?? throw new ControlException("not_found", $"The shell at {socket} closed the connection without answering.");
        using JsonDocument reply = JsonDocument.Parse(line);
        JsonElement root = reply.RootElement;
        if (root.TryGetProperty("error", out JsonElement rpcError))
        {
            output.WriteLine(Fail("rpc_error", rpcError.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "error" : "error"));
            return 1;
        }

        JsonElement result = root.GetProperty("result");
        string text = string.Empty;
        foreach (JsonElement part in result.GetProperty("content").EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement t))
            {
                text = t.GetString() ?? string.Empty;
                break;
            }
        }

        bool isError = result.TryGetProperty("isError", out JsonElement ie) && ie.ValueKind == JsonValueKind.True;
        if (isError)
        {
            output.WriteLine(text.Contains("\"error\"", StringComparison.Ordinal) && text.Contains("\"ok\"", StringComparison.Ordinal) ? text : WrapError(text));
            return 1;
        }

        output.WriteLine(text);
        return 0;
    }

    private static string WrapError(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out JsonElement e))
            {
                return JsonOut.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("ok", false);
                    w.WritePropertyName("error");
                    e.WriteTo(w);
                    w.WriteEndObject();
                });
            }
        }
        catch (JsonException)
        {
        }

        return Fail("error", text);
    }

    private static Socket Connect(string path)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            s.Connect(new UnixDomainSocketEndPoint(path));
            return s;
        }
        catch (SocketException ex)
        {
            s.Dispose();
            throw new ControlException("not_found", $"No onescope is listening on {path}: {ex.Message}");
        }
    }

    private static int Bridge(Options o, TextWriter output, TextWriter error)
    {
        string socket = o.Socket ?? throw new ControlException("usage", "mcp needs --socket <path> of a running onescope.");
        using Socket s = Connect(socket);
        using var stream = new NetworkStream(s);
        var pump = Task.Run(() =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                output.WriteLine(line);
                output.Flush();
            }
        });
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string? input;
        while ((input = Console.In.ReadLine()) is not null)
        {
            writer.WriteLine(input);
        }

        s.Shutdown(SocketShutdown.Send);
        pump.Wait(TimeSpan.FromSeconds(2));
        error.Flush();
        return 0;
    }
}

/// <summary>The command line: a verb, positional words and <c>--name value</c> options.</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["headless", "no-wait", "help"];
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public string Verb { get; private init; } = string.Empty;

    public List<string> Positional { get; } = [];

    public string? Socket => Value("socket");

    /// <summary>Null when <c>--live</c> is absent; empty when it has no path.</summary>
    public string? LiveValue { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options { Verb = args[0] };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                o.Positional.Add(a);
                continue;
            }

            string name = a[2..];
            if (name == "live")
            {
                o.LiveValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : string.Empty;
                continue;
            }

            if (Flags.Contains(name))
            {
                o.Add(name, "true");
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ControlException("usage", $"Option --{name} needs a value.");
            }

            o.Add(name, args[++i]);
        }

        return o;
    }

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out List<string>? list))
        {
            _values[name] = list = [];
        }

        list.Add(value);
    }

    public string? Value(string name) => _values.TryGetValue(name, out List<string>? l) ? l[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out List<string>? l) ? l : [];

    public bool Flag(string name) => _values.ContainsKey(name);

    /// <summary>The <c>--name value</c> options (other than the command's own) as a JSON object, with numbers and booleans typed.</summary>
    public string ArgsAsJson() => JsonOut.Build(w =>
    {
        w.WriteStartObject();
        foreach ((string key, List<string> values) in _values)
        {
            if (key == "socket")
            {
                continue;
            }

            string v = values[^1];
            w.WritePropertyName(key);
            if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            {
                w.WriteNumberValue(l);
            }
            else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
            {
                w.WriteNumberValue(d);
            }
            else if (v is "true" or "false")
            {
                w.WriteBooleanValue(v == "true");
            }
            else
            {
                w.WriteStringValue(v);
            }
        }

        w.WriteEndObject();
    });
}
