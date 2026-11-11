using System.Net.Sockets;
using OneScope.Probe;

namespace OneScope.Attach;

/// <summary>A running program that has a probe, as read from its handshake.</summary>
/// <param name="SocketPath">The probe's socket.</param>
/// <param name="Handshake">What the probe announced: module id, library version, format version and process id.</param>
public sealed record ProbeEntry(string SocketPath, ProbeHandshake Handshake)
{
    /// <summary>The host's process id.</summary>
    public int ProcessId => Handshake.ProcessId;
}

/// <summary>The result of listing the probes in a directory.</summary>
/// <param name="Running">Probes that answered with a valid handshake, ordered by socket name.</param>
/// <param name="Removed">Socket files whose program is gone (connecting was refused). They were deleted.</param>
/// <param name="Unreadable">Socket files that did not give a valid handshake in time, with the reason. They are left alone.</param>
public sealed record ProbeListing(IReadOnlyList<ProbeEntry> Running, IReadOnlyList<string> Removed, IReadOnlyList<string> Unreadable);

/// <summary>Finds the programs that are running a probe, the way <c>onescope attach</c> lists them.</summary>
public static class ProbeFinder
{
    /// <summary>
    /// Connects to each socket in <paramref name="directory"/> (default <see cref="ProbeDirectory.Default"/>) and reads its handshake,
    /// then closes without asking for the stream, which costs the host nothing. A socket nobody listens on is stale: it is deleted
    /// and reported in <see cref="ProbeListing.Removed"/>, never listed as running.
    /// </summary>
    /// <param name="directory">The directory to scan; a missing directory lists nothing.</param>
    /// <param name="timeoutMilliseconds">How long to wait for each probe, on the viewer's own time (not simulation time).</param>
    public static ProbeListing List(string? directory = null, int timeoutMilliseconds = 2000)
    {
        directory ??= ProbeDirectory.Default();
        var running = new List<ProbeEntry>();
        var removed = new List<string>();
        var unreadable = new List<string>();
        if (!Directory.Exists(directory))
        {
            return new ProbeListing(running, removed, unreadable);
        }

        string[] files = Directory.GetFiles(directory, "*" + ProbeDirectory.SocketExtension);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string path in files)
        {
            try
            {
                running.Add(new ProbeEntry(path, ReadHandshake(path, timeoutMilliseconds)));
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused)
            {
                if (TryDelete(path))
                {
                    removed.Add(path);
                }
                else
                {
                    unreadable.Add($"{path}: nothing is listening, and the file could not be removed.");
                }
            }
            catch (Exception e) when (e is SocketException or ProbeFormatException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                unreadable.Add($"{path}: {e.Message}");
            }
        }

        return new ProbeListing(running, removed, unreadable);
    }

    /// <summary>Connects to one socket and reads its handshake, then closes.</summary>
    /// <exception cref="SocketException">Nothing listens there or the connection failed.</exception>
    /// <exception cref="ProbeFormatException">The peer did not send a valid handshake.</exception>
    /// <exception cref="OperationCanceledException">The probe did not answer in time.</exception>
    public static ProbeHandshake ReadHandshake(string socketPath, int timeoutMilliseconds = 2000)
    {
        using Socket socket = Connect(socketPath, timeoutMilliseconds);
        return ReadHandshake(socket, socketPath);
    }

    internal static Socket Connect(string socketPath, int timeoutMilliseconds)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = new CancellationTokenSource(timeoutMilliseconds);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).AsTask().GetAwaiter().GetResult();
            socket.ReceiveTimeout = timeoutMilliseconds;
            socket.SendTimeout = timeoutMilliseconds;
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static ProbeHandshake ReadHandshake(Socket socket, string input)
    {
        Span<byte> buffer = stackalloc byte[ProbeWire.MaxHandshakeLength];
        ReceiveExact(socket, buffer[..ProbeWire.HandshakeHeaderLength], input, "handshake");
        int strings = ProbeWire.ReadHandshakeStringBytes(buffer, input);
        int total = ProbeWire.HandshakeHeaderLength + strings;
        ReceiveExact(socket, buffer[ProbeWire.HandshakeHeaderLength..total], input, "handshake");
        return ProbeWire.ParseHandshake(buffer[..total], input);
    }

    internal static void ReceiveExact(Socket socket, Span<byte> destination, string input, string what)
    {
        int got = 0;
        while (got < destination.Length)
        {
            int n = socket.Receive(destination[got..]);
            if (n == 0)
            {
                throw new ProbeFormatException(input, $"expected {destination.Length} bytes of {what}, the connection closed after {got}.");
            }

            got += n;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
