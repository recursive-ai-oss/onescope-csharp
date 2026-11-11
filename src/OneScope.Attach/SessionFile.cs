using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OneScope.Sdk;

namespace OneScope.Attach;

/// <summary>How a session holds a source.</summary>
public enum SessionSourceKind : byte
{
    /// <summary>The source's replay bytes are inside the session file.</summary>
    Embedded = 0,

    /// <summary>The session names a file and holds its SHA-256, so opening checks that the file is the one that was saved.</summary>
    Referenced = 1,
}

/// <summary>Size limits <see cref="SessionFile.Parse"/> enforces before allocating.</summary>
/// <param name="MaxFileBytes">The largest session file accepted.</param>
/// <param name="MaxSources">The most sources (forks count as sources).</param>
/// <param name="MaxLayoutBytes">The largest opaque layout blob.</param>
public readonly record struct SessionLimits(long MaxFileBytes, int MaxSources, int MaxLayoutBytes)
{
    /// <summary>1 GiB files, 4096 sources, 16 MiB of layout.</summary>
    public static SessionLimits Default => new(1L << 30, 4096, 16 << 20);
}

/// <summary>One source in a session: an embedded replay or a checked file reference, optionally a fork of an earlier source.</summary>
public sealed class SessionSource
{
    private SessionSource(string moduleId, string name, SessionSourceKind kind, int parent, long forkStep, int keyframeInterval,
        ReadOnlyMemory<byte> replay, string? path, byte[]? hash)
    {
        ModuleId = moduleId;
        Name = name;
        Kind = kind;
        ParentIndex = parent;
        ForkStep = forkStep;
        KeyframeInterval = keyframeInterval;
        Replay = replay;
        Path = path;
        Sha256 = hash;
    }

    /// <summary>The module that opens the replay.</summary>
    public string ModuleId { get; }

    /// <summary>The display name.</summary>
    public string Name { get; }

    /// <summary>Embedded or referenced.</summary>
    public SessionSourceKind Kind { get; }

    /// <summary>Index of the source this one was forked from, or -1. A parent always comes earlier in the list. A fork is always embedded: it holds the pokes.</summary>
    public int ParentIndex { get; }

    /// <summary>The step the fork was taken at (0 when <see cref="ParentIndex"/> is -1).</summary>
    public long ForkStep { get; }

    /// <summary>The keyframe interval to open the history with.</summary>
    public int KeyframeInterval { get; }

    /// <summary>The replay bytes, in the module's format, when <see cref="Kind"/> is embedded; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Replay { get; }

    /// <summary>The file path when referenced; null otherwise. As the user gave it; a loader may resolve it relative to the session file.</summary>
    public string? Path { get; }

    /// <summary>The 32-byte SHA-256 of the referenced file; null when embedded.</summary>
    public byte[]? Sha256 { get; }

    /// <summary>Embeds a source by writing its replay.</summary>
    public static SessionSource Embed(IScopeSource source, int keyframeInterval = ScopeHistoryOptions.DefaultKeyframeInterval)
    {
        ArgumentNullException.ThrowIfNull(source);
        var w = new ArrayBufferWriter<byte>();
        source.WriteReplay(w);
        return new SessionSource(source.Info.ModuleId, source.Info.Name, SessionSourceKind.Embedded, -1, 0, keyframeInterval, w.WrittenSpan.ToArray(), null, null);
    }

    /// <summary>Embeds a fork: <paramref name="source"/> is the branch's source (its recording holds the pokes), forked from the source at <paramref name="parentIndex"/> at <paramref name="forkStep"/>.</summary>
    public static SessionSource EmbedFork(IScopeSource source, int parentIndex, long forkStep, int keyframeInterval = ScopeHistoryOptions.DefaultKeyframeInterval)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(parentIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(forkStep);
        var w = new ArrayBufferWriter<byte>();
        source.WriteReplay(w);
        return new SessionSource(source.Info.ModuleId, source.Info.Name, SessionSourceKind.Embedded, parentIndex, forkStep, keyframeInterval, w.WrittenSpan.ToArray(), null, null);
    }

