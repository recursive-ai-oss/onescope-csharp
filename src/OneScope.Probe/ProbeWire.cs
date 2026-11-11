using System.Buffers.Binary;
using System.Text;

namespace OneScope.Probe;

/// <summary>
/// Thrown when bytes read from a probe (a handshake or a stream frame) are malformed or over a limit.
/// The message names the input and says what was expected and where.
/// </summary>
public sealed class ProbeFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="input">What was being read, for example a socket path.</param>
    /// <param name="message">What was expected and where it went wrong.</param>
    public ProbeFormatException(string input, string message)
        : base($"{input}: {message}")
    {
        Input = input ?? string.Empty;
    }

    /// <summary>What was being read when the problem was found.</summary>
    public string Input { get; }
}

/// <summary>What a probe tells a viewer when it connects, before any snapshot.</summary>
/// <param name="Info">The module id, library version and replay-format version the host declared.</param>
/// <param name="ProcessId">The host's process id.</param>
public readonly record struct ProbeHandshake(ProbeInfo Info, int ProcessId);

/// <summary>The kind of a frame in a probe's stream.</summary>
public enum ProbeFrameKind : byte
{
    /// <summary>The library's snapshot of the whole world. Always the first frame.</summary>
    Snapshot = 1,

    /// <summary>One replay record, exactly the bytes the host passed to <see cref="ScopeProbe.Record"/>.</summary>
    Record = 2,

    /// <summary>The host closed its probe. No bytes follow.</summary>
    End = 3,
}

/// <summary>Size limits a reader enforces on a stream before it allocates.</summary>
/// <param name="MaxSnapshotBytes">The most bytes one snapshot frame may carry.</param>
/// <param name="MaxRecordBytes">The most bytes one record frame may carry.</param>
public readonly record struct ProbeLimits(int MaxSnapshotBytes, int MaxRecordBytes)
{
    /// <summary>64 MiB snapshots, 16 MiB records.</summary>
    public static ProbeLimits Default => new(64 << 20, 16 << 20);
}

/// <summary>
/// The probe's wire format, version 1 (little endian throughout; documented in <c>docs/probe-wire.md</c>).
/// A viewer connects, reads the handshake, and sends one byte (<see cref="AttachCommand"/>) to ask for the stream.
/// Connecting only to read the handshake, as a listing does, costs the host nothing. The stream is then a snapshot frame
/// followed by record frames, each <c>u8 kind, u32 length, bytes</c>, ending with an end frame or a closed socket.
/// </summary>
public static class ProbeWire
{
    /// <summary>The wire version this code reads and writes.</summary>
    public const ushort ProtocolVersion = 1;

    /// <summary>The single byte a viewer sends to ask for the stream. The host reads nothing else from a viewer.</summary>
    public const byte AttachCommand = 1;

    /// <summary>Bytes in the fixed part of the handshake.</summary>
    public const int HandshakeHeaderLength = 20;

    /// <summary>The longest module id or library version, in UTF-8 bytes.</summary>
    public const int MaxStringBytes = 256;

    /// <summary>The longest possible handshake.</summary>
    public const int MaxHandshakeLength = HandshakeHeaderLength + 2 * MaxStringBytes;

    /// <summary>Bytes in a frame header: kind and length.</summary>
    public const int FrameHeaderLength = 5;

    private static readonly UTF8Encoding Strict = new(false, true);

    private static ReadOnlySpan<byte> Magic => "OSPB"u8;

    /// <summary>The handshake's size in bytes for <paramref name="info"/>.</summary>
    /// <exception cref="ArgumentException">A string is longer than <see cref="MaxStringBytes"/> in UTF-8.</exception>
    public static int HandshakeLength(in ProbeInfo info) =>
        HandshakeHeaderLength + CheckedByteCount(info.ModuleId, "module id") + CheckedByteCount(info.LibraryVersion, "library version");

