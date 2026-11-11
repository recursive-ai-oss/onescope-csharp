using System.Security.Cryptography;
using OneScope.Sdk;

namespace OneScope.Attach;

/// <summary>A source reopened from a session, with its history.</summary>
/// <param name="Index">Its position in the session's source list.</param>
/// <param name="Name">The display name.</param>
/// <param name="Source">The reopened source.</param>
/// <param name="History">A history over it, at the session's cursor (or at its last step if that is earlier).</param>
/// <param name="ParentIndex">The source it was forked from, or -1.</param>
/// <param name="ForkStep">The step it was forked at.</param>
/// <param name="DivergenceStep">For a fork, the first step whose input differs from its parent's, or null while it has done nothing different.</param>
/// <param name="Branch">For a fork, its branch on the parent history, so it can be poked and stepped again; null otherwise.</param>
public sealed record RestoredSource(int Index, string Name, IScopeSource Source, History History, int ParentIndex, long ForkStep, long? DivergenceStep, Branch? Branch = null);

/// <summary>A session reopened: every source with a history, plus the shell's opaque layout.</summary>
/// <param name="CursorStep">The saved time cursor.</param>
/// <param name="Layout">The shell's layout and camera blob, unchanged.</param>
/// <param name="Sources">The sources in session order.</param>
public sealed record RestoredSession(long CursorStep, ReadOnlyMemory<byte> Layout, IReadOnlyList<RestoredSource> Sources);

/// <summary>Reopens a <see cref="SessionFile"/> with the loaded modules.</summary>
public static class SessionRestore
{
    /// <summary>
    /// Opens every source with the module whose id it names, checks each referenced file against its saved hash, builds a history
    /// for each at the session's cursor, and links forks to their parents. A fork's history comes from its own replay, so
    /// seeking it gives the same hashes as the branch had when it was saved.
    /// </summary>
    /// <param name="file">The session.</param>
    /// <param name="modules">The loaded modules.</param>
    /// <param name="readFile">Returns the bytes of a referenced file by the path stored in the session, or null when it is missing.</param>
    /// <exception cref="ScopeFormatException">A module is missing, a referenced file is missing or has changed, or a replay is malformed.</exception>
    public static RestoredSession Open(SessionFile file, IReadOnlyList<IScopeModule> modules, Func<string, byte[]?> readFile)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(readFile);
        var result = new List<RestoredSource>(file.Sources.Count);
        for (int i = 0; i < file.Sources.Count; i++)
        {
            SessionSource entry = file.Sources[i];
            string input = $"session source {i} '{entry.Name}'";
            IScopeModule? module = null;
            foreach (IScopeModule m in modules)
            {
                if (m.Id == entry.ModuleId)
                {
                    module = m;
                    break;
                }
            }

            if (module is null)
            {
                throw new ScopeFormatException(input, $"expected a loaded module with id '{entry.ModuleId}'; none matches.");
            }

            ReadOnlyMemory<byte> bytes;
            if (entry.Kind == SessionSourceKind.Embedded)
            {
                bytes = entry.Replay;
            }
            else
            {
                byte[]? data = readFile(entry.Path!);
                if (data is null)
                {
                    throw new ScopeFormatException(input, $"expected the file '{entry.Path}' to exist.");
                }

                if (!SHA256.HashData(data).AsSpan().SequenceEqual(entry.Sha256))
                {
                    throw new ScopeFormatException(input, $"expected the file '{entry.Path}' to be the one that was saved (its SHA-256 differs).");
                }

                bytes = data;
            }

            IScopeSource source = module.Open(bytes, entry.Name);
            Branch? branch = null;
            if (entry.ParentIndex >= 0)
            {
                // A fork is replayed from its own recording, but must stay writable so it can be poked and stepped again.
                source = source.WithRecording(source.Recording.Fork(source.Recording.EndStep));
            }

            var history = new History(source, new ScopeHistoryOptions { KeyframeInterval = entry.KeyframeInterval });
            if (entry.ParentIndex >= 0)
            {
                RestoredSource parent = result[entry.ParentIndex];
                try
                {
                    branch = parent.History.Adopt(history, entry.ForkStep);
                }
                catch (Exception ex) when (ex is ArgumentException)
                {
                    throw new ScopeFormatException(input, $"expected a fork of source {entry.ParentIndex} at step {entry.ForkStep}: {ex.Message}");
                }
            }

            history.Seek(Math.Clamp(file.CursorStep, history.StartStep, history.EndStep));
            long? divergence = entry.ParentIndex >= 0 ? source.Recording.FirstDifference(result[entry.ParentIndex].Source.Recording) : null;
            result.Add(new RestoredSource(i, entry.Name, source, history, entry.ParentIndex, entry.ForkStep, divergence, branch));
        }

        return new RestoredSession(file.CursorStep, file.Layout, result);
    }
}
