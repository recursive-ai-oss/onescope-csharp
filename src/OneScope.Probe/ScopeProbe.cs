using System.Buffers;

namespace OneScope.Probe;

/// <summary>What a viewer is told about the host's library when it attaches.</summary>
/// <param name="ModuleId">The id of the viewer module that understands the stream, such as "onephysics".</param>
/// <param name="LibraryVersion">The library's version string.</param>
/// <param name="FormatVersion">The version of the library's replay format the records use.</param>
public readonly record struct ProbeInfo(string ModuleId, string LibraryVersion, int FormatVersion);

/// <summary>Lets the probe take a snapshot of the host's world when a viewer attaches.</summary>
public interface ISnapshotSource
{
    /// <summary>
    /// Writes the world's complete state in the library's snapshot format. Called on the host's thread, at a point the host chose
    /// to allow it, only after a viewer has connected.
    /// </summary>
    void WriteSnapshot(IBufferWriter<byte> writer);
}

/// <summary>
/// The handle a host program keeps to stream its library's replay records to a viewer. Nothing happens until the host
/// creates one with <see cref="Attach"/>, and nothing is sent until a viewer connects. While nobody is connected,
/// <see cref="Record"/> is one branch. It never blocks, never throws into the host, and never grows without bound.
/// </summary>
/// <remarks>This version has no transport: no viewer can connect yet, so <see cref="IsConnected"/> stays false.</remarks>
public sealed class ScopeProbe : IDisposable
{
    private int _connected;
    private int _disposed;

    private ScopeProbe(ProbeInfo info, ISnapshotSource snapshots)
    {
        Info = info;
        Snapshots = snapshots;
    }

    /// <summary>What viewers are told on attaching.</summary>
    public ProbeInfo Info { get; }

    /// <summary>The source of the snapshot sent on attaching.</summary>
    public ISnapshotSource Snapshots { get; }

    /// <summary>True while a viewer is connected.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <summary>Makes a probe for a host world. The host calls this itself; the library never does.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ScopeProbe Attach(ProbeInfo info, ISnapshotSource snapshots)
    {
        ArgumentNullException.ThrowIfNull(info.ModuleId);
        ArgumentNullException.ThrowIfNull(info.LibraryVersion);
        ArgumentNullException.ThrowIfNull(snapshots);
        return new ScopeProbe(info, snapshots);
    }

    /// <summary>
    /// Offers one replay record (one world API call, or one step, in the library's replay format). With no viewer connected this
    /// is a single branch and the bytes are not touched. With one, the bytes are copied into a pooled buffer and the viewer is
    /// dropped if it falls behind; the host is never slowed.
    /// </summary>
    public void Record(ReadOnlySpan<byte> record)
    {
        if (Volatile.Read(ref _connected) == 0)
        {
            return;
        }

        RecordConnected(record);
    }

    /// <summary>Stops streaming and disconnects any viewer. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _connected, 0);
        }
    }

    // Kept out of line so the unconnected path in Record stays a load and a branch.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RecordConnected(ReadOnlySpan<byte> record)
    {
        // The transport arrives with the socket; until then nothing can set _connected.
        _ = record;
    }
}
