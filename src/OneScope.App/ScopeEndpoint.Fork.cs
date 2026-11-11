using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Fork-and-poke operations: fork, poke, diff and replay_save.</summary>
internal sealed partial class ScopeEndpoint
{
    private string ForkOp(Args a)
    {
        RequireSources();
        SourceSession parent = SourceArg(a);
        long step = a.Long("step") ?? Math.Clamp(_shell.Step, parent.History.StartStep, parent.History.EndStep);
        SourceSession fork = _shell.Fork(parent, step, a.Bool("view") ?? true);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("branch", fork.Index);
            WriteSourceInfo(w, fork);
            w.WriteNumber("step", fork.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(fork.History.StateHash));
            w.WriteNumber("views", _shell.Views.Count);
            w.WriteEndObject();
        });
    }


    private string PokeOp(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        string? name = a.String("poke");
        if (name is null)
        {
            return JsonOut.Build(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("source", s.Index);
                w.WriteBoolean("fork", s.IsFork);
                WriteSourceInfo(w, s);
                w.WriteEndObject();
            });
        }

        // An opened file refuses before anything else is looked at, so the error is the same whatever was asked.
        ShellModel.RequireFork(s);
        PokeInfo? info = null;
        foreach (PokeInfo p in s.Info.Pokes)
        {
            if (string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                info = p;
                break;
            }
        }

        if (info is not { } poke)
        {
            throw new ControlException("not_found", $"Source {s.Index} declares no poke '{name}'; its pokes are {string.Join(", ", s.Info.Pokes.Select(x => x.Id))}.");
        }

        ScopeValue[] values = ParsePokeArgs(poke, a.Element("args"));
        long before = s.History.CurrentStep;
        _shell.Poke(s, new PokeCall(poke.Id, values));
        if (a.Bool("step") == true)
        {
            _shell.StepFork(s, 1);
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteString("poke", poke.Id);
            w.WriteNumber("appliesAtStep", before);
            w.WriteNumber("pending", s.Branch!.PendingPokes);
            w.WriteNumber("step", s.History.CurrentStep);
            w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
            w.WriteEndObject();
        });
    }

    private static ScopeValue[] ParsePokeArgs(PokeInfo poke, JsonElement? args)
    {
        ParamInfo[] ps = poke.Params;
        var values = new ScopeValue[ps.Length];
        JsonElement none = default;
        JsonElement given = args ?? none;
        if (given.ValueKind == JsonValueKind.Array)
        {
            if (given.GetArrayLength() != ps.Length)
            {
                throw new ControlException("bad_args", $"Poke '{poke.Id}' takes {ps.Length} arguments ({ParamList(ps)}), got {given.GetArrayLength()}.");
            }

            int i = 0;
            foreach (JsonElement e in given.EnumerateArray())
            {
                values[i] = ToValue(poke, ps[i], e);
                i++;
            }

            return values;
        }

        if (given.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            throw new ControlException("bad_args", $"Argument 'args' must be an object by parameter name or an array in parameter order: {ParamList(ps)}.");
        }

        for (int i = 0; i < ps.Length; i++)
        {
            if (given.ValueKind != JsonValueKind.Object || !given.TryGetProperty(ps[i].Name, out JsonElement e))
            {
                throw new ControlException("bad_args", $"Poke '{poke.Id}' needs argument '{ps[i].Name}' ({ps[i].Kind.ToString().ToLowerInvariant()}); its arguments are {ParamList(ps)}.");
            }

            values[i] = ToValue(poke, ps[i], e);
        }

        if (given.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty extra in given.EnumerateObject())
            {
                if (!ps.Any(p => p.Name == extra.Name))
                {
                    throw new ControlException("bad_args", $"Poke '{poke.Id}' has no argument '{extra.Name}'; its arguments are {ParamList(ps)}.");
                }
            }
        }

        return values;
    }

    private static string ParamList(ParamInfo[] ps) => ps.Length == 0 ? "none" : string.Join(", ", ps.Select(p => $"{p.Name}: {p.Kind.ToString().ToLowerInvariant()}"));

    private static ScopeValue ToValue(PokeInfo poke, ParamInfo param, JsonElement e)
    {
        string where = $"Poke '{poke.Id}' argument '{param.Name}'";
        switch (param.Kind)
        {
            case ValueKind.Bool when e.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return ScopeValue.OfBool(e.GetBoolean());
            case ValueKind.Integer when e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long l):
                return ScopeValue.OfInteger(l);
            case ValueKind.Number when e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d) && double.IsFinite(d):
                return ScopeValue.OfNumber(d);
            case ValueKind.Text when e.ValueKind == JsonValueKind.String:
                return ScopeValue.OfText(e.GetString()!);
            case ValueKind.Entity when e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long id) && id > 0:
                return ScopeValue.OfEntity(new EntityId((ulong)id));
            case ValueKind.Vector when e.ValueKind == JsonValueKind.Array && e.GetArrayLength() is 2 or 3:
                {
                    Span<float> c = stackalloc float[3];
                    int i = 0;
                    foreach (JsonElement x in e.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out double v) || !double.IsFinite(v))
                        {
                            throw new ControlException("bad_args", $"{where} must be an array of 2 or 3 finite numbers.");
                        }

                        c[i++] = (float)v;
                    }

                    return ScopeValue.OfVector(new Vector3(c[0], c[1], c[2]));
                }

            default:
                string expected = param.Kind switch
                {
                    ValueKind.Bool => "true or false",
                    ValueKind.Integer => "an integer",
                    ValueKind.Number => "a finite number",
                    ValueKind.Text => "a string",
                    ValueKind.Entity => "a positive entity id (an integer)",
                    ValueKind.Vector => "an array of 2 or 3 numbers",
                    _ => param.Kind.ToString(),
                };
                throw new ControlException("bad_args", $"{where} must be {expected}.");
        }
    }

    private string DiffOp(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        SourceSession other = a.Int("other") is int o
            ? _shell.Source(o)
            : s.Parent ?? throw new ControlException("bad_args", $"Source {s.Index} is an opened file with no parent; name the source to compare with in 'other'.");
        long step = a.Long("step") ?? Math.Clamp(_shell.Step, Math.Max(s.History.StartStep, other.History.StartStep), Math.Min(s.History.EndStep, other.History.EndStep));
        Difference? diff = _shell.Diff(s, other, step, out ulong hashA, out ulong hashB);
        long? divergence = s.Branch is { } b && ReferenceEquals(s.Parent, other) ? b.DivergenceStep : null;
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("other", other.Index);
            w.WriteNumber("step", step);
            w.WriteBoolean("equal", diff is null);
            w.WriteString("hash", JsonOut.Hex(hashA));
            w.WriteString("otherHash", JsonOut.Hex(hashB));
            if (divergence is long d)
            {
                w.WriteNumber("divergence", d);
            }
            else
            {
                w.WriteNull("divergence");
            }

            if (diff is { } x)
            {
                w.WriteStartObject("difference");
                w.WriteNumber("entity", x.Entity.Value);
                w.WriteString("field", x.Field);
                w.WriteString("value", x.Left);
                w.WriteString("otherValue", x.Right);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("difference");
            }

            w.WriteEndObject();
        });
    }

    private string ReplaySave(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        string relative = a.RequiredString("path");
        string full = ResolveInside(relative);
        foreach (SourceSession other in _shell.Sources)
        {
            if (string.Equals(Path.GetFullPath(other.Path), full, StringComparison.Ordinal))
            {
                throw new ControlException("rejected", $"'{relative}' is the file source {other.Index} was opened from; save to a new file.");
            }
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        s.Source.WriteReplay(buffer);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, buffer.WrittenSpan);
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan));
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteString("path", full);
            w.WriteNumber("bytes", buffer.WrittenCount);
            w.WriteString("sha256", sha);
            w.WriteNumber("steps", s.History.EndStep);
            if (s.Branch?.DivergenceStep is long d)
            {
                w.WriteNumber("divergence", d);
            }
            else
            {
                w.WriteNull("divergence");
            }

            w.WriteEndObject();
        });
    }
}
