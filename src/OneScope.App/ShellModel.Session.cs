using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

internal sealed partial class ShellModel
{
    /// <summary>The largest layout blob the shell reads from a session; the file format allows more, the shell's own layout never needs it.</summary>
    public const int MaxLayoutBytes = 1 << 20;

    private const int MaxLogRowsSaved = 200;
    private const int MaxLogTextChars = 4096;

    /// <summary>
    /// Describes everything open as a session: opened files by reference (or embedded when <paramref name="embedAll"/> or the file is gone),
    /// forks and live sources embedded, the cursor, and the layout (views, cameras, panels, selection, log).
    /// </summary>
    public SessionFile BuildSession(bool embedAll)
    {
        var file = new SessionFile { CursorStep = Step };
        foreach (SourceSession s in _sources)
        {
            if (s.Branch is { } b && s.Parent is not null)
            {
                file.Sources.Add(SessionSource.EmbedFork(s.Source, s.Parent.Index, b.ForkStep, s.KeyframeInterval));
            }
            else if (s.Live is not null || embedAll || !File.Exists(s.Path))
            {
                file.Sources.Add(SessionSource.Embed(s.Source, s.KeyframeInterval));
            }
            else
            {
                file.Sources.Add(SessionSource.Reference(s.Info.ModuleId, s.Info.Name, s.Path, File.ReadAllBytes(s.Path), s.KeyframeInterval));
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteLayout(w);
        }

        file.Layout = buffer.WrittenSpan.ToArray();
        return file;
    }

    private void WriteLayout(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("version", 1);
        w.WriteStartObject("panels");
        w.WriteBoolean("entities", Panels.Entities);
        w.WriteBoolean("inspector", Panels.Inspector);
        w.WriteBoolean("timeline", Panels.Timeline);
        w.WriteBoolean("log", Panels.Log);
        w.WriteEndObject();
        w.WriteNumber("activeSource", ActiveSource);
        w.WriteNumber("activeView", ActiveView);
        w.WriteNumber("selected", Selected.Value);
        w.WriteNumber("speed", SpeedMilliStepsPerSecond);
        w.WriteBoolean("loop", Loop);
        w.WriteStartArray("labels");
        foreach (SourceSession s in _sources)
        {
            w.WriteStringValue(s.Label);
        }

        w.WriteEndArray();
        w.WriteStartArray("views");
        foreach (ViewState v in _views)
        {
            w.WriteStartObject();
            w.WriteNumber("source", v.Source);
            w.WriteString("kind", v.Kind == ViewKind.Space3D ? "3d" : "2d");
            w.WriteNumber("overlays", v.Overlays);
            w.WriteBoolean("wireframe", v.Wireframe);
            w.WriteBoolean("grid", v.Grid);
            w.WriteStartArray("target");
            w.WriteNumberValue(v.Target.X);
            w.WriteNumberValue(v.Target.Y);
            w.WriteNumberValue(v.Target.Z);
            w.WriteEndArray();
            w.WriteNumber("halfHeight", v.HalfHeight);
            w.WriteNumber("distance", v.Distance);
            w.WriteNumber("yaw", v.Yaw);
            w.WriteNumber("pitch", v.Pitch);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("log");
        for (int i = Math.Max(0, _log.Count - MaxLogRowsSaved); i < _log.Count; i++)
        {
            LogRow row = _log[i];
            w.WriteStartObject();
            w.WriteNumber("step", row.Step);
            w.WriteNumber("source", row.Source);
            w.WriteString("kind", row.Kind);
            w.WriteString("text", row.Text.Length > MaxLogTextChars ? row.Text[..MaxLogTextChars] : row.Text);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>What a session's layout blob says, checked and ready to apply.</summary>
    private sealed class LayoutData
    {
        public PanelState Panels { get; } = new();
        public int ActiveSource { get; set; }
        public int ActiveView { get; set; }
        public ulong Selected { get; set; }
        public long Speed { get; set; }
        public bool Loop { get; set; }
        public List<string> Labels { get; } = [];
        public List<ViewState> Views { get; } = [];
        public List<LogRow> Log { get; } = [];
        public bool Present { get; set; }
    }

    private static LayoutData ParseLayout(ReadOnlyMemory<byte> blob, int sourceCount, string input)
    {
        var data = new LayoutData();
        if (blob.IsEmpty)
        {
            return data;
        }

        if (blob.Length > MaxLayoutBytes)
        {
            throw new ControlException("bad_session", $"'{input}' holds {blob.Length} bytes of layout; the shell reads at most {MaxLayoutBytes}.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(blob, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out JsonElement ver) || !ver.TryGetInt32(out int version) || version != 1)
            {
                throw new ControlException("bad_session", $"'{input}' has a layout that is not a version 1 object; expected {{\"version\":1,...}}.");
            }

            data.Present = true;
            if (root.TryGetProperty("panels", out JsonElement panels) && panels.ValueKind == JsonValueKind.Object)
            {
                data.Panels.Entities = Flag(panels, "entities", true);
                data.Panels.Inspector = Flag(panels, "inspector", true);
                data.Panels.Timeline = Flag(panels, "timeline", true);
                data.Panels.Log = Flag(panels, "log", true);
            }

            data.ActiveSource = InRange(root, "activeSource", 0, sourceCount - 1, input);
            data.Selected = root.TryGetProperty("selected", out JsonElement sel) && sel.TryGetUInt64(out ulong id) ? id : 0;
            data.Speed = root.TryGetProperty("speed", out JsonElement sp) && sp.TryGetInt64(out long speed) ? Math.Clamp(speed, -1_000_000_000, 1_000_000_000) : 0;
            data.Loop = Flag(root, "loop", false);
            if (root.TryGetProperty("labels", out JsonElement labels) && labels.ValueKind == JsonValueKind.Array)
            {
                if (labels.GetArrayLength() > sourceCount)
                {
                    throw new ControlException("bad_session", $"'{input}' layout names {labels.GetArrayLength()} labels for {sourceCount} sources.");
                }

                foreach (JsonElement l in labels.EnumerateArray())
                {
                    data.Labels.Add(l.ValueKind == JsonValueKind.String ? Truncate(l.GetString()!) : string.Empty);
                }
            }

            if (root.TryGetProperty("views", out JsonElement views) && views.ValueKind == JsonValueKind.Array)
            {
                if (views.GetArrayLength() > MaxViews)
                {
                    throw new ControlException("bad_session", $"'{input}' layout holds {views.GetArrayLength()} views; the shell shows at most {MaxViews}.");
                }

                foreach (JsonElement v in views.EnumerateArray())
                {
                    data.Views.Add(ParseView(v, sourceCount, input));
                }
            }

            data.ActiveView = data.Views.Count == 0 ? 0 : InRange(root, "activeView", 0, data.Views.Count - 1, input);
            if (root.TryGetProperty("log", out JsonElement log) && log.ValueKind == JsonValueKind.Array)
            {
                if (log.GetArrayLength() > 1000)
                {
                    throw new ControlException("bad_session", $"'{input}' layout holds {log.GetArrayLength()} log rows; at most 1000.");
                }

                foreach (JsonElement r in log.EnumerateArray())
                {
                    long step = r.TryGetProperty("step", out JsonElement st) && st.TryGetInt64(out long stp) ? stp : 0;
                    int source = r.TryGetProperty("source", out JsonElement so) && so.TryGetInt32(out int src) && src >= 0 && src < sourceCount ? src : 0;
                    string kind = r.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String ? Truncate(k.GetString()!) : "shell";
                    string text = r.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? Truncate(t.GetString()!) : string.Empty;
                    data.Log.Add(new LogRow(step, source, kind, text));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new ControlException("bad_session", $"'{input}' has a layout that is not valid JSON: {ex.Message}");
        }

        return data;
    }

    private static ViewState ParseView(JsonElement v, int sourceCount, string input)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new ControlException("bad_session", $"'{input}' layout: a view must be an object.");
        }

        var view = new ViewState
        {
            Source = InRange(v, "source", 0, sourceCount - 1, input),
            Kind = v.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String && k.GetString() == "3d" ? ViewKind.Space3D : ViewKind.Plane2D,
            Overlays = v.TryGetProperty("overlays", out JsonElement o) && o.TryGetUInt64(out ulong mask) ? mask : 0,
            Wireframe = Flag(v, "wireframe", false),
            Grid = Flag(v, "grid", true),
            HalfHeight = Finite(v, "halfHeight", 1, input, positive: true),
            Distance = Finite(v, "distance", 1, input, positive: true),
            Yaw = Finite(v, "yaw", 35, input),
            Pitch = Math.Clamp(Finite(v, "pitch", 25, input), -89, 89),
        };
        if (v.TryGetProperty("target", out JsonElement t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 3)
        {
            float[] c = new float[3];
            int i = 0;
            foreach (JsonElement e in t.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d) || !double.IsFinite(d))
                {
                    throw new ControlException("bad_session", $"'{input}' layout: a camera target must be three finite numbers.");
                }

                c[i++] = (float)d;
            }

            view.Target = new Vector3(c[0], c[1], c[2]);
        }

        return view;
    }

    private static bool Flag(JsonElement o, string name, bool fallback) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static float Finite(JsonElement o, string name, float fallback, string input, bool positive = false)
    {
        if (!o.TryGetProperty(name, out JsonElement v))
        {
            return fallback;
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d) || Math.Abs(d) > 1e12 || (positive && d <= 0))
        {
            throw new ControlException("bad_session", $"'{input}' layout: '{name}' must be a finite number{(positive ? " above 0" : string.Empty)}, not {v.GetRawText()}.");
        }

        return (float)d;
    }

    private static int InRange(JsonElement o, string name, int min, int max, string input)
    {
        if (!o.TryGetProperty(name, out JsonElement v))
        {
            return Math.Max(min, 0);
        }

        if (!v.TryGetInt32(out int n) || n < min || n > max)
        {
            throw new ControlException("bad_session", $"'{input}' layout: '{name}' must be an integer from {min} to {max}, not {v.GetRawText()}.");
        }

        return n;
    }

    private static string Truncate(string s) => s.Length > MaxLogTextChars ? s[..MaxLogTextChars] : s;

    /// <summary>
    /// Replaces everything open with a session: every source reopened with the loaded modules (referenced files checked against their saved hash),
    /// forks relinked to their parents so they can be poked and stepped again, then the views, cameras, panels, selection and log from its layout.
    /// Nothing changes when the session can't be opened.
    /// </summary>
    /// <exception cref="ControlException"><c>bad_session</c> for malformed or unusable files, naming the input and what was expected.</exception>
    public void OpenSession(SessionFile file, string input)
    {
        RestoredSession restored;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(input));
        try
        {
            restored = SessionRestore.Open(file, _modules, path =>
            {
                string full = Path.IsPathRooted(path) || dir is null ? path : Path.Combine(dir, path);
                return File.Exists(full) ? File.ReadAllBytes(full) : null;
            });
        }
        catch (Exception ex) when (ex is ScopeFormatException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ControlException("bad_session", $"'{input}' could not be opened as a session: {ex.Message}");
        }

        LayoutData layout = ParseLayout(file.Layout, restored.Sources.Count, input);
        foreach (RestoredSource r in restored.Sources)
        {
            if (r.History.StepCount <= ScanLimit)
            {
                try
                {
                    r.History.Scan();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ScopeFormatException)
                {
                    throw new ControlException("bad_session", $"'{input}' source {r.Index} ('{r.Name}') opened, but its recording did not play: {ex.Message}");
                }
            }
        }

        CloseLive();
        _sources.Clear();
        _views.Clear();
        _log.Clear();
        Selected = EntityId.None;
        ActiveView = 0;
        Solo = -1;
        foreach (RestoredSource r in restored.Sources)
        {
            SessionSource entry = file.Sources[r.Index];
            IScopeModule module = _modules.First(m => m.Id == entry.ModuleId);
            SourceSession? parent = r.ParentIndex >= 0 ? _sources[r.ParentIndex] : null;
            string path = entry.Kind == SessionSourceKind.Referenced ? Path.GetFullPath(entry.Path!) : $"{Path.GetFullPath(input)}#{r.Index}";
            var session = new SourceSession(r.Index, path, module, r.Source, r.History, entry.KeyframeInterval, parent, r.Branch)
            {
                Label = r.Index < layout.Labels.Count && layout.Labels[r.Index].Length > 0 ? layout.Labels[r.Index] : r.Name,
            };
            _sources.Add(session);
        }

        Step = Math.Clamp(restored.CursorStep, MinSteps, MaxSteps);
        foreach (SourceSession s in _sources)
        {
            s.History.Seek(Math.Clamp(Step, s.History.StartStep, s.History.EndStep));
        }

        if (layout.Present && layout.Views.Count > 0)
        {
            _views.AddRange(layout.Views);
        }
        else
        {
            foreach (SourceSession s in _sources.Where(x => !x.IsFork))
            {
                AddDefaultViews(s);
            }
        }

        ActiveSource = layout.ActiveSource;
        ActiveView = Math.Min(layout.ActiveView, Math.Max(_views.Count - 1, 0));
        Selected = new EntityId(layout.Selected);
        if (layout.Present)
        {
            Panels.Entities = layout.Panels.Entities;
            Panels.Inspector = layout.Panels.Inspector;
            Panels.Timeline = layout.Panels.Timeline;
            Panels.Log = layout.Panels.Log;
            Loop = layout.Loop;
            SetSpeed(layout.Speed);
            _log.AddRange(layout.Log);
        }
        else
        {
            AddLog(0, "shell", $"opened session {Path.GetFileName(input)} ({_sources.Count} sources)");
        }

        _logVersion = -1;
        Touch();
    }
}
