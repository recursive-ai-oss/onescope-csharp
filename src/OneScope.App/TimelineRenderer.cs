using OneScope.Sdk;
using UiColor = OneGame.UI.Color;
using UiRect = OneGame.UI.Rect;

namespace OneScope.App;

/// <summary>Where the timeline's parts sit inside its panel. Drawing and pointer hit-testing both use it, so they always agree.</summary>
internal static class TimelineLayout
{
    public const float LabelWidth = 150;
    public const float Pad = 8;
    public const float RulerHeight = 18;
    public const float RowHeight = 15;
    public const float SeriesHeight = 30;

    public static float Left(UiRect r) => r.X + LabelWidth + Pad;

    public static float Width(UiRect r) => Math.Max(r.Width - LabelWidth - (Pad * 2), 1);

    public static float XOf(UiRect r, long step, long max) => Left(r) + (max <= 0 ? 0 : (float)((double)step / max * Width(r)));

    public static long StepAt(UiRect r, float x, long max)
    {
        double f = (x - Left(r)) / Width(r);
        return Math.Clamp((long)Math.Round(f * max), 0, max);
    }
}

/// <summary>Draws the timeline: a ruler, the time axis with its keyframes, other axes, the module's tracks and findings.</summary>
internal static class TimelineRenderer
{
    private static readonly UiColor Ink = new(190, 196, 208);
    private static readonly UiColor Dim = new(120, 126, 140);
    private static readonly UiColor Lane = new(32, 35, 42);
    private static readonly UiColor Scanned = new(48, 54, 68);
    private static readonly UiColor Playhead = new(255, 96, 80);
    private static readonly UiColor EventInk = new(90, 200, 230);
    private static readonly UiColor MarkerInk = new(240, 170, 70);
    private static readonly UiColor SeriesInk = new(120, 200, 120);
    private static readonly UiColor BranchInk = new(110, 150, 255);
    private static readonly UiColor DivergeInk = new(255, 90, 200);

    public static void Draw(Painter c, UiRect r, ShellModel shell)
    {
        c.Fill(r.X, r.Y, r.Width, 1, new UiColor(60, 64, 74));
        if (shell.Sources.Count == 0)
        {
            c.Text("Timeline: open a file to see its history", r.X + 10, r.Y + 8, 12, Dim);
            return;
        }

        SourceSession s = shell.Sources[Math.Min(shell.ActiveSource, shell.Sources.Count - 1)];
        long max = Math.Max(shell.MaxSteps, 1);
        float left = TimelineLayout.Left(r);
        float width = TimelineLayout.Width(r);
        float y = r.Y + 4;

        // Ruler.
        float step = GridMath.NiceStep(max / 8f);
        step = Math.Max(1, MathF.Round(step));
        for (float t = 0; t <= max; t += step)
        {
            float x = TimelineLayout.XOf(r, (long)t, max);
            c.Fill(x, y + 12, 1, 5, Dim);
            c.Text(((long)t).ToString(System.Globalization.CultureInfo.InvariantCulture), x + 2, y, 10, Dim);
        }

        y += TimelineLayout.RulerHeight;

        // Time axis: the played-through extent, keyframes, and the cursor.
        c.Text($"Time  {s.History.CurrentStep}/{s.History.StepCount}", r.X + 8, y, 11, Ink);
        c.Fill(left, y, width, TimelineLayout.RowHeight - 2, Lane);
        float scannedTo = TimelineLayout.XOf(r, Math.Min(s.History.ScannedThrough, s.History.StepCount), max);
        c.Fill(left, y, Math.Max(scannedTo - left, 0), TimelineLayout.RowHeight - 2, Scanned);
        float last = float.MinValue;
        foreach (long k in s.History.KeyframeSteps)
        {
            float x = TimelineLayout.XOf(r, k, max);
            if (x - last >= 3)
            {
                c.Fill(x, y + 8, 1, 5, Dim);
                last = x;
            }
        }

        float rowsTop = y;
        y += TimelineLayout.RowHeight;

        // Forks of this file hang below the time axis as branches: a line from the step they were forked at to where their recording ends,
        // joined to the time axis by a connector, with a marker at the step where their input first differs from the original's.
        foreach (SourceSession f in shell.Sources)
        {
            if (f.Branch is not { } branch || !ReferenceEquals(f.Root, s.Root) || y + TimelineLayout.RowHeight > r.Bottom - 2)
            {
                continue;
            }

            bool active = f.Index == shell.ActiveSource;
            c.Text($"fork {f.Index} @{branch.ForkStep}", r.X + 8, y, 11, active ? BranchInk : Ink);
            c.Fill(left, y, width, TimelineLayout.RowHeight - 2, Lane);
            float x0 = TimelineLayout.XOf(r, branch.ForkStep, max);
            float x1 = Math.Max(TimelineLayout.XOf(r, f.History.StepCount, max), x0 + 2);
            c.Fill(x0, rowsTop + TimelineLayout.RowHeight - 3, 1, y - rowsTop - TimelineLayout.RowHeight + 8, BranchInk);
            c.Fill(x0, y + 5, x1 - x0, 3, active ? BranchInk : Dim);
            if (branch.DivergenceStep is long d)
            {
                float dx = TimelineLayout.XOf(r, d, max);
                c.Fill(dx - 1, y, 3, TimelineLayout.RowHeight - 2, DivergeInk);
                c.Fill(dx - 3, y + 3, 7, 7, DivergeInk);
            }

            y += TimelineLayout.RowHeight;
        }

        // Other axes: the cells of the stage the view is at.
        foreach (AxisInfo a in s.Info.Axes)
        {
            if (a.Id == AxisIds.Time)
            {
                continue;
            }

            int position = s.History.ViewAxis == a.Id ? s.History.ViewPosition : 0;
            c.Text($"{a.Name}  {position}/{a.PositionCount}", r.X + 8, y, 11, Ink);
            float cell = Math.Min(width / Math.Max(a.PositionCount, 1), 40);
            for (int i = 0; i < a.PositionCount; i++)
            {
                c.Fill(left + (i * cell), y, cell - 2, TimelineLayout.RowHeight - 2, i < position ? Scanned : Lane);
                if (i < position)
                {
                    c.Fill(left + (i * cell), y + TimelineLayout.RowHeight - 4, cell - 2, 2, SeriesInk);
                }
            }

            y += TimelineLayout.RowHeight;
        }

        // The module's tracks.
        foreach (TrackData t in s.History.Tracks)
        {
            float h = t.Info.Kind == TrackKind.Series ? TimelineLayout.SeriesHeight : TimelineLayout.RowHeight;
            if (y + h > r.Bottom - 2)
            {
                break;
            }

            c.Text(t.Info.Name, r.X + 8, y, 11, Ink);
            c.Fill(left, y, width, h - 2, Lane);
            DrawTrack(c, r, t, left, width, y, h - 2, max);
            y += h;
        }

        if (y + TimelineLayout.RowHeight <= r.Bottom - 2)
        {
            c.Text($"Findings ({s.History.Findings.Count})", r.X + 8, y, 11, Ink);
            c.Fill(left, y, width, TimelineLayout.RowHeight - 2, Lane);
            foreach (Finding f in s.History.Findings)
            {
                UiColor col = f.Severity == Severity.Error ? new UiColor(240, 80, 80) : f.Severity == Severity.Warning ? MarkerInk : EventInk;
                c.Fill(TimelineLayout.XOf(r, f.Step, max), y, 2, TimelineLayout.RowHeight - 2, col);
            }

            y += TimelineLayout.RowHeight;
        }

        // The playhead goes through every row.
        float px = TimelineLayout.XOf(r, shell.Step, max);
        c.Fill(px - 1, rowsTop - 2, 2, Math.Max(y - rowsTop + 2, 4), Playhead);
        c.Text($"{shell.Step}", Math.Min(px + 4, r.Right - 60), r.Y + 4, 11, Playhead);
    }

