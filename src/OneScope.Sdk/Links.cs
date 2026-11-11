namespace OneScope.Sdk;

/// <summary>Which way an <see cref="EntityLink"/> points along the entity's life.</summary>
public enum LinkDirection : byte
{
    /// <summary>The entity continues an entity of another source: it is what the other entity became (the arrival side of a transfer).</summary>
    Continues = 0,

    /// <summary>Another source's entity continues this one: this entity became it (the departure side of a transfer).</summary>
    ContinuedBy,
}

/// <summary>
/// A declaration that an entity is the same thing as an entity of another source, from a step on: a body handed from one world to
/// another, an agent crossing a portal. Both worlds may declare the same transfer (one as <see cref="LinkDirection.ContinuedBy"/>, the other as
/// <see cref="LinkDirection.Continues"/>); declaring it from one side is enough. Before <paramref name="Step"/> the entity
/// lives in the departure source, from <paramref name="Step"/> on in the arrival source, and the shell moves a selection
/// and a tracking camera across when the shared cursor crosses it.
/// </summary>
/// <param name="Entity">The entity of the world that declares the link.</param>
/// <param name="Direction">Whether this side is the arrival or the departure.</param>
/// <param name="Source">The <see cref="SourceInfo.LinkKey"/> of the other source.</param>
/// <param name="Other">The entity in the other source.</param>
/// <param name="Step">The first step at which the entity lives in the arrival source. Time is the shared step axis.</param>
public readonly record struct EntityLink(EntityId Entity, LinkDirection Direction, string Source, EntityId Other, long Step);
