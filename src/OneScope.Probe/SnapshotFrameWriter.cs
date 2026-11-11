using System.Buffers;

namespace OneScope.Probe;

/// <summary>
/// Collects a snapshot frame in a pooled array: five bytes reserved for the frame header, then the library's snapshot bytes.
/// Refuses to grow past the limit so a runaway snapshot cannot exhaust the host's memory.
/// </summary>
internal sealed class SnapshotFrameWriter : IBufferWriter<byte>
{
    private readonly int _max;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _written = ProbeWire.FrameHeaderLength;

    public SnapshotFrameWriter(int maxSnapshotBytes)
    {
        _max = maxSnapshotBytes;
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Advanced past the span that was handed out.");
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Finishes the frame header and hands the array to the caller, who returns it to the pool.</summary>
    public byte[] Take(out int length)
    {
        if (_written - ProbeWire.FrameHeaderLength > _max)
        {
            throw new InvalidOperationException($"The snapshot is larger than the limit of {_max} bytes.");
        }

        ProbeWire.WriteFrameHeader(_buffer, ProbeFrameKind.Snapshot, _written - ProbeWire.FrameHeaderLength);
        byte[] result = _buffer;
        length = _written;
        _buffer = [];
        return result;
    }

    public void Release()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }

    private void Ensure(int sizeHint)
    {
        int free = _buffer.Length - _written;
        int need = Math.Max(sizeHint, 1);
        if (free >= need)
        {
            return;
        }

        long target = (long)_written + Math.Max(need, _buffer.Length);
        if (target - ProbeWire.FrameHeaderLength > _max)
        {
            target = (long)_written + need;
            if (target - ProbeWire.FrameHeaderLength > _max)
            {
                throw new InvalidOperationException($"The snapshot is larger than the limit of {_max} bytes.");
            }
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent((int)target);
        _buffer.AsSpan(0, _written).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }
}
