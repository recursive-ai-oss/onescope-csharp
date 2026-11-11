using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Read-only operations on a source's world: entities, inspect, axes and findings.</summary>
internal sealed partial class ScopeEndpoint
{
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
}