    /// <summary>
    /// Writes the handshake: magic "OSPB", u16 protocol version, u16 flags (0), i32 format version, u32 process id,
    /// u16 module id length, u16 library version length, then the two UTF-8 strings.
    /// </summary>
    /// <returns>The bytes written.</returns>
    /// <exception cref="ArgumentException">A string is too long, or <paramref name="destination"/> is smaller than <see cref="HandshakeLength"/>.</exception>
    public static int WriteHandshake(Span<byte> destination, in ProbeInfo info, int processId)
    {
        int idLen = CheckedByteCount(info.ModuleId, "module id");
        int verLen = CheckedByteCount(info.LibraryVersion, "library version");
        int total = HandshakeHeaderLength + idLen + verLen;
        if (destination.Length < total)
        {
            throw new ArgumentException($"The destination must hold {total} bytes.", nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], info.FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)processId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], (ushort)idLen);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[18..], (ushort)verLen);
        Strict.GetBytes(info.ModuleId, destination[HandshakeHeaderLength..]);
        Strict.GetBytes(info.LibraryVersion, destination[(HandshakeHeaderLength + idLen)..]);
        return total;
    }

    /// <summary>
    /// Checks the fixed part of a handshake and returns how many more bytes (the two strings) follow it.
    /// </summary>
    /// <param name="header">At least <see cref="HandshakeHeaderLength"/> bytes.</param>
    /// <param name="input">What is being read, for error messages.</param>
    /// <exception cref="ProbeFormatException">The header is short, has the wrong magic, version or flags, or declares a string over the limit.</exception>
    public static int ReadHandshakeStringBytes(ReadOnlySpan<byte> header, string input)
    {
        if (header.Length < HandshakeHeaderLength)
        {
            throw new ProbeFormatException(input, $"expected a {HandshakeHeaderLength}-byte handshake header, only {header.Length} bytes.");
        }

        if (!header[..4].SequenceEqual(Magic))
        {
            throw new ProbeFormatException(input, "expected the magic bytes 'OSPB' at offset 0; this is not a onescope probe.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != ProtocolVersion)
        {
            throw new ProbeFormatException(input, $"expected probe protocol version {ProtocolVersion} at offset 4, found {version}.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (flags != 0)
        {
            throw new ProbeFormatException(input, $"expected flags 0 at offset 6, found {flags}.");
        }

        int idLen = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        int verLen = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        if (idLen == 0 || idLen > MaxStringBytes)
        {
            throw new ProbeFormatException(input, $"expected a module id of 1 to {MaxStringBytes} bytes at offset 16, found length {idLen}.");
        }

        if (verLen > MaxStringBytes)
        {
            throw new ProbeFormatException(input, $"expected a library version of at most {MaxStringBytes} bytes at offset 18, found length {verLen}.");
        }

        return idLen + verLen;
    }

    /// <summary>Parses a complete handshake.</summary>
    /// <param name="bytes">Exactly the handshake: the header and both strings, and nothing after.</param>
    /// <param name="input">What is being read, for error messages.</param>
    /// <exception cref="ProbeFormatException">The bytes are not a valid handshake.</exception>
    public static ProbeHandshake ParseHandshake(ReadOnlySpan<byte> bytes, string input)
    {
        int strings = ReadHandshakeStringBytes(bytes, input);
        int expected = HandshakeHeaderLength + strings;
        if (bytes.Length != expected)
        {
            throw new ProbeFormatException(input, $"expected a handshake of {expected} bytes, found {bytes.Length}.");
        }

        int idLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);
        string id;
        string ver;
        try
        {
            id = Strict.GetString(bytes.Slice(HandshakeHeaderLength, idLen));
            ver = Strict.GetString(bytes[(HandshakeHeaderLength + idLen)..]);
        }
        catch (DecoderFallbackException)
        {
            throw new ProbeFormatException(input, $"expected UTF-8 text for the module id and library version at offset {HandshakeHeaderLength}.");
        }

        int format = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        int pid = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        return new ProbeHandshake(new ProbeInfo(id, ver, format), pid);
    }

    /// <summary>Writes a frame header (kind and payload length) into the first <see cref="FrameHeaderLength"/> bytes.</summary>
    public static void WriteFrameHeader(Span<byte> destination, ProbeFrameKind kind, int length)
    {
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], (uint)length);
    }

    /// <summary>
    /// Reads a frame header and checks the length against <paramref name="limits"/> before the caller allocates.
    /// <paramref name="first"/> says the frame is the first of the stream, which must be a snapshot; later ones must be records or the end.
    /// </summary>
    /// <exception cref="ProbeFormatException">The header is short, the kind is unknown or out of order, or the length is over the limit.</exception>
    public static (ProbeFrameKind Kind, int Length) ReadFrameHeader(ReadOnlySpan<byte> header, in ProbeLimits limits, bool first, string input)
    {
        if (header.Length < FrameHeaderLength)
        {
            throw new ProbeFormatException(input, $"expected a {FrameHeaderLength}-byte frame header, only {header.Length} bytes.");
        }

        var kind = (ProbeFrameKind)header[0];
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[1..]);
        switch (kind)
        {
            case ProbeFrameKind.Snapshot when first:
                if (length > (uint)limits.MaxSnapshotBytes)
                {
                    throw new ProbeFormatException(input, $"expected a snapshot of at most {limits.MaxSnapshotBytes} bytes, the frame declares {length}.");
                }

                break;
            case ProbeFrameKind.Record when !first:
                if (length > (uint)limits.MaxRecordBytes)
                {
                    throw new ProbeFormatException(input, $"expected a record of at most {limits.MaxRecordBytes} bytes, the frame declares {length}.");
                }

                break;
            case ProbeFrameKind.End when !first:
                if (length != 0)
                {
                    throw new ProbeFormatException(input, $"expected an end frame with length 0, found {length}.");
                }

                break;
            default:
                throw new ProbeFormatException(input, first
                    ? $"expected a snapshot frame (kind 1) first, found kind {header[0]}."
                    : $"expected a record (2) or end (3) frame, found kind {header[0]}.");
        }

        return (kind, (int)length);
    }

    private static int CheckedByteCount(string text, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        int n;
        try
        {
            n = Strict.GetByteCount(text);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException($"The {what} is not valid text.", nameof(text));
        }

        if (n > MaxStringBytes)
        {
            throw new ArgumentException($"The {what} may be at most {MaxStringBytes} UTF-8 bytes, found {n}.", nameof(text));
        }

        return n;
    }
}
