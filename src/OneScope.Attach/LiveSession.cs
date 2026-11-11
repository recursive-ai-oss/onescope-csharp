using OneScope.Probe;
using OneScope.Sdk;

namespace OneScope.Attach;

/// <summary>
/// A live attach turned into something the time model re-simulates: a <see cref="History"/> over the host's snapshot and records.
/// Call <see cref="Pump"/> on the thread that owns the history to move arrived records into it.
/// </summary>
public sealed class LiveSession : IDisposable
{
    private readonly ProbeClient _client;
    private readonly List<byte[]> _scratch = [];

    /// <summary>Bytes of bookkeeping charged for each record on top of its own length, so a stream of empty records still counts.</summary>
    public const int RecordOverheadBytes = 48;

    /// <summary>The default for <c>maxRecordBytes</c>: 256 MiB of received records.</summary>
    public const long DefaultMaxRecordBytes = 256L << 20;

    private LiveSession(ProbeClient client, ILiveSource live, History history, string name, long maxRecordBytes)
    {
        MaxRecordBytes = maxRecordBytes;
        _client = client;
        Live = live;
        History = history;
        Name = name;
    }

    /// <summary>The source name shown to the user.</summary>
    public string Name { get; }

    /// <summary>What the host announced.</summary>
    public ProbeHandshake Handshake => _client.Handshake;

    /// <summary>The live source (its recording grows with <see cref="Pump"/>).</summary>
    public ILiveSource Live { get; }

    /// <summary>The history over the live source: seek, step and fork as with a file.</summary>
    public History History { get; }

    /// <summary>Whether the stream is still open.</summary>
    public ClientState State => _client.State;

    /// <summary>The most record bytes this viewer keeps, each record counted with <see cref="RecordOverheadBytes"/>. The history never grows past it.</summary>
    public long MaxRecordBytes { get; }

    /// <summary>The record bytes kept so far, counted the way <see cref="MaxRecordBytes"/> counts them.</summary>
    public long RecordBytes { get; private set; }

    /// <summary>
    /// Whether the viewer stopped because the next record would have passed <see cref="MaxRecordBytes"/>. The connection is closed (the host sees a viewer
    /// that left and carries on); the history keeps what was received and stays usable.
    /// </summary>
    public bool Capped { get; private set; }

    /// <summary>The error that ended the stream, when <see cref="State"/> is <see cref="ClientState.Failed"/>.</summary>
    public ProbeFormatException? StreamError => _client.Error;

    /// <summary>
    /// Connects to a probe and opens the stream with the module whose id the handshake names.
    /// </summary>
    /// <param name="socketPath">A probe's socket, from <see cref="ProbeFinder.List"/>.</param>
    /// <param name="modules">The modules available; one must have the handshake's module id and implement <see cref="ILiveModule"/>.</param>
    /// <param name="options">History options; null for the defaults.</param>
    /// <param name="limits">Stream size limits; null for the defaults.</param>
    /// <param name="maxRecordBytes">How many record bytes the viewer keeps before it stops and disconnects; see <see cref="MaxRecordBytes"/>.</param>
    /// <param name="timeoutMilliseconds">How long to wait for the connection, handshake and snapshot. The host takes the snapshot at its next recorded step, so a host that steps rarely needs a longer wait.</param>
    /// <exception cref="ScopeFormatException">No module can open the stream, or the module rejected the snapshot.</exception>
    /// <exception cref="ProbeFormatException">The peer sent malformed bytes.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">Nothing listens at the path.</exception>
    public static LiveSession Connect(string socketPath, IReadOnlyList<IScopeModule> modules, ScopeHistoryOptions? options = null, ProbeLimits? limits = null, long maxRecordBytes = DefaultMaxRecordBytes, int timeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordBytes);
        ProbeClient client = ProbeClient.Connect(socketPath, limits, timeoutMilliseconds);
        try
        {
            ProbeInfo info = client.Handshake.Info;
            ILiveModule? module = null;
            foreach (IScopeModule m in modules)
            {
                if (m.Id == info.ModuleId)
                {
                    module = m as ILiveModule
                        ?? throw new ScopeFormatException(socketPath, $"expected a module that can open live streams for '{info.ModuleId}'; '{m.Name}' opens files only.");
                    break;
                }
            }

            if (module is null)
            {
                throw new ScopeFormatException(socketPath, $"expected a loaded module with id '{info.ModuleId}' (library version {info.LibraryVersion}); none matches.");
            }

            string name = $"{info.ModuleId} pid {client.Handshake.ProcessId}";
            ILiveSource live = module.OpenLive(info.LibraryVersion, info.FormatVersion, client.Snapshot, name);
            return new LiveSession(client, live, new History(live.Source, options), name, maxRecordBytes);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends up to <paramref name="max"/> records that have arrived to the live recording and returns how many were appended (fewer when the cap is reached; see <see cref="Capped"/>).
    /// Records that arrived before the stream ended are delivered even after <see cref="State"/> has changed.
    /// </summary>
    /// <exception cref="ScopeFormatException">The module rejected a record; the session should be closed.</exception>
    public int Pump(int max = int.MaxValue)
    {
        if (Capped)
        {
            return 0;
        }

        _scratch.Clear();
        _client.Drain(_scratch, max);
        int n = 0;
        foreach (byte[] record in _scratch)
        {
            long cost = RecordBytes + record.Length + RecordOverheadBytes;
            if (cost > MaxRecordBytes)
            {
                Capped = true;
                _client.Dispose();
                break;
            }

            Live.AppendRecord(record);
            RecordBytes = cost;
            n++;
        }

        _scratch.Clear();
        return n;
    }

    /// <summary>Closes the connection. The history stays usable over what was received.</summary>
    public void Dispose() => _client.Dispose();
}
