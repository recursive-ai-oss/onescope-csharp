using System.Buffers;

namespace OneScope.Sdk;

/// <summary>
/// A module teaches the shell about one library: how to open its files and what its worlds look like.
/// Implementations are in the library's own repo, built against this SDK.
/// </summary>
public interface IScopeModule
{
    /// <summary>Stable id such as "onephysics". Appears in <see cref="SourceInfo.ModuleId"/>.</summary>
    string Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>Opens a scene, snapshot or replay.</summary>
    /// <param name="data">The file's bytes. Untrusted: check every length before allocating from it.</param>
    /// <param name="name">A display name for the source, usually the file name.</param>
    /// <exception cref="ScopeFormatException">The bytes are not a valid input; the message names the input and what was expected.</exception>
    IScopeSource Open(ReadOnlyMemory<byte> data, string name);
}

/// <summary>A recording opened by a module: its declarations, its inputs and a way to make worlds from them.</summary>
public interface IScopeSource
{
    /// <summary>What the source declares.</summary>
    SourceInfo Info { get; }

    /// <summary>The setup and per-step inputs. Worlds replay from these.</summary>
    Recording Recording { get; }

    /// <summary>Creates a fresh world at step 0, built from <see cref="Recording"/>'s setup.</summary>
    IScopeWorld CreateWorld();

    /// <summary>
    /// Returns a source with the same setup and module but this recording. Used for forks; it must not change the original.
    /// </summary>
    IScopeSource WithRecording(Recording recording);

    /// <summary>Writes <see cref="Recording"/> in the module's replay format. Reading it back with <see cref="IScopeModule.Open"/> and writing again gives the same bytes.</summary>
    void WriteReplay(IBufferWriter<byte> writer);
}

/// <summary>
/// A deterministic world owned by a module. The same snapshot and the same inputs always give the same
/// <see cref="Hash"/>, whatever path led there. Worlds are used by one thread at a time.
/// </summary>
public interface IScopeWorld
{
    /// <summary>The step index the world is at. A new world is at 0 and each <see cref="Advance"/> adds one.</summary>
    long Step { get; }

    /// <summary>A 64-bit hash of the whole simulated state, including <see cref="Step"/>.</summary>
    ulong Hash();

    /// <summary>
    /// Runs one step with this step's input and adds one to <see cref="Step"/>. Events, series samples and findings
    /// the step produces go to <paramref name="sink"/>. The input is the concatenation of encoded pokes (see <see cref="EncodePoke"/>)
    /// and is empty on steps without pokes.
    /// </summary>
    /// <exception cref="ScopeFormatException">The input is malformed.</exception>
    void Advance(ReadOnlySpan<byte> input, TimelineSink sink);

    /// <summary>
    /// Runs only the first <paramref name="position"/> parts of the step that <see cref="Advance"/> would run, along a non-time axis,
    /// leaving <see cref="Step"/> unchanged. Called on a scratch copy, never on the history's own world.
    /// Position 0 does nothing.
    /// </summary>
    /// <param name="axis">An <see cref="AxisInfo.Id"/> other than <see cref="AxisIds.Time"/>.</param>
    /// <param name="position">1 to the axis's <see cref="AxisInfo.PositionCount"/>.</param>
    /// <param name="input">The step's input, as for <see cref="Advance"/>.</param>
    void AdvanceAxis(string axis, int position, ReadOnlySpan<byte> input);

    /// <summary>Writes the complete state so <see cref="Restore"/> can rebuild it exactly.</summary>
    void WriteSnapshot(IBufferWriter<byte> writer);

    /// <summary>Replaces the whole state, including <see cref="Step"/>, from a snapshot this module wrote.</summary>
    /// <exception cref="ScopeFormatException">The snapshot is malformed.</exception>
    void Restore(ReadOnlySpan<byte> snapshot);

    /// <summary>Clears <paramref name="into"/> and fills it with the entity tree, parents before children.</summary>
    void CollectEntities(List<EntityInfo> into);

    /// <summary>Clears <paramref name="into"/> and fills it with the entity's properties. Returns false when the entity does not exist.</summary>
    bool Inspect(EntityId entity, PropertyList into);

    /// <summary>Adds the world's primitives for the overlays in <paramref name="request"/> to <paramref name="into"/> (which the caller has cleared).</summary>
    void Draw(DrawList into, in DrawRequest request);

    /// <summary>
    /// Validates a poke against the current state and appends its encoding to <paramref name="into"/>.
    /// The encoding must be self-delimiting, because a step's input is the concatenation of encodings.
    /// Does not change the world.
    /// </summary>
    /// <exception cref="ArgumentException">The poke is unknown, has the wrong arguments or names an entity that does not exist.</exception>
    void EncodePoke(in PokeCall call, IBufferWriter<byte> into);

    /// <summary>
    /// Finds the first entity and field that differ from <paramref name="other"/>, or null when the worlds are equal.
    /// Order is the module's, but must be the same for the same pair of worlds.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="other"/> is from a different module.</exception>
    Difference? FirstDifference(IScopeWorld other);
}
