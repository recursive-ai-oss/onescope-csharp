using OneGame.Control;

namespace OneScope.App;

/// <summary>One source that a close removed, with the number it had before the survivors were renumbered.</summary>
internal readonly record struct ClosedSource(int Index, string Label, bool Fork);

internal sealed partial class ShellModel
{
    /// <summary>Raised whenever sources are removed and renumbered, so a cache keyed by a source number knows its number may now mean another source.</summary>
    public int SourceEpoch { get; private set; }

    /// <summary>
    /// Closes a source and every fork made from it (a fork is meaningless without its parent). Surviving sources keep their order and are
    /// renumbered without gaps; the views of the closed sources go with them. A live source's connection is dropped; the program is not told anything.
    /// </summary>
    /// <returns>The closed sources by their old numbers, the closed source first.</returns>
    public IReadOnlyList<ClosedSource> Close(SourceSession source)
    {
        var closing = new List<SourceSession> { source };
        for (int i = 0; i < closing.Count; i++)
        {
            foreach (SourceSession s in _sources)
            {
                if (s.Parent == closing[i] && !closing.Contains(s))
                {
                    closing.Add(s);
                }
            }
        }

        var closed = new List<ClosedSource>();
        foreach (SourceSession s in closing)
        {
            closed.Add(new ClosedSource(s.Index, s.Label, s.IsFork));
        }

        SourceSession? active = ActiveSource < _sources.Count ? _sources[ActiveSource] : null;
        bool activeGone = active is null || closing.Contains(active);
        SourceSession? solo = Solo >= 0 && Solo < _sources.Count ? _sources[Solo] : null;
        foreach (SourceSession s in closing)
        {
            s.Live?.Dispose();
            _sources.Remove(s);
        }

        for (int i = 0; i < _sources.Count; i++)
        {
            _sources[i].Index = i;
        }

        // Views name sources by number, which has just changed: resolve them through the objects the old numbers named.
        RemapViews(closed, closing);
        _log.RemoveAll(r => closed.Any(c => c.Index == r.Source));
        for (int i = 0; i < _log.Count; i++)
        {
            LogRow r = _log[i];
            _log[i] = r with { Source = r.Source - closed.Count(c => c.Index < r.Source) };
        }

        _logVersion = -1;
        ActiveSource = activeGone ? 0 : active!.Index;
        ActiveView = Math.Clamp(ActiveView, 0, Math.Max(_views.Count - 1, 0));
        Solo = solo is not null && _sources.Contains(solo) ? solo.Index : -1;
        if (activeGone)
        {
            Select(default);
        }

        if (_pair is not null && (closing.Contains(_pair.First) || closing.Contains(_pair.Second)))
        {
            _pair = null;
        }

        _implicit = null;
        SourceEpoch++;
        if (_sources.Count == 0)
        {
            Step = 0;
            SetSpeed(0);
        }
        else
        {
            SeekCore(Math.Clamp(Step, MinSteps, MaxSteps));
        }

        Touch();
        return closed;
    }

    private void RemapViews(List<ClosedSource> closed, List<SourceSession> closing)
    {
        var gone = closed.Select(c => c.Index).ToHashSet();
        for (int i = _views.Count - 1; i >= 0; i--)
        {
            ViewState v = _views[i];
            if (gone.Contains(v.Source))
            {
                _views.RemoveAt(i);
                continue;
            }

            v.Source -= closed.Count(c => c.Index < v.Source);
            v.Ghosts.RemoveAll(closing.Contains);
            v.Touch();
        }
    }
}
