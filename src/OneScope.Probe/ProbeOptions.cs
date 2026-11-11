namespace OneScope.Probe;

/// <summary>Settings for a <see cref="ScopeProbe"/>. The defaults suit a game or test host.</summary>
public sealed class ProbeOptions
{
    /// <summary>Where the socket goes. Null means <see cref="ProbeDirectory.Default"/>.</summary>
    public string? Directory { get; init; }

    /// <summary>False makes a probe that never listens (nothing can attach). Defaults to true.</summary>
    public bool Listen { get; init; } = true;

    /// <summary>
    /// The most record bytes (headers included) buffered for a viewer that has not read them yet. A viewer that falls this far
    /// behind is disconnected. At least 1024; default 16 MiB.
    /// </summary>
    public int MaxBufferedBytes { get; init; } = 16 << 20;

    /// <summary>The largest snapshot the probe will send. A larger one drops the viewer. At least 1024; default 64 MiB.</summary>
    public int MaxSnapshotBytes { get; init; } = 64 << 20;

    /// <summary>
    /// How long one socket write may take before the viewer is treated as stalled and disconnected, in milliseconds.
    /// It is a socket timeout on the probe's own thread; it never affects the host's thread or the simulation. At least 100; default 5000.
    /// </summary>
    public int SendTimeoutMilliseconds { get; init; } = 5000;
}