    /// <summary>Refers to a file, recording its hash from <paramref name="fileBytes"/> (the bytes the source was opened from).</summary>
    public static SessionSource Reference(string moduleId, string name, string path, ReadOnlySpan<byte> fileBytes, int keyframeInterval = ScopeHistoryOptions.DefaultKeyframeInterval)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        return new SessionSource(moduleId, name, SessionSourceKind.Referenced, -1, 0, keyframeInterval, default, path, SHA256.HashData(fileBytes));
    }

    internal static SessionSource FromParts(string moduleId, string name, SessionSourceKind kind, int parent, long forkStep, int interval,
        ReadOnlyMemory<byte> replay, string? path, byte[]? hash) =>
        new(moduleId, name, kind, parent, forkStep, interval, replay, path, hash);
}

/// <summary>
/// A <c>.oscope</c> session file (version 1, little endian; documented in <c>docs/session-file.md</c>): the open sources, the forks
/// with their poke replays, the shared time cursor, and an opaque blob the shell uses for layout and cameras.
/// Reading then writing gives the same bytes.
/// </summary>
public sealed class SessionFile
{
    /// <summary>The format version this code reads and writes.</summary>
    public const ushort Version = 1;

    /// <summary>Bytes in the fixed header.</summary>
    public const int HeaderLength = 24;

    /// <summary>Bytes in the fixed part of each source record.</summary>
    public const int SourceHeaderLength = 27;

    /// <summary>The longest module id, name or path, in UTF-8 bytes.</summary>
    public const int MaxStringBytes = 4096;

    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>The shared time cursor, in steps of the first source's history. At least 0.</summary>
    public long CursorStep { get; set; }

    /// <summary>The sources, parents before their forks.</summary>
    public List<SessionSource> Sources { get; } = [];

    /// <summary>The shell's opaque layout and camera state. The SDK never interprets it.</summary>
    public ReadOnlyMemory<byte> Layout { get; set; }

    private static ReadOnlySpan<byte> Magic => "OSCP"u8;

    /// <summary>Reads a session from a file, checking its size against <paramref name="limits"/> before reading it.</summary>
    /// <exception cref="ScopeFormatException">The file is too large or malformed.</exception>
    public static SessionFile Load(string path, SessionLimits? limits = null)
    {
        SessionLimits lim = limits ?? SessionLimits.Default;
        long size = new FileInfo(path).Length;
        if (size > lim.MaxFileBytes)
        {
            throw new ScopeFormatException(path, $"expected a session of at most {lim.MaxFileBytes} bytes, the file has {size}.");
        }

        return Parse(File.ReadAllBytes(path), path, lim);
    }

