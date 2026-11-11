using System.Net.Sockets;
using OneScope.Attach;
using OneScope.Probe;
using Xunit;

namespace OneScope.Attach.Tests;

public class ListingTests
{
    private sealed class Empty : ISnapshotSource
    {
        public void WriteSnapshot(System.Buffers.IBufferWriter<byte> writer)
        {
        }
    }

    [Fact]
    public void ListsEveryRunningProbeAndRemovesAStaleSocket()
    {
        string dir = Support.NewSocketDir();
        var options = new ProbeOptions { Directory = dir };
        using var a = ScopeProbe.Attach(new ProbeInfo("onephysics", "1.0.0", 3), new Empty(), options);
        using var b = ScopeProbe.Attach(new ProbeInfo("onepath", "1.1.0", 2), new Empty(), options);

        // Nothing listens on this file, as after a host was killed. (The runtime removes a socket file it created when the socket
        // closes, so a plain file stands in here; killing a real host process is covered by the out-of-process run.)
        string stale = Path.Combine(dir, "p999999-1.sock");
        File.WriteAllBytes(stale, []);

        Assert.True(File.Exists(stale));
        ProbeListing listing = ProbeFinder.List(dir);
        Assert.Equal(2, listing.Running.Count);
        Assert.Contains(listing.Running, e => e.Handshake.Info == new ProbeInfo("onephysics", "1.0.0", 3) && e.ProcessId == Environment.ProcessId);
        Assert.Contains(listing.Running, e => e.Handshake.Info == new ProbeInfo("onepath", "1.1.0", 2));

        // Never listed as running: removed where the OS refuses the connection (Linux), reported otherwise (macOS says "not a socket").
        Assert.DoesNotContain(listing.Running, e => e.SocketPath == stale);
        Assert.True(listing.Removed.Contains(stale) || listing.Unreadable.Any(u => u.StartsWith(stale, StringComparison.Ordinal)));
        Assert.Equal(listing.Removed.Contains(stale), !File.Exists(stale));

        // Listing connected and left without asking for the stream: nothing was attached.
        Assert.False(a.IsConnected);
        Assert.Equal(0, a.DroppedViewers);

        a.Dispose();
        ProbeListing after = ProbeFinder.List(dir);
        Assert.Single(after.Running);
        Assert.DoesNotContain(after.Running, e => e.SocketPath == a.SocketPath);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ADisposedOrMissingDirectoryListsNothing()
    {
        Assert.Empty(ProbeFinder.List(Path.Combine(Path.GetTempPath(), "osp-nonexistent-" + Guid.NewGuid().ToString("N"))).Running);
    }

    [Fact]
    public void ProbeNeverStartsWithoutAttachAndStaysInertWhenTheSocketCannotBeMade()
    {
        string dir = Support.NewSocketDir();
        Assert.Empty(Directory.GetFiles(dir));
        using var quiet = ScopeProbe.Attach(new ProbeInfo("t", "1", 1), new Empty(), new ProbeOptions { Directory = dir, Listen = false });
        Assert.False(quiet.IsListening);
        Assert.Empty(Directory.GetFiles(dir));

        // A path too long for a socket address makes the probe inert instead of throwing into the host.
        using var inert = ScopeProbe.Attach(new ProbeInfo("t", "1", 1), new Empty(), new ProbeOptions { Directory = Path.Combine(dir, new string('x', 200)) });
        Assert.False(inert.IsListening);
        inert.Record(new byte[8]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ProbeDirectoryIsPrivateToItsUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = Path.Combine(Support.NewSocketDir(), "probes");
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using var p = ScopeProbe.Attach(new ProbeInfo("t", "1", 1), new Empty(), new ProbeOptions { Directory = dir });
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(p.SocketPath!));
    }
}
