using OneGame.Control;
using OneScope.Sdk;

namespace OneScope.App;

internal sealed partial class ShellModel
{
    /// <summary>The shortest stretch of steps the timeline can be zoomed to.</summary>
    public const long MinTimelineSpan = 4;

    // The zoomed window, or both -1 for "fit all" (the whole shared axis, growing as a live source or a fork grows).
    private long _windowFrom = -1;
    private long _windowTo = -1;

    /// <summary>Whether the timeline shows less than the whole axis.</summary>
    public bool TimelineZoomed => _windowFrom >= 0;

    /// <summary>The steps the timeline spans: all of the shared axis, or the zoomed window clamped to it.</summary>
    public TimeRange TimelineRange
    {
        get
        {
            long min = MinSteps;
            long max = Math.Max(MaxSteps, min + 1);
            if (_windowFrom < 0)
            {
                return new TimeRange(min, max);
            }

            long from = Math.Clamp(_windowFrom, min, max - 1);
            long to = Math.Clamp(_windowTo, from + 1, max);
            return new TimeRange(from, to);
        }
    }

    /// <summary>Shows steps <paramref name="from"/> to <paramref name="to"/>, widened to <see cref="MinTimelineSpan"/> and kept inside the axis.</summary>
    /// <exception cref="ControlException"><c>bad_args</c> when <paramref name="to"/> is not after <paramref name="from"/>.</exception>
    public void SetTimelineWindow(long from, long to)
    {
        if (to <= from)
        {
            throw new ControlException("bad_args", $"The timeline window needs from < to, found from {from} and to {to}.");
        }

        long min = MinSteps;
        long max = Math.Max(MaxSteps, min + 1);
        long span = Math.Clamp(to - from, Math.Min(MinTimelineSpan, max - min), max - min);
        long start = Math.Clamp(from - ((span - (to - from)) / 2), min, max - span);
        _windowFrom = start;
        _windowTo = start + span;
        if (_windowFrom == min && _windowTo == max)
        {
            FitTimelineAll();
            return;
        }

        Touch();
    }

    /// <summary>Shows the whole axis again, following it as it grows.</summary>
    public void FitTimelineAll()
    {
        _windowFrom = _windowTo = -1;
        Touch();
    }

    /// <summary>Shows a fork's own stretch: from a little before its fork step to a little after the end of its recording.</summary>
    public void FitTimelineFork(SourceSession fork)
    {
        Branch branch = RequireFork(fork);
        long length = Math.Max(fork.History.EndStep - branch.ForkStep, 1);
        long pad = Math.Max(2, length / 4);
        SetTimelineWindow(branch.ForkStep - pad, fork.History.EndStep + pad);
    }

    /// <summary>Zooms by <paramref name="factor"/> (above 1 shows fewer steps) keeping the step <paramref name="centre"/> where it is on screen.</summary>
    public void ZoomTimeline(double factor, long centre)
    {
        if (!(factor > 0) || !double.IsFinite(factor))
        {
            throw new ControlException("bad_args", $"The zoom factor must be a positive number, found {factor}.");
        }

        TimeRange r = TimelineRange;
        double along = (centre - r.Min) / (double)r.Length;
        long span = Math.Max((long)Math.Round(r.Length / factor), 1);
        long from = centre - (long)Math.Round(along * span);
        SetTimelineWindow(from, from + span);
    }

    /// <summary>Slides the window by <paramref name="delta"/> steps.</summary>
    public void PanTimeline(long delta)
    {
        TimeRange r = TimelineRange;
        SetTimelineWindow(r.Min + delta, r.Max + delta);
    }
}
