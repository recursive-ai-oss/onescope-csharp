using OneGame.Control;
using OneScope.Sdk;

namespace OneScope.App;

internal sealed partial class ShellModel
{
    /// <summary>The most rows the compare panel lists.</summary>
    public const int PanelCompareRows = 200;

    private ComparePair? _pair;

    /// <summary>The pair set by the <c>compare</c> operation or by opening one file through two module copies, if any.</summary>
    public ComparePair? Pair
    {
        get => _pair;
        set
        {
            _pair = value;
            if (value is not null)
            {
                // A pair whose second source numbers its steps differently is shown at one moment: the shared cursor step s puts the
                // first source at its step s and the second at s + the pair's offset.
                value.Second.Offset = value.First.Offset + value.Offset;
                SeekCore(Math.Clamp(Step, MinSteps, MaxSteps));
            }

            Touch();
        }
    }

    /// <summary>The pair the compare panel shows: the chosen one, else the active fork against its parent, else none.</summary>
    public ComparePair? EffectivePair
    {
        get
        {
            if (_pair is not null && _sources.Contains(_pair.First) && _sources.Contains(_pair.Second))
            {
                return _pair;
            }

            if (ActiveSource < _sources.Count && _sources[ActiveSource] is { Parent: { } parent } fork)
            {
                if (_implicit is null || _implicit.First != fork || _implicit.Second != parent)
                {
                    _implicit = new ComparePair(fork, parent, CompareKind.Fork, "identity", EntityMap.Identity);
                }

                return _implicit;
            }

            return null;
        }
    }

    private ComparePair? _implicit;

    /// <summary>Forgets the chosen pair (when every source is replaced).</summary>
    internal void ClearPair()
    {
        _pair = null;
        _implicit = null;
    }

    /// <summary>Checks that two sources can be compared and says how.</summary>
    /// <param name="kind">"shared" to compare only entities both sides hold; null to infer: one module instance is <see cref="CompareKind.Fork"/>, two copies of one module id <see cref="CompareKind.Versions"/>.</param>
    /// <param name="map">"identity" (default) or "links" (the declared transfers; shared only).</param>
    /// <exception cref="ControlException"><c>bad_args</c> when the sources are the same, ghosts, or from different modules.</exception>
    public ComparePair MakePair(SourceSession first, SourceSession second, string? kind, string? map, long? offset = null)
    {
        if (first == second)
        {
            throw new ControlException("bad_args", $"Source {first.Index} can't be compared with itself; name a different source in 'other'.");
        }

        if (first.Info.Ghost is not null || second.Info.Ghost is not null)
        {
            throw new ControlException("bad_args", "A ghost source holds poses only and can't be compared.");
        }

        if (!ReferenceEquals(first.Module, second.Module) && first.Info.ModuleId != second.Info.ModuleId)
        {
            throw new ControlException("bad_args", $"Sources {first.Index} and {second.Index} come from different modules ({first.Info.ModuleId}, {second.Info.ModuleId}) and can't be compared.");
        }

        CompareKind k = kind switch
        {
            null => ReferenceEquals(first.Module, second.Module) ? CompareKind.Fork : CompareKind.Versions,
            "shared" => CompareKind.Shared,
            "fork" => ReferenceEquals(first.Module, second.Module) ? CompareKind.Fork : throw new ControlException("bad_args", "Kind 'fork' needs both sources on one module; these are two copies, use 'versions'."),
            "versions" => ReferenceEquals(first.Module, second.Module) ? throw new ControlException("bad_args", "Kind 'versions' needs two loaded copies of the module; these share one.") : CompareKind.Versions,
            _ => throw new ControlException("bad_args", $"Argument 'kind' must be 'fork', 'versions' or 'shared', not '{kind}'."),
        };

        string mapName = map ?? "identity";
        EntityMap entityMap;
        switch (mapName)
        {
            case "identity":
                entityMap = EntityMap.Identity;
                break;
            case "links":
                entityMap = LinkMap(first, second);
                break;
            default:
                throw new ControlException("bad_args", $"Argument 'map' must be 'identity' or 'links', not '{mapName}'.");
        }

        return new ComparePair(first, second, k, mapName, entityMap, offset ?? ComparePair.AutoOffset(first, second, k));
    }

    /// <summary>The entity pairs the open sources' declared links make between two sources, oriented first to second.</summary>
    public EntityMap LinkMap(SourceSession first, SourceSession second)
    {
        var pairs = new List<KeyValuePair<EntityId, EntityId>>();
        foreach (SourceLink l in Links())
        {
            if (l.FromSource == first.Index && l.ToSource == second.Index)
            {
                pairs.Add(new(l.From, l.To));
            }
            else if (l.FromSource == second.Index && l.ToSource == first.Index)
            {
                pairs.Add(new(l.To, l.From));
            }
        }

        return EntityMap.FromPairs(pairs);
    }

    /// <summary>
    /// The differences between a pair's worlds at <paramref name="step"/>, at most <paramref name="limit"/> listed. The shell's cursors end where they started.
    /// </summary>
    /// <exception cref="ControlException"><c>bad_args</c> when the step is outside both recordings.</exception>
    public CompareRows CompareAt(ComparePair pair, long step, int limit)
    {
        SourceSession a = pair.First;
        SourceSession b = pair.Second;
        pair.Range(out long from, out long to);
        if (step < from || step > to)
        {
            throw new ControlException("bad_args", $"Step {step} is outside both recordings; sources {a.Index} and {b.Index} share steps {from} to {to}.");
        }

        long stepB = step + pair.Offset;
        var key = (step, a.History.EndStep, b.History.EndStep, limit);
        if (pair.CachedRows is { } cached && pair.RowsKey == key)
        {
            return cached;
        }

        bool moved = a.History.CurrentStep != step || b.History.CurrentStep != stepB;
        try
        {
            if (moved)
            {
                a.History.Seek(step);
                b.History.Seek(stepB);
            }

            CompareRows rows = CompareWorlds.Rows(a.History.World, b.History.World, pair.Kind, pair.Map, limit);
            pair.CachedRows = rows;
            pair.RowsKey = key;
            return rows;
        }
        finally
        {
            if (moved)
            {
                Seek(Step);
            }
        }
    }

    /// <summary>Selects the entity a compare row names (the first source's, else the second's).</summary>
    public void SelectRow(CompareRow row) => Select(row.Entity.IsSome ? row.Entity : row.Other);

    /// <summary>The entity to outline in a view of the pair's second source: the selection, mapped through the pair's map.</summary>
    internal EntityId PairHighlight(ViewState view)
    {
        if (EffectivePair is not { } p || !Selected.IsSome)
        {
            return EntityId.None;
        }

        if (ActiveSource == p.First.Index && view.Source == p.Second.Index)
        {
            return p.Map.TryMap(Selected, out EntityId other) ? other : EntityId.None;
        }

        // Back the other way only an identity map is known, so only then is the same id the same entity.
        return ActiveSource == p.Second.Index && view.Source == p.First.Index && p.Map.IsIdentity ? Selected : EntityId.None;
    }
}
