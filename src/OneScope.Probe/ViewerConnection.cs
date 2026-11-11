using System.Buffers;
using System.Net.Sockets;

namespace OneScope.Probe;

/// <summary>
/// One viewer's side of a stream. The host's thread only copies frames into a bounded buffer; a dedicated thread does all
/// socket writes. A viewer that stops reading lets the buffer fill, and is then closed, so the host is never slowed.
/// </summary>
internal sealed class ViewerConnection
{
    private readonly Socket _socket;
    private readonly int _cap;
    private readonly object _lock = new();
    private readonly Action<ViewerConnection, string> _onGone;
    private byte[] _front = ArrayPool<byte>.Shared.Rent(4096);
    private byte[] _back = ArrayPool<byte>.Shared.Rent(4096);
    private int _frontLength;
    private bool _dead;
    private bool _finish;
    private byte[]? _snapshot;
    private int _snapshotLength;

    public ViewerConnection(Socket socket, int maxBufferedBytes, int sendTimeoutMilliseconds, Action<ViewerConnection, string> onGone)
    {
        _socket = socket;
        _cap = maxBufferedBytes;
        _onGone = onGone;
        socket.SendTimeout = sendTimeoutMilliseconds;
        socket.ReceiveTimeout = 0;
    }

    public bool IsDead
    {
        get
        {
            lock (_lock)
            {
                return _dead;
            }
        }
    }

    /// <summary>Hands over the snapshot frame (header included, from a pooled array) and starts the sending thread.</summary>
    public void Begin(byte[] snapshotFrame, int length)
    {
        lock (_lock)
        {
            if (_dead)
            {
                ArrayPool<byte>.Shared.Return(snapshotFrame);
                return;
            }

            _snapshot = snapshotFrame;
            _snapshotLength = length;
        }

        var thread = new Thread(Run) { IsBackground = true, Name = "onescope-probe-send" };
        thread.Start();
    }

    /// <summary>Copies a record frame into the buffer. Returns false, and closes the viewer, when the buffer is full.</summary>
    public bool Enqueue(ReadOnlySpan<byte> record)
    {
        lock (_lock)
        {
            if (_dead)
            {
                return false;
            }

            int needed = ProbeWire.FrameHeaderLength + record.Length;
            if ((long)_frontLength + needed > _cap)
            {
                goto overflow;
            }

            bool wasEmpty = _frontLength == 0;
            Grow(ref _front, _frontLength, _frontLength + needed);
            ProbeWire.WriteFrameHeader(_front.AsSpan(_frontLength), ProbeFrameKind.Record, record.Length);
            record.CopyTo(_front.AsSpan(_frontLength + ProbeWire.FrameHeaderLength));
            _frontLength += needed;

            // The sender only waits while the buffer is empty, so only the first frame after it drained needs to wake it.
            if (wasEmpty)
            {
                Monitor.Pulse(_lock);
            }

            return true;
        }

        overflow:
        Kill("the viewer fell behind: its buffer reached the limit.");
        return false;
    }

    /// <summary>Sends what is queued, then an end frame, then closes. Does not wait.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            _finish = true;
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Closes the socket now and wakes the sending thread. Safe to call from any thread, any number of times.</summary>
    public void Kill(string reason)
    {
        bool first;
        lock (_lock)
        {
            first = !_dead;
            _dead = true;
            Monitor.Pulse(_lock);
        }

        if (!first)
        {
            return;
        }

        SocketCloser.CloseInBackground(_socket);
        _onGone(this, reason);
    }

    private void Run()
    {
        try
        {
            byte[]? snapshot;
            int snapshotLength;
            lock (_lock)
            {
                snapshot = _snapshot;
                snapshotLength = _snapshotLength;
                _snapshot = null;
            }

            if (snapshot is not null)
            {
                try
                {
                    SendAll(snapshot, snapshotLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(snapshot);
                }
            }

            while (true)
            {
                int length;
                bool finish;
                lock (_lock)
                {
                    while (!_dead && _frontLength == 0 && !_finish)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_dead)
                    {
                        return;
                    }

                    (_front, _back) = (_back, _front);
                    length = _frontLength;
                    _frontLength = 0;
                    finish = _finish;
                }

                if (length > 0)
                {
                    SendAll(_back, length);
                }

                if (finish)
                {
                    Span<byte> end = stackalloc byte[ProbeWire.FrameHeaderLength];
                    ProbeWire.WriteFrameHeader(end, ProbeFrameKind.End, 0);
                    SendAll(end);
                    Kill("the probe was disposed.");
                    return;
                }
            }
        }
        catch (SocketException e)
        {
            Kill("the viewer stopped reading or disconnected: " + e.SocketErrorCode);
        }
        catch (ObjectDisposedException)
        {
            Kill("the connection was closed.");
        }
        finally
        {
            lock (_lock)
            {
                _dead = true;
            }

            ArrayPool<byte>.Shared.Return(_front);
            ArrayPool<byte>.Shared.Return(_back);
            _front = [];
            _back = [];
        }
    }

    private void SendAll(byte[] buffer, int length) => SendAll(buffer.AsSpan(0, length));

    private void SendAll(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int n = _socket.Send(data, SocketFlags.None);
            data = data[n..];
        }
    }

    private static void Grow(ref byte[] buffer, int used, int needed)
    {
        if (needed <= buffer.Length)
        {
            return;
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buffer.Length * 2));
        buffer.AsSpan(0, used).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = bigger;
    }
}
