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

    private LiveSession(ProbeClient client, ILiveSource live, History history, string name)
    {
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

    /// <summary>The error that ended the stream, when <see cref="State"/> is <see cref="ClientState.Failed"/>.</summary>
    public ProbeFormatException? StreamError => _client.Error;

    /// <summary>
    /// Connects to a probe and opens the stream with the module whose id the handshake names.
    /// </summary>
    /// <param name="socketPath">A probe's socket, from <see cref="ProbeFinder.List"/>.</param>
    /// <param name="modules">The modules available; one must have the handshake's module id and implement <see cref="ILiveModule"/>.</param>
    /// <param name="options">History options; null for the defaults.</param>
    /// <param name="limits">Stream size limits; null for the defaults.</param>
    /// <exception cref="ScopeFormatException">No module can open the stream, or the module rejected the snapshot.</exception>
    /// <exception cref="ProbeFormatException">The peer sent malformed bytes.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">Nothing listens at the path.</exception>
    public static LiveSession Connect(string socketPath, IReadOnlyList<IScopeModule> modules, ScopeHistoryOptions? options = null, ProbeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ProbeClient client = ProbeClient.Connect(socketPath, limits);
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
            return new LiveSession(client, live, new History(live.Source, options), name);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends up to <paramref name="max"/> records that have arrived to the live recording and returns how many.
    /// Records that arrived before the stream ended are delivered even after <see cref="State"/> has changed.
    /// </summary>
    /// <exception cref="ScopeFormatException">The module rejected a record; the session should be closed.</exception>
    public int Pump(int max = int.MaxValue)
    {
        _scratch.Clear();
        int n = _client.Drain(_scratch, max);
        foreach (byte[] record in _scratch)
        {
            Live.AppendRecord(record);
        }

        _scratch.Clear();
        return n;
    }

    /// <summary>Closes the connection. The history stays usable over what was received.</summary>
    public void Dispose() => _client.Dispose();
}
