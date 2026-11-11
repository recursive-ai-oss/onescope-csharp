using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Shared JSON writers and path helpers for the operations.</summary>
internal sealed partial class ScopeEndpoint
{
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


    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (string v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
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
