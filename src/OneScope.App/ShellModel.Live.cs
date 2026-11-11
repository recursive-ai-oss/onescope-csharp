using System.Net.Sockets;
using OneGame.Control;
using OneScope.Attach;
using OneScope.Probe;
using OneScope.Sdk;

namespace OneScope.App;

internal sealed partial class ShellModel
{
    /// <summary>Records handed to a live history per call, so a burst from a fast host never stalls a frame; the rest wait for the next call.</summary>
    public const int MaxRecordsPerPump = 4096;

    /// <summary>The directory probes are looked for in; null for the per-user default.</summary>
    public string? ProbeDir { get; set; }

    /// <summary>Lists the running probes (and removes the sockets of programs that are gone).</summary>
    public ProbeListing ListProbes() => ProbeFinder.List(ProbeDir);

    /// <summary>
    /// Connects to a probe by its process id or its socket path, makes the stream a source and, with following on, puts the shared cursor at
    /// the host's newest step.
    /// </summary>
    /// <exception cref="ControlException"><c>not_found</c> (no such probe or nothing listening), <c>timeout</c>, <c>bad_stream</c> (not a probe, or malformed bytes),
    /// <c>no_module</c>, <c>bad_format</c> (the module rejected the snapshot).</exception>
    public SourceSession AttachLive(int? pid, string? socket, int keyframeInterval)
    {
        string path;
        if (socket is not null)
        {
            path = Path.GetFullPath(socket);
        }
        else
        {
            ProbeEntry? entry = ListProbes().Running.FirstOrDefault(e => e.ProcessId == pid);
            path = entry?.SocketPath ?? throw new ControlException("not_found", $"No running probe belongs to process {pid}; list the probes with attach and no arguments.");
        }

        var options = new ScopeHistoryOptions { KeyframeInterval = keyframeInterval > 0 ? keyframeInterval : ScopeHistoryOptions.DefaultKeyframeInterval };
        LiveSession live;
        try
        {
            ProbeHandshake hello = ProbeFinder.ReadHandshake(path);
            if (!_modules.Any(m => m.Id == hello.Info.ModuleId))
            {
                throw new ControlException("no_module", $"'{path}' streams module '{hello.Info.ModuleId}' (library {hello.Info.LibraryVersion}), and no loaded module has that id; load one with --module <assembly>.");
            }

            live = LiveSession.Connect(path, _modules, options);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProbeFormatException or ScopeFormatException or ArgumentException or UnauthorizedAccessException)
        {
            throw AttachError(path, ex);
        }

        IScopeModule module = _modules.First(m => m.Id == live.Handshake.Info.ModuleId);
        var session = new SourceSession(_sources.Count, path, module, live.History.Source, live.History, options.KeyframeInterval, live: live) { Label = live.Name };
        AddSession(session);
        AddLog(session.Index, "shell", $"attached to {live.Name} (host step {live.History.StartStep}, library {live.Handshake.Info.LibraryVersion})");
        if (session.Follow)
        {
            SeekCore(session.History.EndStep);
        }

        Touch();
        return session;
    }

    private static ControlException AttachError(string path, Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.TimedOut } => new ControlException("timeout", $"'{path}' did not answer in time; it is not a running probe, or its host is not stepping."),
        SocketException s => new ControlException("not_found", $"Nothing is listening at '{path}' ({s.SocketErrorCode}); the program may have exited. List running probes with attach and no arguments."),
        OperationCanceledException => new ControlException("timeout", $"'{path}' did not answer in time; it is not a running probe, or its host is not stepping."),
        ProbeFormatException p => new ControlException("bad_stream", $"'{path}' is not a valid probe stream: {p.Message}"),
        ScopeFormatException f => new ControlException("bad_format", $"The module could not read the stream from '{path}': {f.Message}"),
        _ => new ControlException("not_found", $"Could not connect to '{path}': {ex.Message}"),
    };

    /// <summary>Whether any source is a live connection.</summary>
    public bool HasLive => _sources.Any(s => s.Live is not null);

    /// <summary>
    /// Moves arrived records of every live source into its history, and when a source is following, moves the shared cursor to its newest step.
    /// Runs on the thread that owns the histories. Returns true when anything changed.
    /// </summary>
    public bool PumpLive()
    {
        bool changed = false;
        long follow = -1;
        foreach (SourceSession s in _sources)
        {
            if (s.Live is not { } live || s.LiveEnded is not null)
            {
                continue;
            }

            long before = s.History.EndStep;
            int n = 0;
            try
            {
                n = live.Pump(MaxRecordsPerPump);
            }
            catch (ScopeFormatException ex)
            {
                s.LiveEnded = $"the module rejected a record: {ex.Message}";
                live.Dispose();
                AddLog(s.Index, "error", $"{s.Label}: {s.LiveEnded}");
                changed = true;
            }

            if (s.History.EndStep != before)
            {
                changed = true;
                if (s.Follow)
                {
                    follow = Math.Max(follow, s.History.EndStep);
                }
            }

            if (s.LiveEnded is null && n == 0 && live.State != ClientState.Streaming)
            {
                s.LiveEnded = live.State switch
                {
                    ClientState.Ended => "the host closed its probe",
                    ClientState.Disconnected => "the host dropped this viewer or exited",
                    ClientState.Failed => $"the stream was malformed: {live.StreamError?.Message}",
                    _ => "closed",
                };
                AddLog(s.Index, "shell", $"{s.Label}: stream ended ({s.LiveEnded}) at step {s.History.EndStep}");
                changed = true;
            }

        }

        if (follow >= 0)
        {
            SeekCore(follow);
        }
        else if (changed)
        {
            Touch();
        }

        return changed;
    }

    /// <summary>Turns following on or off for a live source. Turning it on moves the cursor to the newest step.</summary>
    public void SetFollow(SourceSession source, bool on)
    {
        if (source.Live is null)
        {
            throw new ControlException("bad_args", $"Source {source.Index} ('{source.Label}') is not a live source; only an attached program can be followed.");
        }

        source.Follow = on;
        if (on)
        {
            SeekCore(source.History.EndStep);
        }
        else
        {
            Touch();
        }
    }

    /// <summary>Closes every live connection; the histories keep what was received.</summary>
    public void CloseLive()
    {
        foreach (SourceSession s in _sources)
        {
            s.Live?.Dispose();
        }
    }
}
