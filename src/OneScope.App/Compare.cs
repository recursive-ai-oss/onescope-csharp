using OneScope.Sdk;

namespace OneScope.App;

/// <summary>What two compared sources are.</summary>
internal enum CompareKind
{
    /// <summary>The same module instance on both sides (a fork and its original, or two sources of one file): the module's own difference finder applies.</summary>
    Fork,

    /// <summary>Two loaded copies of one module id, each in its own load context (two versions of a library): worlds are compared through the inspector.</summary>
    Versions,

    /// <summary>Only the entities both sides hold (a partial world against a whole one); world hashes are not expected to agree.</summary>
    Shared,
}

/// <summary>One difference between two sources at a step.</summary>
/// <param name="Entity">The entity in the first source.</param>
/// <param name="Other">The entity in the second source (the same id unless a map paired them differently).</param>
/// <param name="Field">The inspector row that differs; "(entity)" when only one side holds the entity.</param>
/// <param name="Value">The first source's value, or "(missing)".</param>
/// <param name="OtherValue">The second source's value, or "(missing)".</param>
internal readonly record struct CompareRow(EntityId Entity, EntityId Other, string Field, string Value, string OtherValue);

/// <summary>The rows at one step, bounded.</summary>
/// <param name="Rows">At most the requested number of rows, in the first source's entity order, then the second source's extras.</param>
/// <param name="Total">How many differences there are, counted past the bound. For a shared comparison, entities only one side holds are counted separately and are not differences.</param>
/// <param name="Shared">Entities both sides hold (roots are left out for <see cref="CompareKind.Shared"/>).</param>
/// <param name="OnlyFirst">Entities only the first source holds.</param>
/// <param name="OnlySecond">Entities only the second source holds.</param>
internal sealed record CompareRows(IReadOnlyList<CompareRow> Rows, int Total, int Shared, int OnlyFirst, int OnlySecond);

/// <summary>
/// Where two sources' worlds agree along the shared steps, found by playing a private copy of each from the start of their common range
/// (the shell's own cursors are not touched). Agreement is world-hash equality, or for <see cref="CompareKind.Shared"/> equality of the shared entities.
/// </summary>
internal sealed class Agreement
{
    /// <summary>Most steps a hash comparison plays.</summary>
    public const long MaxHashSteps = 100_000;

    /// <summary>Most steps a shared-entity comparison plays (it inspects every entity at every step).</summary>
    public const long MaxSharedSteps = 10_000;

    /// <summary>Most disagreement runs kept; further ones are counted only.</summary>
    public const int MaxRuns = 100_000;

    private readonly List<(long From, long To)> _runs = [];

    private Agreement(long from, long to, long scannedThrough)
    {
        From = from;
        To = to;
        ScannedThrough = scannedThrough;
    }

    /// <summary>First step both sources have, in the first source's numbering.</summary>
    public long From { get; }

    /// <summary>Last step both sources have.</summary>
    public long To { get; }

    /// <summary>The last step compared; below <see cref="To"/> when the scan limit stopped it.</summary>
    public long ScannedThrough { get; }

    /// <summary>How many steps disagree.</summary>
    public long Disagreeing { get; private set; }

    /// <summary>True when more runs than <see cref="MaxRuns"/> were found and the extra ones are not listed.</summary>
    public bool RunsTruncated { get; private set; }

    /// <summary>Runs of consecutive disagreeing steps, in order.</summary>
    public IReadOnlyList<(long From, long To)> Runs => _runs;

    /// <summary>The first step that disagrees, or null.</summary>
    public long? First => _runs.Count > 0 ? _runs[0].From : null;

    /// <summary>The last step compared that disagrees, or null.</summary>
    public long? Last => _runs.Count > 0 ? _runs[^1].To : null;

    /// <summary>Whether <paramref name="step"/> disagrees.</summary>
    public bool IsDivergent(long step)
    {
        int i = FirstRunEndingAtOrAfter(step);
        return i < _runs.Count && _runs[i].From <= step;
    }

    /// <summary>The nearest disagreeing step after <paramref name="step"/>, or null.</summary>
    public long? Next(long step)
    {
        int i = FirstRunEndingAtOrAfter(step + 1);
        return i < _runs.Count ? Math.Max(_runs[i].From, step + 1) : null;
    }

    /// <summary>The nearest disagreeing step before <paramref name="step"/>, or null.</summary>
    public long? Previous(long step)
    {
        for (int i = _runs.Count - 1; i >= 0; i--)
        {
            if (_runs[i].From < step)
            {
                return Math.Min(_runs[i].To, step - 1);
            }
        }

        return null;
    }

