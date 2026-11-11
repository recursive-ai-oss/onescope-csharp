using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace OneScope.Probe;

/// <summary>Lets the probe take a snapshot of the host's world when a viewer attaches.</summary>
public interface ISnapshotSource
{
    /// <summary>
    /// Writes the world's complete state in the library's snapshot format. Called on the host's own thread, from inside a
    /// <see cref="ScopeProbe.Record"/> call, only after a viewer has asked to attach. The world is exactly as it is after the
    /// record passed to that call was applied.
    /// </summary>
    void WriteSnapshot(IBufferWriter<byte> writer);
}

/// <summary>What a viewer is told about the host's library when it attaches.</summary>
/// <param name="ModuleId">The id of the viewer module that understands the stream, such as "onephysics". 1 to 256 UTF-8 bytes.</param>
/// <param name="LibraryVersion">The library's version string. At most 256 UTF-8 bytes.</param>
/// <param name="FormatVersion">The version of the library's replay format the records use.</param>
public readonly record struct ProbeInfo(string ModuleId, string LibraryVersion, int FormatVersion);

/// <summary>
/// The handle a host program keeps to stream its library's replay records to a viewer. Nothing happens until the host
/// creates one with <see cref="Attach(ProbeInfo, ISnapshotSource, ProbeOptions?)"/>, and nothing is sent until a viewer asks.
/// While nobody is attached, <see cref="Record"/> is one branch. It never blocks on a socket, never throws into the host,
/// and never buffers more than <see cref="ProbeOptions.MaxBufferedBytes"/>.
/// </summary>
/// <remarks>
/// Call <see cref="Record"/> from one thread (the thread that runs the world) after each record has been applied to the world.
/// The first call after a viewer asks to attach takes the snapshot, so the snapshot already contains that call's record; it is
/// not sent again. Later records follow in order. One viewer streams at a time; a second is refused while one is attached.
/// A viewer that disconnects, stalls or falls behind is dropped, and attaching again starts from a fresh snapshot.
/// The probe listens on a Unix-domain socket in <see cref="ProbeDirectory"/>. Where sockets are unavailable (a browser) it stays inert.
/// </remarks>
public sealed class ScopeProbe : IDisposable
{
    private const int MaxHandlers = 8;
    private const int HandshakeTimeoutMilliseconds = 2000;
    private static int s_serial;

    private readonly ProbeOptions _options;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _socketPath;
    private readonly Socket? _listener;
    private int _state;
    private int _disposed;
    private int _handlers;
    private int _dropped;
    private ViewerConnection? _pending;
    private ViewerConnection? _active;
    private string? _lastDrop;

    private ScopeProbe(ProbeInfo info, ISnapshotSource snapshots, ProbeOptions options)
    {
        Info = info;
        Snapshots = snapshots;
        _options = options;
        if (options.Listen)
        {
            (_listener, _socketPath) = TryListen(options);
        }

        if (_listener is not null)
        {
            Socket listener = _listener;
            CancellationToken token = _stop.Token;
            _ = Task.Run(() => AcceptLoopAsync(listener, token));
        }
    }

    /// <summary>What viewers are told on attaching.</summary>
    public ProbeInfo Info { get; }

    /// <summary>The source of the snapshot sent on attaching.</summary>
    public ISnapshotSource Snapshots { get; }

    /// <summary>True while a viewer is streaming (its snapshot has been taken and it has not been dropped).</summary>
    public bool IsConnected => Volatile.Read(ref _active) is { } v && !v.IsDead;

    /// <summary>True when the probe is listening; false when it was created with <see cref="ProbeOptions.Listen"/> false or sockets were unavailable.</summary>
    public bool IsListening => _listener is not null;

    /// <summary>The socket's path while listening, or null.</summary>
    public string? SocketPath => _socketPath;

    /// <summary>How many viewers have been dropped for stalling, falling behind or disconnecting.</summary>
    public int DroppedViewers => Volatile.Read(ref _dropped);

    /// <summary>Why the most recent viewer was dropped, or null if none was.</summary>
    public string? LastDropReason
    {
        get
        {
            lock (_gate)
            {
                return _lastDrop;
            }
        }
    }

    /// <summary>Makes a probe for a host world, listening in the default directory. The host calls this itself; the library never does.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The module id is empty, or a string in <paramref name="info"/> is too long.</exception>
    public static ScopeProbe Attach(ProbeInfo info, ISnapshotSource snapshots) => Attach(info, snapshots, null);

