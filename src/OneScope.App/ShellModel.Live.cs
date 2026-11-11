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

    /// <summary>The default for how long an attach waits for a host's snapshot before giving up: a host that steps rarely still gets there.</summary>
    public const int DefaultAttachTimeoutMilliseconds = 30_000;

    private readonly List<PendingAttach> _attaches = [];
    private int _nextAttach = 1;
    private bool _liveClosed;

    /// <summary>Attaches that are connecting or have failed (the last few), oldest first. A finished attach becomes a source and leaves the list.</summary>
    public IReadOnlyList<PendingAttach> Attaches => _attaches;

    /// <summary>
    /// Starts connecting to a probe by its process id or its socket path on a worker thread and returns at once, so the window keeps drawing while the
    /// host takes its snapshot. <see cref="PumpLive"/> turns a finished connection into a source; <see cref="PendingAttach.Task"/> completes when it is ready.
    /// </summary>
    public PendingAttach BeginAttach(int? pid, string? socket, int keyframeInterval, long maxRecordBytes, int timeoutMilliseconds)
    {
        var options = new ScopeHistoryOptions { KeyframeInterval = keyframeInterval > 0 ? keyframeInterval : ScopeHistoryOptions.DefaultKeyframeInterval };
        IScopeModule[] modules = [.. _modules];
        string? dir = ProbeDir;
        string target = socket is not null ? Path.GetFullPath(socket) : $"pid {pid}";
        Task<LiveSession> task = Task.Run(() => ConnectCore(pid, socket is null ? null : Path.GetFullPath(socket), dir, modules, options, maxRecordBytes, timeoutMilliseconds));
        var pending = new PendingAttach(_nextAttach++, target, options.KeyframeInterval, task);
        _attaches.Add(pending);
        while (_attaches.Count(x => x.State == "failed") > 8)
        {
            _attaches.Remove(_attaches.First(x => x.State == "failed"));
        }

        Touch();
        return pending;
    }

    // Runs on a worker thread: touches no shell state.
    private static LiveSession ConnectCore(int? pid, string? socketPath, string? dir, IScopeModule[] modules, ScopeHistoryOptions options, long maxRecordBytes, int timeoutMilliseconds)
    {
        string path;
        if (socketPath is not null)
        {
            path = socketPath;
        }
        else
        {
            ProbeEntry? entry = ProbeFinder.List(dir).Running.FirstOrDefault(e => e.ProcessId == pid);
            path = entry?.SocketPath ?? throw new ControlException("not_found", $"No running probe belongs to process {pid}; list the probes with attach and no arguments.");
        }

        try
        {
            ProbeHandshake hello = ProbeFinder.ReadHandshake(path);
            if (!modules.Any(m => m.Id == hello.Info.ModuleId))
            {
                throw new ControlException("no_module", $"'{path}' streams module '{hello.Info.ModuleId}' (library {hello.Info.LibraryVersion}), and no loaded module has that id; load one with --module <assembly>.");
            }

            return LiveSession.Connect(path, modules, options, null, maxRecordBytes, timeoutMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ProbeFormatException or ScopeFormatException or ArgumentException or UnauthorizedAccessException)
        {
            throw AttachError(path, ex);
        }
    }

    /// <summary>
    /// Turns attaches whose connection has finished into sources (or into a failed entry with its error, also written to the event log).
    /// Runs on the thread that owns the histories. Returns true when anything changed.
    /// </summary>
    public bool CompleteAttaches()
    {
        bool changed = false;
        foreach (PendingAttach p in _attaches.ToArray())
        {
            if (p.State != "connecting" || !p.Task.IsCompleted)
            {
                continue;
            }

            changed = true;
            if (p.Task.IsCompletedSuccessfully)
            {
                if (_liveClosed)
                {
                    p.Task.Result.Dispose();
                    p.State = "failed";
                    p.ErrorCode = "closed";
                    p.Error = "the shell closed before the attach finished";
                    continue;
                }

                LiveSession live = p.Task.Result;
                IScopeModule module = _modules.First(m => m.Id == live.Handshake.Info.ModuleId);
                var session = new SourceSession(_sources.Count, p.Target, module, live.History.Source, live.History, p.KeyframeInterval, live: live) { Label = live.Name };
                AddSession(session);
                AddLog(session.Index, "shell", $"attached to {live.Name} (host step {live.History.StartStep}, library {live.Handshake.Info.LibraryVersion})");
                if (session.Follow)
                {
                    SeekCore(session.ToShared(session.History.EndStep));
                }

                p.State = "attached";
                p.Source = session;
                _attaches.Remove(p);
            }
            else
            {
                Exception ex = p.Task.Exception!.GetBaseException();
                p.State = "failed";
                p.ErrorCode = ex is ControlException c ? c.Code : "not_found";
                p.Error = ex.Message;
                AddLog(Math.Min(ActiveSource, Math.Max(_sources.Count - 1, 0)), "error", $"attach to {p.Target} failed ({p.ErrorCode}): {p.Error}");
            }
        }

        if (changed)
        {
            Touch();
        }

        return changed;
    }

    /// <summary>Forgets a failed attach after its error was handed to the caller.</summary>
    public void Dismiss(PendingAttach attach) => _attaches.Remove(attach);

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
        bool changed = CompleteAttaches();
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
                    follow = Math.Max(follow, s.ToShared(s.History.EndStep));
                }
            }

            if (live.Capped && s.LiveEnded is null)
            {
                s.LiveCapped = true;
                s.LiveEnded = $"the viewer reached its live cap of {live.MaxRecordBytes} bytes at step {s.History.EndStep}; the program is not affected, and what was received stays usable";
                AddLog(s.Index, "error", $"{s.Label}: {s.LiveEnded}");
                changed = true;
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
            SeekCore(source.ToShared(source.History.EndStep));
        }
        else
        {
            Touch();
        }
    }

    /// <summary>Closes every live connection; the histories keep what was received.</summary>
    public void CloseLive()
    {
        _liveClosed = true;
        foreach (PendingAttach p in _attaches)
        {
            if (p.State == "connecting")
            {
                // A connection that finishes after the shell is gone must not leave a reader thread behind.
                _ = p.Task.ContinueWith(static t => t.Result.Dispose(), System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
        }

        foreach (SourceSession s in _sources)
        {
            s.Live?.Dispose();
        }
    }
}

/// <summary>An attach in progress or failed: what was asked, where it stands and why it failed.</summary>
internal sealed class PendingAttach(int id, string target, int keyframeInterval, Task<LiveSession> task)
{
    public int Id { get; } = id;

    /// <summary>The socket path, or "pid N" until the pid is resolved.</summary>
    public string Target { get; } = target;

    public int KeyframeInterval { get; } = keyframeInterval;

    public Task<LiveSession> Task { get; } = task;

    /// <summary><c>connecting</c>, <c>failed</c> or <c>attached</c>.</summary>
    public string State { get; set; } = "connecting";

    public string? Error { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>The source it became, once attached.</summary>
    public SourceSession? Source { get; set; }

    /// <summary>The onegame frame number when the attach began; with the current frame it shows the window kept presenting while the attach waited.</summary>
    public long StartFrame { get; set; }
}
