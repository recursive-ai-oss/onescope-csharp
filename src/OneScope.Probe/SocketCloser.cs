using System.Net.Sockets;

namespace OneScope.Probe;

/// <summary>
/// Closes sockets without making the caller wait. On some platforms disposing a socket that another thread is blocked on
/// waits for that thread, and the probe's callers (the host's thread) must never wait. Shutting down first wakes blocked
/// readers and writers; the dispose then runs on a pool thread.
/// </summary>
internal static class SocketCloser
{
    public static void CloseInBackground(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Not connected (a listener), or already closed.
        }

        _ = ThreadPool.UnsafeQueueUserWorkItem(static s => s.Dispose(), socket, preferLocal: false);
    }
}
