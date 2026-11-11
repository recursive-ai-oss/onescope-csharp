using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Capture operations: capture_frame and capture_sequence.</summary>
internal sealed partial class ScopeEndpoint
{
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
}
