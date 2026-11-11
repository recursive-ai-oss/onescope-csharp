namespace OneScope.Sdk;

/// <summary>
/// A module that can also open a live stream from a probe: a snapshot taken at some step, then the library's replay records.
/// Implemented by the same class as <see cref="IScopeModule"/>, so one module serves files and live attaches.
/// </summary>
public interface ILiveModule : IScopeModule
{
    /// <summary>
    /// Opens a live stream. The returned source's <see cref="Recording.StartStep"/> is the host's step at the snapshot, and its world
    /// starts there with <see cref="IScopeWorld.Step"/> equal to it, so the viewer's step numbers are the host's and hashes
    /// compare by step number.
    /// </summary>
    /// <param name="libraryVersion">The library version the host announced.</param>
    /// <param name="formatVersion">The replay-format version the host announced.</param>
    /// <param name="snapshot">The host's snapshot. Untrusted: check every length before allocating from it.</param>
    /// <param name="name">A display name for the source.</param>
    /// <exception cref="ScopeFormatException">The snapshot or versions are not ones the module can read.</exception>
    ILiveSource OpenLive(string libraryVersion, int formatVersion, ReadOnlyMemory<byte> snapshot, string name);
}

/// <summary>A source that grows as the host's records arrive.</summary>
public interface ILiveSource
{
    /// <summary>
    /// The source. Its <see cref="IScopeSource.Recording"/> must be writable and gains one step per step the host ran; the
    /// <see cref="History"/> built on it sees new steps as they are appended.
    /// </summary>
    IScopeSource Source { get; }

    /// <summary>
    /// Adds one replay record, exactly the bytes the host passed to its probe. May add zero steps (a world API call that
    /// belongs to the next step) or one. Called on the thread that owns the history.
    /// </summary>
    /// <exception cref="ScopeFormatException">The record is malformed.</exception>
    void AppendRecord(ReadOnlySpan<byte> record);
}