    private int FirstRunEndingAtOrAfter(long step)
    {
        int lo = 0;
        int hi = _runs.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_runs[mid].To < step)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Plays private copies of both sources through their common steps and records where they disagree.</summary>
    public static Agreement Compute(ComparePair pair)
    {
        SourceSession a = pair.First;
        SourceSession b = pair.Second;
        CompareKind kind = pair.Kind;
        EntityMap map = pair.Map;
        pair.Range(out long from, out long to);
        if (to < from)
        {
            return new Agreement(from, from - 1, from - 1);
        }

        long limit = kind == CompareKind.Shared ? MaxSharedSteps : MaxHashSteps;
        long through = Math.Min(to, from + limit);
        var options = new ScopeHistoryOptions { KeyframeInterval = int.MaxValue / 2 };
        var ha = new History(a.History.Source, options);
        var hb = new History(b.History.Source, options);
        ha.Seek(from);
        hb.Seek(from + pair.Offset);
        var result = new Agreement(from, to, through);
        long runFrom = -1;
        for (long step = from; step <= through; step++)
        {
            if (step > from)
            {
                ha.Step();
                hb.Step();
            }

            bool same = kind == CompareKind.Shared
                ? SharedEqual(a, b, ha.World, hb.World, map)
                : ha.StateHash == hb.StateHash;
            if (!same)
            {
                result.Disagreeing++;
                if (runFrom < 0)
                {
                    runFrom = step;
                }
            }
            else if (runFrom >= 0)
            {
                result.Close(runFrom, step - 1);
                runFrom = -1;
            }
        }

        if (runFrom >= 0)
        {
            result.Close(runFrom, through);
        }

        return result;
    }

    private void Close(long from, long to)
    {
        if (_runs.Count < MaxRuns)
        {
            _runs.Add((from, to));
        }
        else
        {
            RunsTruncated = true;
        }
    }

    private static bool SharedEqual(SourceSession a, SourceSession b, IScopeWorld wa, IScopeWorld wb, EntityMap map) =>
        CompareWorlds.Shared(a, b, wa, wb, map).First is null;
}

/// <summary>Comparisons of two worlds that may come from different load contexts, so only the SDK's interfaces are used across them.</summary>
internal static class CompareWorlds
{
    /// <summary>The module's own shared comparison when both sources use one module instance; the inspector-based one otherwise.</summary>
    public static SharedDifference Shared(SourceSession a, SourceSession b, IScopeWorld wa, IScopeWorld wb, EntityMap map) =>
        ReferenceEquals(a.Module, b.Module) ? wa.CompareShared(wb, map) : SharedDiff.Compare(wa, wb, map);

    /// <summary>
    /// The first difference between two worlds: the module's own finder for one module instance, else the first shared entity
    /// and field by the inspector's rows (a module's finder can't be handed a world made by another copy of the module).
    /// </summary>
    public static Difference? First(SourceSession a, SourceSession b, IScopeWorld wa, IScopeWorld wb) =>
        ReferenceEquals(a.Module, b.Module) ? wa.FirstDifference(wb) : SharedDiff.Compare(wa, wb, EntityMap.Identity).First;

