using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Operations that open, list and attach sources, and save or restore sessions.</summary>
internal sealed partial class ScopeEndpoint
{
    private string Open(Args a)
    {
        if (a.RequiredString("path").EndsWith(".oscope", StringComparison.OrdinalIgnoreCase))
        {
            return SessionOpen(a);
        }

        // A module named here is the one that opens this file, even when another loaded module would also accept it:
        // that is how one file is played through two versions of a library, each in its own load context.
        LoadedModule? from = null;
        string? assembly = a.String("moduleAssembly");
        if (assembly is not null)
        {
            from = ModuleLoader.Load(assembly);
            _loaded.Add(from);
            _shell.AddModule(from.Module);
        }

        SourceSession s = _shell.Open(a.RequiredString("path"), a.String("module"), a.Int("keyframeInterval") ?? 0, a.Bool("scan"), from);
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
            w.WriteStartArray("attaching");
            foreach (PendingAttach p in _shell.Attaches)
            {
                w.WriteStartObject();
                WritePending(w, p);
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
        w.WriteString("state", s.LiveEnded is null ? "streaming" : s.LiveCapped ? "capped" : live.State.ToString().ToLowerInvariant());
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
        w.WriteNumber("recordBytes", live.RecordBytes);
        w.WriteNumber("capBytes", live.MaxRecordBytes);
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

        int wait = a.Int("wait") ?? 100;
        int timeout = a.Int("timeout") ?? ShellModel.DefaultAttachTimeoutMilliseconds;
        long cap = a.Long("capBytes") ?? LiveSession.DefaultMaxRecordBytes;
        if (wait is < 0 or > 120_000 || timeout is < 100 or > 600_000 || cap < 1024)
        {
            throw new ControlException("bad_args", "Arguments must satisfy: wait 0 to 120000 ms, timeout 100 to 600000 ms, capBytes at least 1024.");
        }

        PendingAttach pending = _shell.BeginAttach(pid, socket, a.Int("keyframeInterval") ?? 0, cap, timeout);
        pending.StartFrame = _game.Frame;

        // The shell thread waits only as long as the caller allows, so a window keeps drawing while a slow host takes its snapshot.
        if (wait > 0)
        {
            try
            {
                pending.Task.Wait(wait);
            }
            catch (AggregateException)
            {
                // A failed connection is reported below by CompleteAttaches, with its typed error.
            }
        }

        _shell.CompleteAttaches();
        if (pending.State == "failed")
        {
            _shell.Dismiss(pending);
            throw new ControlException(pending.ErrorCode ?? "not_found", pending.Error ?? "The attach failed.");
        }

        if (pending.Source is not { } s)
        {
            return JsonOut.Build(w =>
            {
                w.WriteStartObject();
                WritePending(w, pending);
                w.WriteEndObject();
            });
        }

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

    /// <summary>An attach that has not become a source: still connecting (the host has not recorded a step yet) or failed.</summary>
    private void WritePending(Utf8JsonWriter w, PendingAttach p)
    {
        w.WriteNumber("attach", p.Id);
        w.WriteString("state", p.State);
        w.WriteString("target", p.Target);
        w.WriteNumber("startFrame", p.StartFrame);
        w.WriteNumber("frame", _game.Frame);
        if (p.Error is not null)
        {
            w.WriteString("errorCode", p.ErrorCode);
            w.WriteString("error", p.Error);
        }
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
}
