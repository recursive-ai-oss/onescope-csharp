namespace OneScope.Sdk;

/// <summary>
/// Identifies an entity inside one source. Ids are chosen by the module, are stable for the life of the
/// source, and are never reused for a different entity at a different step.
/// </summary>
/// <param name="Value">The raw id. Zero means "no entity".</param>
public readonly record struct EntityId(ulong Value)
{
    /// <summary>The "no entity" id (zero).</summary>
    public static EntityId None => default;

    /// <summary>True when this is not <see cref="None"/>.</summary>
    public bool IsSome => Value != 0;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