    /// <summary>
    /// Every difference between two worlds' inspector rows, at most <paramref name="limit"/> listed. When the worlds sit at different step numbers
    /// (an aligned pair), rows that show those step numbers are left out.
    /// </summary>
    public static CompareRows Rows(IScopeWorld wa, IScopeWorld wb, CompareKind kind, EntityMap map, int limit)
    {
        // With the steps aligned across an offset, a row that shows each world's own step number ("Step" 0 and 1) is the offset, not a difference.
        string stepA = wa.Step.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string stepB = wb.Step.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool offset = stepA != stepB;
        var ea = new List<EntityInfo>();
        var eb = new List<EntityInfo>();
        wa.CollectEntities(ea);
        wb.CollectEntities(eb);
        bool skipRoots = kind == CompareKind.Shared;

        var inB = new Dictionary<ulong, EntityInfo>(eb.Count);
        foreach (EntityInfo e in eb)
        {
            if (!skipRoots || e.Parent.IsSome)
            {
                inB[e.Id.Value] = e;
            }
        }

        var rows = new List<CompareRow>();
        var pa = new PropertyList();
        var pb = new PropertyList();
        var byName = new Dictionary<(string, string), ScopeValue>();
        var matched = new HashSet<ulong>();
        int total = 0;
        int shared = 0;
        int onlyFirst = 0;

        void Add(CompareRow row)
        {
            total++;
            if (rows.Count < limit)
            {
                rows.Add(row);
            }
        }

        foreach (EntityInfo e in ea)
        {
            if (skipRoots && !e.Parent.IsSome)
            {
                continue;
            }

            if (!map.TryMap(e.Id, out EntityId other) || !inB.ContainsKey(other.Value) || !matched.Add(other.Value))
            {
                // A partial world is expected to lack entities, so for a shared comparison they are counted, not listed as differences.
                onlyFirst++;
                if (!skipRoots)
                {
                    Add(new CompareRow(e.Id, EntityId.None, "(entity)", "present", "(missing)"));
                }

                continue;
            }

            shared++;
            bool okA = wa.Inspect(e.Id, pa);
            bool okB = wb.Inspect(other, pb);
            if (!okA || !okB)
            {
                Add(new CompareRow(e.Id, other, "(inspect)", okA ? "present" : "(missing)", okB ? "present" : "(missing)"));
                continue;
            }

            byName.Clear();
            foreach (Property p in pb.Rows)
            {
                byName[(p.Group, p.Name)] = p.Value;
            }

            foreach (Property p in pa.Rows)
            {
                if (!byName.Remove((p.Group, p.Name), out ScopeValue v))
                {
                    Add(new CompareRow(e.Id, other, p.Name, p.Value.ToString(), "(missing)"));
                }
                else if (p.Value != v && !(offset && p.Value.ToString() == stepA && v.ToString() == stepB))
                {
                    Add(new CompareRow(e.Id, other, p.Name, p.Value.ToString(), v.ToString()));
                }
            }

            foreach (KeyValuePair<(string, string), ScopeValue> extra in byName)
            {
                Add(new CompareRow(e.Id, other, extra.Key.Item2, "(missing)", extra.Value.ToString()));
            }
        }

        int onlySecond = 0;
        foreach (EntityInfo e in eb)
        {
            if ((!skipRoots || e.Parent.IsSome) && !matched.Contains(e.Id.Value))
            {
                onlySecond++;
                if (!skipRoots)
                {
                    Add(new CompareRow(EntityId.None, e.Id, "(entity)", "(missing)", "present"));
                }
            }
        }

        return new CompareRows(rows, total, shared, onlyFirst, onlySecond);
    }
}

/// <summary>The pair of sources the compare panel, the timeline's agreement track and the <c>compare</c> operation look at.</summary>
internal sealed class ComparePair
{
    public ComparePair(SourceSession first, SourceSession second, CompareKind kind, string mapName, EntityMap map, long offset = 0)
    {
        Offset = offset;
        First = first;
        Second = second;
        Kind = kind;
        MapName = mapName;
        Map = map;
    }

    public SourceSession First { get; }

    public SourceSession Second { get; }

    public CompareKind Kind { get; }

    /// <summary>"identity" or "links".</summary>
    public string MapName { get; }

    public EntityMap Map { get; }

    /// <summary>
    /// How far the second source's step numbers run ahead of the first's for the same position in the replay. One file played through two versions of
    /// a module can number its steps differently (one may start at 0 and the other at 1); aligning the starts compares like with like.
    /// Every step this pair reports is in the first source's numbering.
    /// </summary>
    public long Offset { get; }

    /// <summary>The steps both sources have, in the first source's numbering. <paramref name="to"/> is below <paramref name="from"/> when they share none.</summary>
    public void Range(out long from, out long to)
    {
        from = Math.Max(First.History.StartStep, Second.History.StartStep - Offset);
        to = Math.Min(First.History.EndStep, Second.History.EndStep - Offset);
    }

    /// <summary>The offset a pair of module copies on one file gets: their starts aligned when both play the same number of steps.</summary>
    public static long AutoOffset(SourceSession first, SourceSession second, CompareKind kind) =>
        kind == CompareKind.Versions && first.History.StepCount == second.History.StepCount ? second.History.StartStep - first.History.StartStep : 0;

    private Agreement? _agreement;
    private (long A, long B) _key = (-1, -1);

    /// <summary>The agreement along the common steps, recomputed when either recording grew.</summary>
    public Agreement Agreement
    {
        get
        {
            var key = (First.History.EndStep, Second.History.EndStep);
            if (_agreement is null || key != _key)
            {
                _key = key;
                _agreement = Agreement.Compute(this);
            }

            return _agreement;
        }
    }

    /// <summary>The compare panel's rows at one step, cached by step and recording ends.</summary>
    public CompareRows? CachedRows { get; set; }

    public (long Step, long EndA, long EndB, int Limit) RowsKey { get; set; } = (-1, -1, -1, -1);
}
