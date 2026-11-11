using System.Net.Sockets;
using OneScope.Probe;

namespace OneScope.Attach;

/// <summary>Why a <see cref="ProbeClient"/> is no longer streaming.</summary>
public enum ClientState
{
    /// <summary>Still connected; more records may arrive.</summary>
    Streaming,

    /// <summary>The host closed its probe and sent the end frame. Every record was delivered.</summary>
    Ended,

    /// <summary>The connection closed without an end frame: the host dropped this viewer (it was too slow) or died.</summary>
    Disconnected,

    /// <summary>The bytes were malformed or over a limit; see <see cref="ProbeClient.Error"/>.</summary>
    Failed,

    /// <summary>This side closed the connection.</summary>
    Closed,
}

/// <summary>
/// One connection to a probe: the handshake, the snapshot, then records. A reader thread receives frames and queues them
/// (up to <see cref="MaxQueuedBytes"/>, after which it waits, so the host's probe sees a slow viewer and drops it). The caller takes
/// records with <see cref="Drain"/> on its own thread, so nothing here touches a world. The viewer sends one byte to the host
/// and nothing else; it cannot change the host.
/// </summary>
public sealed class ProbeClient : IDisposable
{
    private readonly Socket _socket;
    private readonly ProbeLimits _limits;
    private readonly string _input;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _queue = new();
    private long _queuedBytes;
    private ClientState _state = ClientState.Streaming;
    private ProbeFormatException? _error;

    private ProbeClient(Socket socket, string input, ProbeHandshake handshake, byte[] snapshot, ProbeLimits limits)
    {
        _socket = socket;
        _input = input;
        _limits = limits;
        Handshake = handshake;
        Snapshot = snapshot;
        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "onescope-viewer-read" };
        thread.Start();
    }

    /// <summary>The most record bytes held unread before the reader waits.</summary>
    public static long MaxQueuedBytes => 64L << 20;

    /// <summary>What the host announced.</summary>
    public ProbeHandshake Handshake { get; }

    /// <summary>The snapshot the host took when this viewer attached, in the library's snapshot format.</summary>
    public byte[] Snapshot { get; }

    /// <summary>Whether the stream is still open and, if not, why.</summary>
    public ClientState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>The error that ended the stream when <see cref="State"/> is <see cref="ClientState.Failed"/>.</summary>
    public ProbeFormatException? Error
    {
        get
        {
            lock (_lock)
            {
                return _error;
            }
        }
    }

    /// <summary>
    /// Connects, reads the handshake, asks for the stream and reads the snapshot. Records start arriving in the background.
    /// </summary>
    /// <param name="socketPath">A probe's socket, from <see cref="ProbeFinder.List"/>.</param>
    /// <param name="limits">Size limits enforced before allocating; defaults to <see cref="ProbeLimits.Default"/>.</param>
    /// <param name="timeoutMilliseconds">How long to wait for the connection, handshake and snapshot to arrive.</param>
    /// <exception cref="SocketException">Nothing listens there.</exception>
    /// <exception cref="ProbeFormatException">The peer's bytes are not a valid probe stream, or are over the limits.</exception>
    /// <exception cref="OperationCanceledException">The probe did not answer in time.</exception>
    public static ProbeClient Connect(string socketPath, ProbeLimits? limits = null, int timeoutMilliseconds = 5000)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        ProbeLimits lim = limits ?? ProbeLimits.Default;
        Socket socket = ProbeFinder.Connect(socketPath, timeoutMilliseconds);
        try
        {
            ProbeHandshake hello = ProbeFinder.ReadHandshake(socket, socketPath);
            socket.Send([ProbeWire.AttachCommand]);

            // The host takes the snapshot on its own thread at its next record, which can be later than the connect timeout
            // for a host that steps slowly, so the wait for the snapshot is the caller's timeout and no shorter.
            socket.ReceiveTimeout = timeoutMilliseconds;
            Span<byte> header = stackalloc byte[ProbeWire.FrameHeaderLength];
            ProbeFinder.ReceiveExact(socket, header, socketPath, "snapshot frame header");
            (ProbeFrameKind _, int length) = ProbeWire.ReadFrameHeader(header, lim, first: true, socketPath);
            byte[] snapshot = new byte[length];
            ProbeFinder.ReceiveExact(socket, snapshot, socketPath, "snapshot");
            socket.ReceiveTimeout = 0;
            return new ProbeClient(socket, socketPath, hello, snapshot, lim);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Moves up to <paramref name="max"/> queued records into <paramref name="into"/>, oldest first, and returns how many.
    /// Records that arrived before the stream ended are still delivered after <see cref="State"/> changes.
    /// </summary>
    public int Drain(List<byte[]> into, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(into);
        int n = 0;
        lock (_lock)
        {
            while (n < max && _queue.Count > 0)
            {
                byte[] record = _queue.Dequeue();
                _queuedBytes -= record.Length;
                into.Add(record);
                n++;
            }

            if (n > 0)
            {
                Monitor.PulseAll(_lock);
            }
        }

        return n;
    }

    /// <summary>Closes the connection. The host sees a viewer that left, and carries on.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_state == ClientState.Streaming)
            {
                _state = ClientState.Closed;
            }

            Monitor.PulseAll(_lock);
        }

        CloseInBackground(_socket);
    }

    private void ReadLoop()
    {
        byte[] header = new byte[ProbeWire.FrameHeaderLength];
        try
        {
            while (true)
            {
                if (!ReceiveHeader(header))
                {
                    Finish(ClientState.Disconnected, null);
                    return;
                }

                (ProbeFrameKind kind, int length) = ProbeWire.ReadFrameHeader(header, _limits, first: false, _input);
                if (kind == ProbeFrameKind.End)
                {
                    Finish(ClientState.Ended, null);
                    return;
                }

                byte[] record = new byte[length];
                ProbeFinder.ReceiveExact(_socket, record, _input, "record");
                lock (_lock)
                {
                    while (_state == ClientState.Streaming && _queuedBytes > 0 && _queuedBytes + length > MaxQueuedBytes)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_state != ClientState.Streaming)
                    {
                        return;
                    }

                    _queue.Enqueue(record);
                    _queuedBytes += length;
                }
            }
        }
        catch (ProbeFormatException e)
        {
            Finish(ClientState.Failed, e);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Finish(State == ClientState.Closed ? ClientState.Closed : ClientState.Disconnected, null);
        }
    }

    // False when the connection closed cleanly between frames.
    private bool ReceiveHeader(byte[] header)
    {
        int got = 0;
        while (got < header.Length)
        {
            int n = _socket.Receive(header.AsSpan(got));
            if (n == 0)
            {
                if (got == 0)
                {
                    return false;
                }

                throw new ProbeFormatException(_input, $"expected a {header.Length}-byte frame header, the connection closed after {got}.");
            }

            got += n;
        }

        return true;
    }

    private void Finish(ClientState state, ProbeFormatException? error)
    {
        lock (_lock)
        {
            if (_state == ClientState.Streaming)
            {
                _state = state;
                _error = error;
            }
        }

        CloseInBackground(_socket);
    }

    // Disposing a socket another thread is blocked on can wait for that thread on some platforms, and Dispose is called from the
    // viewer's own thread. Shut down first to wake the reader, then dispose on a pool thread.
    private static void CloseInBackground(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Already closed.
        }

        _ = ThreadPool.UnsafeQueueUserWorkItem(static s => s.Dispose(), socket, preferLocal: false);
    }
}
