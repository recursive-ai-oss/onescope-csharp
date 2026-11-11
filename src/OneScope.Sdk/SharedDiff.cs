using System.Collections.Frozen;

namespace OneScope.Sdk;

/// <summary>
/// Says which entity of one world is which entity of another. Identity by default; or pairs, for example from declared links.
/// Used to compare worlds that hold different sets of entities.
/// </summary>
public sealed class EntityMap
{
    private readonly FrozenDictionary<ulong, EntityId>? _pairs;

    private EntityMap(FrozenDictionary<ulong, EntityId>? pairs) => _pairs = pairs;

    /// <summary>Every entity maps to the entity with the same id.</summary>
    public static EntityMap Identity { get; } = new(null);

    /// <summary>True for <see cref="Identity"/>.</summary>
    public bool IsIdentity => _pairs is null;

    /// <summary>The number of explicit pairs (zero for <see cref="Identity"/>).</summary>
    public int PairCount => _pairs?.Count ?? 0;

    /// <summary>A map from explicit pairs. Entities that are not listed map to nothing (they are not shared).</summary>
    /// <exception cref="ArgumentException">A left entity is listed twice, or an id is <see cref="EntityId.None"/>.</exception>
    public static EntityMap FromPairs(IEnumerable<KeyValuePair<EntityId, EntityId>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var map = new Dictionary<ulong, EntityId>();
        foreach (KeyValuePair<EntityId, EntityId> p in pairs)
        {
            if (!p.Key.IsSome || !p.Value.IsSome)
            {
                throw new ArgumentException("An entity map pair names EntityId.None.", nameof(pairs));
            }

            if (!map.TryAdd(p.Key.Value, p.Value))
            {
                throw new ArgumentException($"Entity {p.Key} is mapped twice.", nameof(pairs));
            }
        }

        return new EntityMap(map.ToFrozenDictionary());
    }

    /// <summary>Finds the right-hand entity for a left-hand one.</summary>
    /// <returns>False when the map has no counterpart for <paramref name="left"/>.</returns>
    public bool TryMap(EntityId left, out EntityId right)
    {
        if (_pairs is null)
        {
            right = left;
            return true;
        }

        return _pairs.TryGetValue(left.Value, out right);
    }
}

/// <summary>The result of comparing the entities two worlds share.</summary>
/// <param name="First">The first shared entity and field that differ, or null when everything shared is equal.</param>
/// <param name="Shared">How many entities both worlds hold (roots are not counted).</param>
/// <param name="OnlyLeft">Entities only the first world holds (or the map does not pair); they were not compared.</param>
/// <param name="OnlyRight">Entities only the second world holds; they were not compared.</param>
public readonly record struct SharedDifference(Difference? First, int Shared, int OnlyLeft, int OnlyRight);

/// <summary>The comparison <see cref="IScopeWorld.CompareShared"/> uses unless a module supplies a faster one.</summary>
public static class SharedDiff
{
    /// <summary>
    /// Compares every entity both worlds hold, by the rows the inspector shows for it. Root entities (no parent) are left out, because a root
    /// usually aggregates the whole world (a body count) and a partial world differs there by design.
    /// Entities held by only one world are counted and skipped. Rows are matched by group and name; a row one side lacks is a difference.
    /// </summary>
    /// <param name="left">The first world.</param>
    /// <param name="right">The second world, from the same module.</param>
    /// <param name="map">Which right entity matches each left entity.</param>
    /// <returns>The counts, and the first difference in the left world's entity order.</returns>
    public static SharedDifference Compare(IScopeWorld left, IScopeWorld right, EntityMap map)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(map);

        var leftEntities = new List<EntityInfo>();
        var rightEntities = new List<EntityInfo>();
        left.CollectEntities(leftEntities);
        right.CollectEntities(rightEntities);

        var rightIds = new HashSet<ulong>();
        foreach (EntityInfo e in rightEntities)
        {
            if (e.Parent.IsSome)
            {
                rightIds.Add(e.Id.Value);
            }
        }

        var leftRows = new PropertyList();
        var rightRows = new PropertyList();
        var rightByName = new Dictionary<(string, string), ScopeValue>();
        var matched = new HashSet<ulong>();
        Difference? first = null;
        int shared = 0;
        int onlyLeft = 0;
        foreach (EntityInfo e in leftEntities)
        {
            if (!e.Parent.IsSome)
            {
                continue;
            }

            if (!map.TryMap(e.Id, out EntityId r) || !rightIds.Contains(r.Value) || !matched.Add(r.Value))
            {
                onlyLeft++;
                continue;
            }

            shared++;
            if (first is not null)
            {
                continue;
            }

            bool inLeft = left.Inspect(e.Id, leftRows);
            bool inRight = right.Inspect(r, rightRows);
            if (!inLeft || !inRight)
            {
                first = new Difference(e.Id, "inspect", inLeft ? "present" : "missing", inRight ? "present" : "missing");
                continue;
            }

            rightByName.Clear();
            foreach (Property p in rightRows.Rows)
            {
                rightByName[(p.Group, p.Name)] = p.Value;
            }

            foreach (Property p in leftRows.Rows)
            {
                if (!rightByName.Remove((p.Group, p.Name), out ScopeValue other))
                {
                    first = new Difference(e.Id, p.Name, p.Value.ToString(), "(missing)");
                    break;
                }

                if (p.Value != other)
                {
                    first = new Difference(e.Id, p.Name, p.Value.ToString(), other.ToString());
                    break;
                }
            }

            if (first is null)
            {
                foreach (KeyValuePair<(string, string), ScopeValue> extra in rightByName)
                {
                    first = new Difference(e.Id, extra.Key.Item2, "(missing)", extra.Value.ToString());
                    break;
                }
            }
        }

        return new SharedDifference(first, shared, onlyLeft, rightIds.Count - matched.Count);
    }
}