    /// <summary>Parses session bytes. Every length is checked against the bytes that remain before anything is allocated.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="input">What is being read, for error messages.</param>
    /// <param name="limits">Limits; null for <see cref="SessionLimits.Default"/>.</param>
    /// <exception cref="ScopeFormatException">The bytes are not a valid session.</exception>
    public static SessionFile Parse(ReadOnlyMemory<byte> bytes, string input, SessionLimits? limits = null)
    {
        SessionLimits lim = limits ?? SessionLimits.Default;
        ReadOnlySpan<byte> s = bytes.Span;
        if (s.Length > lim.MaxFileBytes)
        {
            throw new ScopeFormatException(input, $"expected a session of at most {lim.MaxFileBytes} bytes, found {s.Length}.");
        }

        if (s.Length < HeaderLength)
        {
            throw new ScopeFormatException(input, $"expected a {HeaderLength}-byte header, only {s.Length} bytes.");
        }

        if (!s[..4].SequenceEqual(Magic))
        {
            throw new ScopeFormatException(input, "expected the magic bytes 'OSCP' at offset 0; this is not a session file.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        if (version != Version)
        {
            throw new ScopeFormatException(input, $"expected session version {Version} at offset 4, found {version}.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        if (flags != 0)
        {
            throw new ScopeFormatException(input, $"expected flags 0 at offset 6, found {flags}.");
        }

        long cursor = BinaryPrimitives.ReadInt64LittleEndian(s[8..]);
        if (cursor < 0)
        {
            throw new ScopeFormatException(input, $"expected a cursor step of 0 or more at offset 8, found {cursor}.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
        if (count > (uint)lim.MaxSources)
        {
            throw new ScopeFormatException(input, $"expected at most {lim.MaxSources} sources at offset 16, found {count}.");
        }

        uint layoutLength = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
        if (layoutLength > (uint)lim.MaxLayoutBytes)
        {
            throw new ScopeFormatException(input, $"expected at most {lim.MaxLayoutBytes} layout bytes at offset 20, found {layoutLength}.");
        }

        // Each source needs at least its fixed part, so a huge count cannot pass against a short file.
        if ((long)count * SourceHeaderLength > s.Length - HeaderLength)
        {
            throw new ScopeFormatException(input, $"expected {count} sources of at least {SourceHeaderLength} bytes each, only {s.Length - HeaderLength} bytes follow the header.");
        }

        var file = new SessionFile { CursorStep = cursor };
        int pos = HeaderLength;
        for (int i = 0; i < (int)count; i++)
        {
            if (s.Length - pos < SourceHeaderLength)
            {
                throw new ScopeFormatException(input, $"expected a {SourceHeaderLength}-byte header for source {i} at offset {pos}, only {s.Length - pos} bytes remain.");
            }

            ReadOnlySpan<byte> h = s.Slice(pos, SourceHeaderLength);
            var kind = (SessionSourceKind)h[0];
            int parent = BinaryPrimitives.ReadInt32LittleEndian(h[1..]);
            long forkStep = BinaryPrimitives.ReadInt64LittleEndian(h[5..]);
            uint interval = BinaryPrimitives.ReadUInt32LittleEndian(h[13..]);
            int idLen = BinaryPrimitives.ReadUInt16LittleEndian(h[17..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[19..]);
            int pathLen = BinaryPrimitives.ReadUInt16LittleEndian(h[21..]);
            uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(h[23..]);
            CheckSourceFields(input, i, pos, kind, parent, forkStep, interval, idLen, nameLen, pathLen, payloadLen);
            pos += SourceHeaderLength;
            long need = (long)idLen + nameLen + pathLen + payloadLen;
            if (need > s.Length - pos)
            {
                throw new ScopeFormatException(input, $"expected {need} bytes of strings and payload for source {i} at offset {pos}, only {s.Length - pos} remain.");
            }

            string moduleId = ReadString(s, ref pos, idLen, input, i, "module id");
            string name = ReadString(s, ref pos, nameLen, input, i, "name");
            string? path = kind == SessionSourceKind.Referenced ? ReadString(s, ref pos, pathLen, input, i, "path") : null;
            ReadOnlyMemory<byte> payload = bytes.Slice(pos, (int)payloadLen);
            pos += (int)payloadLen;
            file.Sources.Add(kind == SessionSourceKind.Embedded
                ? SessionSource.FromParts(moduleId, name, kind, parent, forkStep, (int)interval, payload, null, null)
                : SessionSource.FromParts(moduleId, name, kind, parent, forkStep, (int)interval, default, path, payload.ToArray()));
        }

        if (layoutLength != s.Length - pos)
        {
            throw new ScopeFormatException(input, $"expected {layoutLength} layout bytes ending the file at offset {pos}, found {s.Length - pos} bytes.");
        }

        file.Layout = bytes.Slice(pos, (int)layoutLength);
        return file;
    }

    /// <summary>Checks the session and writes it. Parsing the result and writing again gives the same bytes.</summary>
    /// <exception cref="ScopeFormatException">The session is not valid (a fork before its parent, a referenced fork, a string too long).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or length does not fit the format.</exception>
    public void Write(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (CursorStep < 0)
        {
            throw new ScopeFormatException("session", $"expected a cursor step of 0 or more, found {CursorStep}.");
        }

        long total = HeaderLength + (long)Layout.Length;
        var strings = new byte[Sources.Count][][];
        for (int i = 0; i < Sources.Count; i++)
        {
            SessionSource src = Sources[i];
            byte[] id = Encode(src.ModuleId, "module id", i);
            byte[] name = Encode(src.Name, "name", i);
            byte[] path = src.Kind == SessionSourceKind.Referenced ? Encode(src.Path ?? string.Empty, "path", i) : [];
            strings[i] = [id, name, path];
            int payload = src.Kind == SessionSourceKind.Embedded ? src.Replay.Length : src.Sha256?.Length ?? 0;
            CheckSourceFields("session", i, 0, src.Kind, src.ParentIndex, src.ForkStep, (uint)src.KeyframeInterval, id.Length, name.Length, path.Length, (uint)payload);
            total += SourceHeaderLength + id.Length + name.Length + path.Length + payload;
        }

        if (total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(writer), total, "The session is larger than 2 GiB.");
        }

        Span<byte> o = writer.GetSpan((int)total);
        Magic.CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(o[6..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(o[8..], CursorStep);
        BinaryPrimitives.WriteUInt32LittleEndian(o[16..], (uint)Sources.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(o[20..], (uint)Layout.Length);
        int pos = HeaderLength;
        for (int i = 0; i < Sources.Count; i++)
        {
            SessionSource src = Sources[i];
            byte[][] st = strings[i];
            ReadOnlySpan<byte> payload = src.Kind == SessionSourceKind.Embedded ? src.Replay.Span : src.Sha256;
            Span<byte> h = o.Slice(pos, SourceHeaderLength);
            h[0] = (byte)src.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(h[1..], src.ParentIndex);
            BinaryPrimitives.WriteInt64LittleEndian(h[5..], src.ForkStep);
            BinaryPrimitives.WriteUInt32LittleEndian(h[13..], (uint)src.KeyframeInterval);
            BinaryPrimitives.WriteUInt16LittleEndian(h[17..], (ushort)st[0].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(h[19..], (ushort)st[1].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(h[21..], (ushort)st[2].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h[23..], (uint)payload.Length);
            pos += SourceHeaderLength;
            foreach (byte[] part in st)
            {
                part.CopyTo(o[pos..]);
                pos += part.Length;
            }

            payload.CopyTo(o[pos..]);
            pos += payload.Length;
        }

        Layout.Span.CopyTo(o[pos..]);
        pos += Layout.Length;
        writer.Advance(pos);
    }

    private static void CheckSourceFields(string input, int index, int offset, SessionSourceKind kind, int parent, long forkStep, uint interval,
        int idLen, int nameLen, int pathLen, uint payloadLen)
    {
        if (kind is not (SessionSourceKind.Embedded or SessionSourceKind.Referenced))
        {
            throw new ScopeFormatException(input, $"expected source {index} kind 0 (embedded) or 1 (referenced) at offset {offset}, found {(byte)kind}.");
        }

        if (parent < -1 || parent >= index)
        {
            throw new ScopeFormatException(input, $"expected the parent of source {index} to be -1 or an earlier source, found {parent}.");
        }

        if (parent == -1 ? forkStep != 0 : forkStep < 0)
        {
            throw new ScopeFormatException(input, $"expected a fork step of 0 for a source with no parent and 0 or more for a fork, source {index} has {forkStep}.");
        }

        if (parent != -1 && kind != SessionSourceKind.Embedded)
        {
            throw new ScopeFormatException(input, $"expected fork source {index} to be embedded, because it holds the pokes.");
        }

        if (interval is < 1 or > int.MaxValue)
        {
            throw new ScopeFormatException(input, $"expected a keyframe interval of 1 or more for source {index}, found {interval}.");
        }

        if (idLen is < 1 or > MaxStringBytes || nameLen > MaxStringBytes || pathLen > MaxStringBytes)
        {
            throw new ScopeFormatException(input, $"expected source {index} to have a module id of 1 to {MaxStringBytes} bytes and a name and path of at most {MaxStringBytes}, found {idLen}, {nameLen}, {pathLen}.");
        }

        if (kind == SessionSourceKind.Referenced && (payloadLen != 32 || pathLen == 0))
        {
            throw new ScopeFormatException(input, $"expected referenced source {index} to have a path and a 32-byte hash, found path length {pathLen} and hash length {payloadLen}.");
        }

        if (kind == SessionSourceKind.Embedded && pathLen != 0)
        {
            throw new ScopeFormatException(input, $"expected embedded source {index} to have no path, found path length {pathLen}.");
        }
    }

    private static string ReadString(ReadOnlySpan<byte> s, ref int pos, int length, string input, int index, string what)
    {
        try
        {
            string text = Strict.GetString(s.Slice(pos, length));
            pos += length;
            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new ScopeFormatException(input, $"expected UTF-8 text for the {what} of source {index} at offset {pos}.");
        }
    }

    private static byte[] Encode(string text, string what, int index)
    {
        try
        {
            return Strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw new ScopeFormatException("session", $"expected valid text for the {what} of source {index}.");
        }
    }
}