    private static void DrawTrack(Painter c, UiRect r, TrackData t, float left, float width, float y, float h, long max)
    {
        IReadOnlyList<TrackSample> samples = t.Samples;
        switch (t.Info.Kind)
        {
            case TrackKind.Event:
                {
                    float last = float.MinValue;
                    foreach (TrackSample smp in samples)
                    {
                        float x = TimelineLayout.XOf(r, smp.Step, max);
                        if (x - last >= 1)
                        {
                            c.Fill(x, y + 2, 1, h - 4, EventInk);
                            last = x;
                        }
                    }

                    break;
                }

            case TrackKind.Marker:
                {
                    float last = float.MinValue;
                    foreach (TrackSample smp in samples)
                    {
                        float x = TimelineLayout.XOf(r, smp.Step, max);
                        c.Fill(x, y, 2, h, MarkerInk);
                        if (x - last > 60 && smp.Text is not null)
                        {
                            c.Text(smp.Text, x + 4, y, 10, MarkerInk);
                            last = x;
                        }
                    }

                    break;
                }

            default:
                {
                    int columns = Math.Max((int)width, 1);
                    double min = double.MaxValue;
                    double hi = double.MinValue;
                    foreach (TrackSample smp in samples)
                    {
                        min = Math.Min(min, smp.Value);
                        hi = Math.Max(hi, smp.Value);
                    }

                    if (min > hi)
                    {
                        break;
                    }

                    double range = Math.Max(hi - min, 1e-12);
                    var lo = new double[columns];
                    var up = new double[columns];
                    Array.Fill(lo, double.MaxValue);
                    Array.Fill(up, double.MinValue);
                    foreach (TrackSample smp in samples)
                    {
                        int col = Math.Clamp((int)((double)smp.Step / max * (columns - 1)), 0, columns - 1);
                        lo[col] = Math.Min(lo[col], smp.Value);
                        up[col] = Math.Max(up[col], smp.Value);
                    }

                    for (int col = 0; col < columns; col++)
                    {
                        if (lo[col] > up[col])
                        {
                            continue;
                        }

                        float y1 = y + h - 1 - (float)((up[col] - min) / range * (h - 2));
                        float y0 = y + h - 1 - (float)((lo[col] - min) / range * (h - 2));
                        c.Fill(left + col, y1, 1, Math.Max(y0 - y1, 1), SeriesInk);
                    }

                    break;
                }
        }
    }
}
