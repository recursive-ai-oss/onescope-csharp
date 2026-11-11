using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Registers an operation with the endpoint.</summary>
internal delegate void AddOperation(string name, string description, string schema, bool changes, Func<Args, string> run);

/// <summary>
/// The operations for fields, links, shared-entity diffs and ghost sources: <c>field</c>, <c>links</c>, <c>selection</c>, <c>track</c>,
/// <c>ghost</c>, <c>open_real</c> and <c>diff_shared</c>. They are in their own class so the endpoint only registers them.
/// </summary>
internal sealed class ContractOps(ShellModel shell)
{
    /// <summary>Registers the operations on an endpoint.</summary>
    public static void Register(ShellModel shell, AddOperation add)
    {
        var ops = new ContractOps(shell);
        add("field", "Read a scalar field of a source at the cursor, at an entity (entity) or a point in the source's length unit (point: [x, y, z]), by field id. With no field, list the source's declared fields: id, name, colour map, range, unit. The value is the module's own, the same the views paint. Optional: source.",
            """{"type":"object","properties":{"source":{"type":"integer"},"field":{"type":"string"},"entity":{"type":"integer"},"point":{"type":"array","items":{"type":"number"}}}}""", false, ops.Field);
        add("links", "List the transfers sources declare between themselves (an entity that continues an entity of another source from a step on), and whether the cursor has crossed each.",
            """{"type":"object","properties":{}}""", false, _ => ops.Links());
        add("selection", "Report the selected entity: its source, id, name and properties at the cursor. The selection moves across a link by itself when the cursor crosses a transfer.",
            """{"type":"object","properties":{}}""", false, _ => ops.Selection());
        add("track", "Make a view's camera follow the selected entity (on), including into the source it moves to across a link, or stop (off).",
            """{"type":"object","properties":{"view":{"type":"integer"},"on":{"type":"boolean"}},"required":["on"]}""", true, ops.Track);
        add("ghost", "List the ghost sources (pose caches with no simulation) and the views each is drawn over. With source and on, draw a ghost over a view (default: every view of the source it belongs over) or stop drawing it.",
            """{"type":"object","properties":{"source":{"type":"integer"},"view":{"type":"integer"},"on":{"type":"boolean"}}}""", true, ops.Ghost);
        add("open_real", "Open the real simulation a ghost source was recorded from: the replay the cache names, looked up next to the cache, with the module it names. The new source is moved to the ghost's cursor and the ghost is drawn over it.",
            """{"type":"object","properties":{"source":{"type":"integer"},"keyframeInterval":{"type":"integer","minimum":1}},"required":["source"]}""", true, ops.OpenReal);
        add("diff_shared", "Compare only the entities two sources both hold (a partial world against a whole one) at a step, as paired by map: identity (same ids; default) or links (the declared transfers). Returns the first differing shared entity and field and how many entities were shared or held by only one side. Optional: source (default the active one), other (default the fork's parent), step.",
            """{"type":"object","properties":{"source":{"type":"integer"},"other":{"type":"integer"},"step":{"type":"integer"},"map":{"type":"string","enum":["identity","links"]}}}""", false, ops.DiffShared);
    }