    /// <summary>Makes a probe for a host world. If the socket cannot be created the probe is inert (<see cref="IsListening"/> is false) and the host is not told.</summary>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The module id is empty, or a string in <paramref name="info"/> is too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is below its minimum.</exception>
    public static ScopeProbe Attach(ProbeInfo info, ISnapshotSource snapshots, ProbeOptions? options)
    {
        ArgumentNullException.ThrowIfNull(info.ModuleId);
        ArgumentNullException.ThrowIfNull(info.LibraryVersion);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentException.ThrowIfNullOrEmpty(info.ModuleId);
        _ = ProbeWire.HandshakeLength(info);
        options ??= new ProbeOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBufferedBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSnapshotBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SendTimeoutMilliseconds, 100);
        return new ScopeProbe(info, snapshots, options);
    }

    /// <summary>
    /// Offers one replay record (one world API call, or one step, in the library's replay format), after the host applied it.
    /// With no viewer attached this is a single branch and the bytes are not touched. With one, the bytes are copied into a
    /// buffer the viewer's own thread drains; a viewer that falls behind is dropped, so the host is never slowed.
    /// A record over 16 MiB drops the viewer too, because the viewer's reader would refuse it.
    /// </summary>
    public void Record(ReadOnlySpan<byte> record)
    {
        if (Volatile.Read(ref _state) == 0)
        {
            return;
        }

        RecordConnected(record, send: true);
    }

    /// <summary>
    /// Lets a waiting viewer attach without a record: if one has asked, takes the snapshot now, with the world as it is.
    /// Hosts call it at a quiet point such as before the first step, so a viewer can attach at step 0 without waiting for a record.
    /// With no viewer waiting this is a single branch.
    /// </summary>
    public void Poll()
    {
        if (Volatile.Read(ref _state) == 0)
        {
            return;
        }

        RecordConnected(default, send: false);
    }

    /// <summary>Stops listening, removes the socket file, and tells any viewer the stream has ended. Does not wait. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ViewerConnection? active;
        ViewerConnection? pending;
        lock (_gate)
        {
            active = _active;
            pending = _pending;
            _active = null;
            _pending = null;
            Volatile.Write(ref _state, 0);
        }

        _stop.Cancel();
        if (_listener is not null)
        {
            SocketCloser.CloseInBackground(_listener);
        }

        if (_socketPath is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
                // A stale file is cleaned by the next listing.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        pending?.Kill("the probe was disposed.");
        active?.Finish();
    }

    // Kept out of line so the unconnected path in Record stays a load and a branch.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordConnected(ReadOnlySpan<byte> record, bool send)
    {
        // The probe must never throw into the host; any failure drops the viewer instead.
        try
        {
            ViewerConnection? active = Volatile.Read(ref _active);
            if (active is not null)
            {
                if (send)
                {
                    _ = active.Enqueue(record);
                }

                return;
            }

            ViewerConnection? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }

            if (pending is not null)
            {
                StartViewer(pending);
            }
        }
        catch (Exception e)
        {
            Drop(null, "the probe failed: " + e.GetType().Name);
        }
    }

    private void StartViewer(ViewerConnection viewer)
    {
        var writer = new SnapshotFrameWriter(_options.MaxSnapshotBytes);
        try
        {
            Snapshots.WriteSnapshot(writer);
            byte[] frame = writer.Take(out int length);
            lock (_gate)
            {
                if (_disposed == 0)
                {
                    _active = viewer;
                }
            }

            viewer.Begin(frame, length);
        }
        catch (Exception e)
        {
            writer.Release();
            viewer.Kill("the snapshot could not be taken: " + e.GetType().Name);
        }
    }

    private void Drop(ViewerConnection? viewer, string reason)
    {
        ViewerConnection? target;
        lock (_gate)
        {
            target = viewer ?? _active;
        }

        target?.Kill(reason);
    }

    private void OnViewerGone(ViewerConnection viewer, string reason)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, viewer))
            {
                _active = null;
            }

            if (ReferenceEquals(_pending, viewer))
            {
                _pending = null;
            }

            _lastDrop = reason;
            Volatile.Write(ref _state, _active is not null || _pending is not null ? 1 : 0);
        }

        _ = Interlocked.Increment(ref _dropped);
    }

    private bool TryOffer(ViewerConnection viewer)
    {
        lock (_gate)
        {
            if (_disposed != 0 || _active is not null || _pending is not null)
            {
                return false;
            }

            _pending = viewer;
            Volatile.Write(ref _state, 1);
            return true;
        }
    }

    private static (Socket? Listener, string? Path) TryListen(ProbeOptions options)
    {
        Socket? socket = null;
        try
        {
            string directory = options.Directory ?? ProbeDirectory.Default();
            ProbeDirectory.Prepare(directory);
            string path = Path.Combine(directory, $"p{Environment.ProcessId}-{Interlocked.Increment(ref s_serial)}{ProbeDirectory.SocketExtension}");

            // Our own pid and serial name this file, so anything already there is left over from a dead process with the same pid.
            File.Delete(path);
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            socket.Listen(8);
            return (socket, path);
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or NotSupportedException or ArgumentException)
        {
            socket?.Dispose();
            return (null, null);
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                // A transient failure such as running out of file handles; wait a moment instead of spinning.
                try
                {
                    await Task.Delay(100, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (Interlocked.Increment(ref _handlers) > MaxHandlers)
            {
                _ = Interlocked.Decrement(ref _handlers);
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => Handle(client), CancellationToken.None);
        }
    }

    private void Handle(Socket client)
    {
        bool handedOver = false;
        try
        {
            client.SendTimeout = HandshakeTimeoutMilliseconds;
            client.ReceiveTimeout = HandshakeTimeoutMilliseconds;
            Span<byte> hello = stackalloc byte[ProbeWire.MaxHandshakeLength];
            int n = ProbeWire.WriteHandshake(hello, Info, Environment.ProcessId);
            int sent = 0;
            while (sent < n)
            {
                sent += client.Send(hello[sent..n]);
            }

            // The one byte a viewer sends. A listing closes without sending it and costs the host nothing.
            Span<byte> command = stackalloc byte[1];
            if (client.Receive(command) == 1 && command[0] == ProbeWire.AttachCommand)
            {
                var viewer = new ViewerConnection(client, _options.MaxBufferedBytes, _options.SendTimeoutMilliseconds, OnViewerGone);
                handedOver = TryOffer(viewer);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The client went away or sent nothing in time.
        }
        finally
        {
            _ = Interlocked.Decrement(ref _handlers);
            if (!handedOver)
            {
                client.Dispose();
            }
        }
    }
}
