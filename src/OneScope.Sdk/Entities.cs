namespace OneScope.Sdk;

/// <summary>One node of the entity tree.</summary>
/// <param name="Id">The entity's id.</param>
/// <param name="Parent">The parent entity, or <see cref="EntityId.None"/> for a root.</param>
/// <param name="Kind">An <see cref="EntityKindInfo.Id"/> the source declared.</param>
/// <param name="Name">Display name.</param>
public readonly record struct EntityInfo(EntityId Id, EntityId Parent, string Kind, string Name);

/// <summary>One inspector row.</summary>
/// <param name="Group">Section heading, such as "Transform".</param>
/// <param name="Name">Row name.</param>
/// <param name="Value">The value.</param>
public readonly record struct Property(string Group, string Name, ScopeValue Value);

/// <summary>A reusable list of inspector rows a module fills for one entity.</summary>
public sealed class PropertyList
{
    /// <summary>The most rows one entity may show; further rows are dropped.</summary>
    public const int MaxRows = 4096;

    private readonly List<Property> _rows = [];

    /// <summary>The rows, in the order added.</summary>
    public IReadOnlyList<Property> Rows => _rows;

    /// <summary>Removes all rows.</summary>
    public void Clear() => _rows.Clear();

    /// <summary>Adds a row. Rows past <see cref="MaxRows"/> are dropped.</summary>
    public void Add(string group, string name, ScopeValue value)
    {
        if (_rows.Count < MaxRows)
        {
            _rows.Add(new Property(group, name, value));
        }
    }
}

/// <summary>The first difference between two worlds.</summary>
/// <param name="Entity">The entity that differs. A world-level field uses the module's root entity.</param>
/// <param name="Field">The field name, as the inspector shows it.</param>
/// <param name="Left">The value in the world <c>FirstDifference</c> was called on, formatted.</param>
/// <param name="Right">The value in the other world, formatted.</param>
public readonly record struct Difference(EntityId Entity, string Field, string Left, string Right);

/// <summary>What a view asks a world to draw.</summary>
/// <param name="Overlays">Bit n is set when overlay id n is on.</param>
public readonly record struct DrawRequest(ulong Overlays)
{
    /// <summary>True when the overlay with this id (0 to 63) is on.</summary>
    public bool IsOn(int overlay) => (uint)overlay < 64 && (Overlays & (1UL << overlay)) != 0;
}

/// <summary>A poke being applied to a fork.</summary>
/// <param name="Poke">The <see cref="PokeInfo.Id"/>.</param>
/// <param name="Args">One value per declared parameter, in order.</param>
public readonly record struct PokeCall(string Poke, ScopeValue[] Args);