    /// <summary>Adds what a source declares for these features to its <c>sources</c> entry: fields, link key, and for a ghost its origin and the offer to open the real simulation.</summary>
    public static void WriteSourceExtras(Utf8JsonWriter w, SourceSession s)
    {
        SourceInfo info = s.Info;
        if (info.Fields.Count > 0)
        {
            w.WriteStartArray("fields");
            foreach (FieldInfo f in info.Fields)
            {
                WriteFieldInfo(w, f);
            }

            w.WriteEndArray();
        }

        if (!string.IsNullOrEmpty(info.LinkKey))
        {
            w.WriteString("linkKey", info.LinkKey);
        }

        if (info.Ghost is not { } g)
        {
            return;
        }

        w.WriteStartObject("ghost");
        w.WriteString("module", g.ModuleId);
        w.WriteString("libraryVersion", g.LibraryVersion);
        w.WriteString("scene", g.Scene);
        w.WriteString("replay", g.Replay);
        string? real = RealPath(s);
        w.WriteStartObject("offer");
        w.WriteString("op", "open_real");
        w.WriteStartObject("args");
        w.WriteNumber("source", s.Index);
        w.WriteEndObject();
        w.WriteString("text", g.Replay.Length == 0
            ? "This cache names no replay, so there is no simulation to open."
            : $"Open {g.Replay} ({(g.ModuleId.Length > 0 ? g.ModuleId : "any module")}{(g.LibraryVersion.Length > 0 ? " " + g.LibraryVersion : string.Empty)}) at the ghost's frame.");
        w.WriteBoolean("available", real is not null);
        if (real is not null)
        {
            w.WriteString("path", real);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteFieldInfo(Utf8JsonWriter w, FieldInfo f)
    {
        w.WriteStartObject();
        w.WriteString("id", f.Id);
        w.WriteString("name", f.Name);
        w.WriteString("map", f.Map.ToString().ToLowerInvariant());
        w.WriteNumber("min", f.Min);
        w.WriteNumber("max", f.Max);
        w.WriteString("unit", f.Unit);
        w.WriteEndObject();
    }

    /// <summary>The replay a ghost's cache names, when it is a plain file name and the file exists next to the cache; else null.</summary>
    private static string? RealPath(SourceSession ghost)
    {
        string replay = ghost.Info.Ghost?.Replay ?? string.Empty;
        if (replay.Length == 0 || replay != Path.GetFileName(replay) || replay is "." or "..")
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(ghost.Path);
        if (dir is null)
        {
            return null;
        }

        string full = Path.Combine(dir, replay);
        return File.Exists(full) ? full : null;
    }

    private SourceSession SourceArg(Args a) => shell.Source(a.Int("source") ?? a.Int("branch") ?? shell.ActiveSource);

    // ---- field ----------------------------------------------------------------------------------------------------------

    private string Field(Args a)
    {
        SourceSession s = SourceArg(a);
        string? id = a.String("field");
        if (id is null)
        {
            return JsonOut.Build(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("source", s.Index);
                w.WriteNumber("step", s.History.CurrentStep);
                w.WriteStartArray("fields");
                foreach (FieldInfo f in s.Info.Fields)
                {
                    WriteFieldInfo(w, f);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        FieldInfo? declared = null;
        foreach (FieldInfo f in s.Info.Fields)
        {
            if (f.Id == id)
            {
                declared = f;
            }
        }

        if (declared is not { } field)
        {
            string known = s.Info.Fields.Count == 0 ? "none" : string.Join(", ", s.Info.Fields.Select(f => f.Id));
            throw new ControlException("bad_args", $"Source {s.Index} declares no field '{id}'; its fields are: {known}.");
        }

        double value;
        Vector3? point = a.Vector("point");
        ulong? entity = a.Entity("entity");
        if ((point is null) == (entity is null))
        {
            throw new ControlException("bad_args", "Give exactly one of 'entity' (an entity id) or 'point' (an array of 2 or 3 numbers).");
        }

        if (entity is { } raw)
        {
            if (raw == 0)
            {
                throw new ControlException("bad_args", "Argument 'entity' must be a positive entity id.");
            }

            if (!s.History.View.TryReadField(id, new EntityId(raw), out value))
            {
                throw new ControlException("no_value", $"Field '{id}' has no value at entity {raw} in source {s.Index} at step {s.History.CurrentStep}: the entity is missing or the field does not cover it.");
            }
        }
        else if (!s.History.View.TryReadFieldAt(id, point!.Value, out value))
        {
            Vector3 p = point.Value;
            throw new ControlException("no_value", $"Field '{id}' has no value at ({p.X.ToString(CultureInfo.InvariantCulture)}, {p.Y.ToString(CultureInfo.InvariantCulture)}, {p.Z.ToString(CultureInfo.InvariantCulture)}) in source {s.Index}: the point is outside the field.");
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("step", s.History.CurrentStep);
            w.WriteString("field", id);
            if (entity is { } e)
            {
                w.WriteNumber("entity", e);
            }
            else
            {
                Vector3 p = point!.Value;
                w.WriteStartArray("point");
                w.WriteNumberValue(p.X);
                w.WriteNumberValue(p.Y);
                w.WriteNumberValue(p.Z);
                w.WriteEndArray();
            }

            if (double.IsFinite(value))
            {
                w.WriteNumber("value", value);
            }
            else
            {
                w.WriteString("value", value.ToString(CultureInfo.InvariantCulture));
            }

            w.WriteString("unit", field.Unit);
            w.WriteNumber("min", field.Min);
            w.WriteNumber("max", field.Max);
            w.WriteEndObject();
        });
    }

    // ---- links and selection ----------------------------------------------------------------------------------------------

    private string Links() => JsonOut.Build(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("step", shell.Step);
        w.WriteStartArray("links");
        foreach (ShellModel.SourceLink l in shell.Links())
        {
            w.WriteStartObject();
            w.WriteStartObject("from");
            w.WriteNumber("source", l.FromSource);
            w.WriteNumber("entity", l.From.Value);
            w.WriteEndObject();
            w.WriteStartObject("to");
            w.WriteNumber("source", l.ToSource);
            w.WriteNumber("entity", l.To.Value);
            w.WriteEndObject();
            w.WriteNumber("step", l.Step);
            w.WriteBoolean("crossed", shell.Step >= l.Step);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    private string Selection()
    {
        var props = new PropertyList();
        var all = new List<EntityInfo>();
        SourceSession? s = shell.Sources.Count > 0 ? shell.Source(shell.ActiveSource) : null;
        bool found = s is not null && shell.Selected.IsSome && s.History.View.Inspect(shell.Selected, props);
        EntityInfo info = default;
        if (found && s is not null)
        {
            s.History.View.CollectEntities(all);
            info = all.FirstOrDefault(e => e.Id == shell.Selected);
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("step", shell.Step);
            if (s is null || !found)
            {
                w.WriteNumber("source", shell.ActiveSource);
                w.WriteNull("entity");
                w.WriteEndObject();
                return;
            }

            w.WriteNumber("source", s.Index);
            w.WriteString("sourceName", s.Label);
            w.WriteNumber("entity", shell.Selected.Value);
            w.WriteString("kind", info.Kind ?? string.Empty);
            w.WriteString("name", info.Name ?? string.Empty);
            w.WriteStartArray("properties");
            foreach (Property p in props.Rows)
            {
                w.WriteStartObject();
                w.WriteString("group", p.Group);
                w.WriteString("name", p.Name);
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

    private string Track(Args a)
    {
        bool on = a.Bool("on") ?? throw new ControlException("bad_args", "Argument 'on' is required (true or false).");
        int index = a.Int("view") ?? shell.ActiveView;
        ViewState v = shell.View(index);
        v.Track = on;
        v.Touch();
        shell.FollowLinks();
        shell.Touch();
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("view", index);
            w.WriteBoolean("track", v.Track);
            w.WriteNumber("source", v.Source);
            w.WriteStartArray("target");
            w.WriteNumberValue(v.Target.X);
            w.WriteNumberValue(v.Target.Y);
            w.WriteNumberValue(v.Target.Z);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    // ---- ghosts -----------------------------------------------------------------------------------------------------------

    private string Ghost(Args a)
    {
        if (a.Has("source") && a.Bool("on") is { } turn)
        {
            SourceSession g = RequireGhost(SourceArg(a));
            if (turn)
            {
                if (a.Int("view") is { } viewIndex)
                {
                    ViewState v = shell.View(viewIndex);
                    if (!v.Ghosts.Contains(g))
                    {
                        v.Ghosts.Add(g);
                        v.Touch();
                        shell.Touch();
                    }
                }
                else
                {
                    SourceSession? host = shell.HostFor(g);
                    if (host is null || !shell.AttachGhost(g, host))
                    {
                        throw new ControlException("rejected", $"No open source with a view fits ghost {g.Index}; open the real source first, or name a view.");
                    }
                }
            }
            else
            {
                shell.DetachGhost(g, a.Int("view"));
            }
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("ghosts");
            foreach (SourceSession s in shell.Sources)
            {
                if (s.Info.Ghost is null)
                {
                    continue;
                }

                w.WriteStartObject();
                w.WriteNumber("source", s.Index);
                w.WriteString("name", s.Label);
                w.WriteNumber("step", s.History.CurrentStep);
                w.WriteNumber("startStep", s.History.StartStep);
                w.WriteNumber("endStep", s.History.EndStep);
                w.WriteStartArray("views");
                for (int i = 0; i < shell.Views.Count; i++)
                {
                    if (shell.Views[i].Ghosts.Contains(s))
                    {
                        w.WriteNumberValue(i);
                    }
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static SourceSession RequireGhost(SourceSession s) =>
        s.Info.Ghost is null ? throw new ControlException("bad_args", $"Source {s.Index} ('{s.Label}') is not a ghost source.") : s;

    private string OpenReal(Args a)
    {
        int index = a.Int("source") ?? throw new ControlException("bad_args", "Argument 'source' is required (the ghost's source number).");
        SourceSession g = RequireGhost(shell.Source(index));
        GhostInfo info = g.Info.Ghost!.Value;
        string? path = RealPath(g);
        if (path is null)
        {
            throw new ControlException("not_found", info.Replay.Length == 0
                ? $"Ghost {g.Index} names no replay, so there is no simulation to open."
                : $"Ghost {g.Index} names replay '{info.Replay}', which is not a file next to '{g.Path}'. Open it with open and attach the ghost with ghost.");
        }

        long step = shell.Step;
        SourceSession real = shell.Open(path, info.ModuleId.Length > 0 ? info.ModuleId : null, a.Int("keyframeInterval") ?? 0, null);
        shell.AttachGhost(g, real);
        long target = Math.Clamp(step, real.History.StartStep, real.History.EndStep);
        shell.Seek(Math.Clamp(target, shell.MinSteps, shell.MaxSteps));
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", real.Index);
            w.WriteString("path", path);
            w.WriteString("module", real.Info.ModuleId);
            w.WriteNumber("ghost", g.Index);
            w.WriteNumber("step", real.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(real.History.StateHash));
            w.WriteEndObject();
        });
    }

    // ---- shared-entity diff -----------------------------------------------------------------------------------------------

    private string DiffShared(Args a)
    {
        SourceSession left = SourceArg(a);
        SourceSession right;
        if (a.Int("other") is { } other)
        {
            right = shell.Source(other);
        }
        else if (left.Parent is { } parent)
        {
            right = parent;
        }
        else
        {
            throw new ControlException("bad_args", $"Source {left.Index} is not a fork; name the source to compare with in 'other'.");
        }

        if (left.Info.Ghost is not null || right.Info.Ghost is not null)
        {
            throw new ControlException("bad_args", "A ghost source holds poses only and can't be compared with diff_shared.");
        }

        if (!ReferenceEquals(left.Module, right.Module))
        {
            throw new ControlException("bad_args", $"Sources {left.Index} and {right.Index} come from different modules ({left.Info.ModuleId}, {right.Info.ModuleId}) and can't be compared.");
        }

        long from = Math.Max(left.History.StartStep, right.History.StartStep);
        long limit = Math.Min(left.History.EndStep, right.History.EndStep);
        long step = a.Long("step") ?? Math.Clamp(shell.Step, from, limit);
        if (step < from || step > limit)
        {
            throw new ControlException("bad_args", $"Step {step} is outside both recordings; use {from} to {limit}.");
        }

        string mapName = a.String("map") ?? "identity";
        EntityMap map;
        switch (mapName)
        {
            case "identity":
                map = EntityMap.Identity;
                break;
            case "links":
                var pairs = new List<KeyValuePair<EntityId, EntityId>>();
                foreach (ShellModel.SourceLink l in shell.Links())
                {
                    if (l.FromSource == left.Index && l.ToSource == right.Index)
                    {
                        pairs.Add(new(l.From, l.To));
                    }
                    else if (l.FromSource == right.Index && l.ToSource == left.Index)
                    {
                        pairs.Add(new(l.To, l.From));
                    }
                }

                map = EntityMap.FromPairs(pairs);
                break;
            default:
                throw new ControlException("bad_args", $"Argument 'map' must be 'identity' or 'links', not '{mapName}'.");
        }

        SharedDifference result;
        ulong hashLeft;
        ulong hashRight;
        try
        {
            left.History.Seek(step);
            right.History.Seek(step);
            result = left.History.World.CompareShared(right.History.World, map);
            hashLeft = left.History.StateHash;
            hashRight = right.History.StateHash;
        }
        catch (ArgumentException ex)
        {
            throw new ControlException("bad_args", ex.Message);
        }
        finally
        {
            shell.Seek(shell.Step);
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", left.Index);
            w.WriteNumber("other", right.Index);
            w.WriteNumber("step", step);
            w.WriteString("map", mapName);
            w.WriteBoolean("equal", result.First is null);
            w.WriteNumber("shared", result.Shared);
            w.WriteNumber("onlySource", result.OnlyLeft);
            w.WriteNumber("onlyOther", result.OnlyRight);
            w.WriteString("hashSource", JsonOut.Hex(hashLeft));
            w.WriteString("hashOther", JsonOut.Hex(hashRight));
            if (result.First is { } d)
            {
                w.WriteStartObject("difference");
                w.WriteNumber("entity", d.Entity.Value);
                if (map.TryMap(d.Entity, out EntityId mapped) && mapped != d.Entity)
                {
                    w.WriteNumber("otherEntity", mapped.Value);
                }

                w.WriteString("field", d.Field);
                w.WriteString("source", d.Left);
                w.WriteString("other", d.Right);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("difference");
            }

            w.WriteEndObject();
        });
    }
}
