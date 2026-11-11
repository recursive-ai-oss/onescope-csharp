using System.Text.Json;
using OneGame.Control;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>The <c>compare</c> operation: one pair of sources, where they agree and where they don't.</summary>
internal sealed partial class ScopeEndpoint
{
    private const int DefaultCompareRows = 50;
    private const int MaxCompareRows = 1000;
    private const int MaxRunsListed = 64;

    private ComparePair ResolvePair(Args a)
    {
        string? kind = a.String("kind");
        string? map = a.String("map");
        long? offset = a.Long("offset");
        bool named = a.Has("source") || a.Has("other") || a.Has("branch");
        if (!named && _shell.EffectivePair is { } current && kind is null && map is null && offset is null)
        {
            return current;
        }

        SourceSession first = SourceArg(a);
        SourceSession second;
        if (a.Int("other") is { } other)
        {
            second = _shell.Source(other);
        }
        else if (first.Parent is { } parent)
        {
            second = parent;
        }
        else if (_shell.EffectivePair is { } p && (p.First == first || p.Second == first))
        {
            second = p.First == first ? p.Second : p.First;
        }
        else
        {
            throw new ControlException("bad_args", $"Source {first.Index} is not a fork and is not paired with another source; name the source to compare with in 'other'.");
        }

        return _shell.MakePair(first, second, kind, map, offset);
    }

    private string Compare(Args a)
    {
        ComparePair pair = ResolvePair(a);
        int limit = Math.Clamp(a.Int("limit") ?? DefaultCompareRows, 1, MaxCompareRows);
        SourceSession first = pair.First;
        SourceSession second = pair.Second;
        pair.Range(out long from, out long to);
        if (to < from)
        {
            throw new ControlException("bad_args", $"Sources {first.Index} and {second.Index} share no steps (second source's numbers shifted by {pair.Offset}): {first.Index} runs {first.History.StartStep} to {first.History.EndStep}, {second.Index} runs {second.History.StartStep} to {second.History.EndStep}.");
        }

        Agreement agreement = pair.Agreement;
        long at = a.Long("step") ?? Math.Clamp(_shell.Step, from, to);
        if (at < from || at > to)
        {
            throw new ControlException("bad_args", $"Step {at} is outside both recordings; use {from} to {to}.");
        }

        bool moved = false;
        string? go = a.String("goto");
        if (go is not null)
        {
            long? target = go switch
            {
                "first" => agreement.First,
                "last" => agreement.Last,
                "next" => agreement.Next(at),
                "previous" => agreement.Previous(at),
                _ => throw new ControlException("bad_args", $"Argument 'goto' must be 'first', 'next', 'previous' or 'last', not '{go}'."),
            };
            if (target is long t)
            {
                at = t;
                moved = true;
                _shell.Seek(t);
            }
        }

        _shell.Pair = pair;
        _shell.Panels.Compare = true;

        CompareRows rows;
        Difference? difference;
        ulong hashFirst;
        ulong hashSecond;
        try
        {
            first.History.Seek(at);
            second.History.Seek(at + pair.Offset);
            hashFirst = first.History.StateHash;
            hashSecond = second.History.StateHash;
            rows = CompareWorlds.Rows(first.History.World, second.History.World, pair.Kind, pair.Map, limit);
            difference = pair.Kind == CompareKind.Shared
                ? CompareWorlds.Shared(first, second, first.History.World, second.History.World, pair.Map).First
                : CompareWorlds.First(first, second, first.History.World, second.History.World);
        }
        finally
        {
            _shell.Seek(_shell.Step);
        }

        string kindName = pair.Kind.ToString().ToLowerInvariant();
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", first.Index);
            w.WriteNumber("other", second.Index);
            w.WriteString("kind", kindName);
            w.WriteString("map", pair.MapName);
            w.WriteNumber("step", at);
            w.WriteNumber("otherStep", at + pair.Offset);
            w.WriteNumber("offset", pair.Offset);
            w.WriteBoolean("moved", moved);
            w.WriteNumber("from", from);
            w.WriteNumber("to", to);
            w.WriteBoolean("equal", pair.Kind == CompareKind.Shared ? rows.Total == 0 : hashFirst == hashSecond);
            w.WriteString("hash", JsonOut.Hex(hashFirst));
            w.WriteString("otherHash", JsonOut.Hex(hashSecond));
            // Inputs are comparable only within one module copy; two copies may number a replay differently.
            if (pair.Kind != CompareKind.Versions && first.History.Recording.FirstDifference(second.History.Recording) is long input)
            {
                w.WriteNumber("divergence", input);
            }
            else
            {
                w.WriteNull("divergence");
            }

            w.WriteStartObject("agreement");
            w.WriteString("basis", pair.Kind == CompareKind.Shared ? "shared entities" : "world hash");
            w.WriteNumber("scannedThrough", agreement.ScannedThrough);
            w.WriteBoolean("complete", agreement.ScannedThrough >= agreement.To);
            w.WriteNumber("divergentSteps", agreement.Disagreeing);
            WriteStepOrNull(w, "firstDivergent", agreement.First);
            WriteStepOrNull(w, "lastDivergent", agreement.Last);
            WriteStepOrNull(w, "nextDivergent", agreement.Next(at));
            WriteStepOrNull(w, "previousDivergent", agreement.Previous(at));
            w.WriteBoolean("divergentHere", agreement.IsDivergent(at));
            w.WriteStartArray("runs");
            for (int i = 0; i < agreement.Runs.Count && i < MaxRunsListed; i++)
            {
                w.WriteStartArray();
                w.WriteNumberValue(agreement.Runs[i].From);
                w.WriteNumberValue(agreement.Runs[i].To);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteBoolean("runsTruncated", agreement.RunsTruncated || agreement.Runs.Count > MaxRunsListed);
            w.WriteEndObject();

            if (difference is { } d)
            {
                w.WriteStartObject("difference");
                w.WriteNumber("entity", d.Entity.Value);
                w.WriteString("field", d.Field);
                w.WriteString("source", d.Left);
                w.WriteString("other", d.Right);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("difference");
            }

            w.WriteNumber("shared", rows.Shared);
            w.WriteNumber("onlySource", rows.OnlyFirst);
            w.WriteNumber("onlyOther", rows.OnlySecond);
            w.WriteNumber("rowCount", rows.Total);
            w.WriteBoolean("truncated", rows.Total > rows.Rows.Count);
            w.WriteStartArray("rows");
            foreach (CompareRow r in rows.Rows)
            {
                w.WriteStartObject();
                if (r.Entity.IsSome)
                {
                    w.WriteNumber("entity", r.Entity.Value);
                }

                if (r.Other.IsSome && r.Other != r.Entity)
                {
                    w.WriteNumber("otherEntity", r.Other.Value);
                }

                w.WriteString("field", r.Field);
                w.WriteString("source", r.Value);
                w.WriteString("other", r.OtherValue);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static void WriteStepOrNull(Utf8JsonWriter w, string name, long? step)
    {
        if (step is long s)
        {
            w.WriteNumber(name, s);
        }
        else
        {
            w.WriteNull(name);
        }
    }
}
