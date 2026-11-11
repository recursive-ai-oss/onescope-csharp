using System.Buffers;

namespace OneScope.Sdk;

/// <summary>
/// The time model for one source: a cursor over its steps with exact seeking. It keeps keyframe snapshots every
/// <see cref="ScopeHistoryOptions.KeyframeInterval"/> steps and re-simulates between them, so the world at step k has the same
/// <see cref="StateHash"/> however the cursor got there. Nothing here reads a clock; time is a step index.
/// Not thread safe.
/// </summary>
public sealed class History
{
    private sealed class Keyframe(long step, byte[] bytes)
    {
        public long Step { get; } = step;
        public byte[] Bytes { get; } = bytes;
    }

    private readonly ScopeHistoryOptions _options;
    private readonly List<Keyframe> _keyframes = [];
    private readonly ArrayBufferWriter<byte> _scratch = new();
    private readonly Dictionary<string, TrackData> _tracks = new(StringComparer.Ordinal);
    private readonly List<TrackData> _trackList = [];
    private readonly List<Finding> _findings = [];
    private readonly List<Branch> _branches = [];
    private readonly TimelineSink _sink;
    private readonly TimelineSink _discard;
    private IScopeWorld _world;
    private IScopeWorld? _view;
    private long _step;
    private bool _worldValid = true;
    private string _viewAxis = AxisIds.Time;
    private int _viewPosition;
    private long _scanned;
    private long? _firstMismatch;

    /// <summary>Creates a history at the recording's <see cref="Recording.StartStep"/>.</summary>
    /// <exception cref="ArgumentException">The source does not declare the time axis, or its new world is not at the recording's start step.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public History(IScopeSource source, ScopeHistoryOptions? options = null)
        : this(source, options, null, null)
    {
    }

    /// <summary>The 0.1 constructor, kept for modules built against SDK 0.1. Same as the one taking <see cref="ScopeHistoryOptions"/>.</summary>
    /// <param name="source">The source to play.</param>
    /// <param name="options">Limits and keyframe spacing; null for the defaults.</param>
    [Obsolete("Pass a ScopeHistoryOptions. This overload is kept for modules built against SDK 0.1 and will be removed before 1.0.")]
    public History(IScopeSource source, HistoryOptions? options)
        : this(source, (ScopeHistoryOptions?)options, null, null)
    {
    }

    private History(IScopeSource source, ScopeHistoryOptions? options, History? seedFrom, long? seedThrough)
    {
        ArgumentNullException.ThrowIfNull(source);
        _options = options ?? new ScopeHistoryOptions();
        if (_options.KeyframeInterval < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.KeyframeInterval, "KeyframeInterval must be at least 1.");
        }

        if (_options.MaxKeyframes < 2 || _options.MaxTrackSamples < 1 || _options.MaxFindings < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxKeyframes must be at least 2; MaxTrackSamples and MaxFindings at least 1.");
        }

        Source = source;
        Recording = source.Recording;
        bool hasTime = false;
        foreach (AxisInfo axis in source.Info.Axes)
        {
            hasTime |= axis.Id == AxisIds.Time;
        }

        if (!hasTime)
        {
            throw new ArgumentException("The source must declare the time axis.", nameof(source));
        }

        foreach (TrackInfo info in source.Info.Tracks)
        {
            var data = new TrackData(info, _options.MaxTrackSamples);
            _tracks.Add(info.Id, data);
            _trackList.Add(data);
        }

        _sink = new TimelineSink(_tracks, _findings, _options.MaxFindings);
        _discard = new TimelineSink(_tracks, _findings, _options.MaxFindings);
        _discard.Begin(0, recording: false);
        _world = source.CreateWorld();
        if (_world.Step != Recording.StartStep)
        {
            throw new ArgumentException($"A new world must be at the recording's start step {Recording.StartStep}, but it is at {_world.Step}.", nameof(source));
        }

        _step = Recording.StartStep;
        _scanned = Recording.StartStep;
        AddKeyframe(Recording.StartStep);
        if (seedFrom is not null)
        {
            long through = seedThrough!.Value;
            foreach (Keyframe k in seedFrom._keyframes)
            {
                if (k.Step > Recording.StartStep && k.Step <= through)
                {
                    InsertKeyframe(k);
                }
            }

            _scanned = Math.Min(through, seedFrom._scanned);
            foreach (TrackData t in seedFrom._trackList)
            {
                _tracks[t.Info.Id].CopyFrom(t, _scanned);
            }

            foreach (Finding f in seedFrom._findings)
            {
                if (f.Step <= _scanned)
                {
                    _findings.Add(f);
                }
            }
        }
    }

    /// <summary>The source this history plays.</summary>
    public IScopeSource Source { get; }

    /// <summary>The source's recording. For a fork it grows as the fork is stepped.</summary>
    public Recording Recording { get; }

    /// <summary>The cursor: the step the world is at, <see cref="StartStep"/> to <see cref="EndStep"/>.</summary>
    public long CurrentStep => _step;

    /// <summary>The first step the history can show: 0 for a replay from the beginning, the host's step for a live attach.</summary>
    public long StartStep => Recording.StartStep;

    /// <summary>The last step recorded, and the highest step the cursor can reach. Grows for a fork that is stepped or a live source.</summary>
    public long EndStep => Recording.EndStep;

    /// <summary>The number of steps recorded after <see cref="StartStep"/>.</summary>
    public long StepCount => Recording.StepCount;

    /// <summary>
    /// The first step at which the world's hash disagreed with the hash the file recorded for it (see
    /// <see cref="Recording.ExpectedHashes"/>), or null while none has. Checked the first time each step is run, so
    /// <see cref="Scan"/> or playing to the end covers the whole file. A finding with code <see cref="MismatchCode"/> marks it on the timeline.
    /// </summary>
    public long? FirstHashMismatch => _firstMismatch;

    /// <summary>The finding code raised at the first step whose hash differs from the file's recorded hash.</summary>
    public const string MismatchCode = "replay.hash-mismatch";

    /// <summary>The world at <see cref="CurrentStep"/>, always at axis position 0. Valid until the next seek or step.</summary>
    public IScopeWorld World => _world;

    /// <summary>
    /// What to show: the world at <see cref="CurrentStep"/>, or when a non-time axis position is set, a scratch world partway
    /// through the step that follows. Valid until the next seek or step.
    /// </summary>
    public IScopeWorld View => _view ?? _world;

    /// <summary>The axis the view is on: <see cref="AxisIds.Time"/>, or the non-time axis last sought.</summary>
    public string ViewAxis => _viewAxis;

    /// <summary>The position along <see cref="ViewAxis"/> inside the current step; 0 on the time axis.</summary>
    public int ViewPosition => _viewPosition;

    /// <summary>The hash of the world at <see cref="CurrentStep"/>. Unaffected by non-time axis positions.</summary>
    public ulong StateHash => _world.Hash();

    /// <summary>The number of keyframes held.</summary>
    public int KeyframeCount => _keyframes.Count;

    /// <summary>The total bytes of the keyframes held.</summary>
    public long KeyframeBytes
    {
        get
        {
            long total = 0;
            foreach (Keyframe k in _keyframes)
            {
                total += k.Bytes.Length;
            }

            return total;
        }
    }

    /// <summary>The steps of the keyframes held, ascending.</summary>
    public IEnumerable<long> KeyframeSteps
    {
        get
        {
            foreach (Keyframe k in _keyframes)
            {
                yield return k.Step;
            }
        }
    }

    /// <summary>The highest step whose timeline data has been recorded.</summary>
    public long ScannedThrough => _scanned;

    /// <summary>The timeline tracks, in declaration order. Filled as steps are played; see <see cref="Scan"/>.</summary>
    public IReadOnlyList<TrackData> Tracks => _trackList;

    /// <summary>Findings raised so far, ordered by step.</summary>
    public IReadOnlyList<Finding> Findings => _findings;

    /// <summary>Forks made from this history, in creation order.</summary>
    public IReadOnlyList<Branch> Branches => _branches;

    /// <summary>
    /// Moves the cursor to <paramref name="step"/> exactly. Restores the nearest keyframe at or before it (or just
    /// steps on when the cursor is already between that keyframe and the target) and re-simulates. Clears any axis position.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not in <see cref="StartStep"/> to <see cref="EndStep"/>.</exception>
    public void Seek(long step)
    {
        if (step < StartStep || step > EndStep)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Step must be {StartStep} to {EndStep}.");
        }

        ClearView();
        int index = KeyframeAtOrBefore(step);
        Keyframe kf = _keyframes[index];
        if (!_worldValid || step < _step || _step < kf.Step)
        {
            _world.Restore(kf.Bytes);
            if (_world.Step != kf.Step)
            {
                throw new InvalidOperationException($"The module restored step {_world.Step} from a keyframe taken at step {kf.Step}.");
            }

            _step = kf.Step;
            _worldValid = true;
        }

        while (_step < step)
        {
            AdvanceOne(Recording.InputAt(_step).Span);
        }
    }

    /// <summary>
    /// Moves along an axis. On the time axis <paramref name="position"/> is a step (same as <see cref="Seek(long)"/>).
    /// On another axis it is a position inside the step after the cursor; the time cursor does not move and
    /// <see cref="StateHash"/> does not change. Position 0 clears the axis view.
    /// </summary>
    /// <exception cref="ArgumentException">The axis is not declared.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the axis.</exception>
    /// <exception cref="InvalidOperationException">The cursor is at the last recorded step, so no step follows.</exception>
    public void Seek(string axis, long position)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (axis == AxisIds.Time)
        {
            Seek(position);
            return;
        }

        AxisInfo info = FindAxis(axis);
        if ((ulong)position > (ulong)info.PositionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Axis '{axis}' has positions 0 to {info.PositionCount}.");
        }

        if (position == 0)
        {
            ClearView();
            return;
        }

        if (_step >= EndStep)
        {
            throw new InvalidOperationException("No step follows the cursor, so there is nothing to step through on this axis.");
        }

        if (!_worldValid)
        {
            Seek(_step);
        }

        _scratch.ResetWrittenCount();
        _world.WriteSnapshot(_scratch);
        _view ??= Source.CreateWorld();
        _view.Restore(_scratch.WrittenSpan);
        _view.AdvanceAxis(axis, (int)position, Recording.InputAt(_step).Span);
        _viewAxis = axis;
        _viewPosition = (int)position;
    }

    /// <summary>Steps the time axis forward one step. Returns false, and does nothing, at the end of the recording.</summary>
    public bool Step() => Step(AxisIds.Time, 1);

    /// <summary>
    /// Moves <paramref name="delta"/> along an axis from where it is now (negative moves back). Returns false, and does
    /// nothing, when the result would fall outside the axis.
    /// </summary>
    /// <exception cref="ArgumentException">The axis is not declared.</exception>
    public bool Step(string axis, long delta = 1)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (axis == AxisIds.Time)
        {
            long target = _step + delta;
            if (target < StartStep || target > EndStep)
            {
                return false;
            }

            Seek(target);
            return true;
        }

        AxisInfo info = FindAxis(axis);
        long current = _viewAxis == axis ? _viewPosition : 0;
        long next = current + delta;
        if (next < 0 || next > info.PositionCount || (next > 0 && _step >= EndStep))
        {
            return false;
        }

        Seek(axis, next);
        return true;
    }

    /// <summary>
    /// Plays from the cursor to <paramref name="throughStep"/> (or the end) so the tracks and findings cover it, then
    /// returns the cursor to where it was.
    /// </summary>
    public void Scan(long throughStep = long.MaxValue)
    {
        long back = _step;
        long target = Math.Min(throughStep, EndStep);
        if (target > _scanned)
        {
            Seek(Math.Min(back, _scanned));
            Seek(target);
        }

        Seek(back);
    }

    /// <summary>
    /// Forks the world at <paramref name="step"/>. The branch has its own source, recording and history, seeded from this
    /// history's keyframes and timeline data; pokes apply only to it. This history is not changed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not in <see cref="StartStep"/> to <see cref="EndStep"/>.</exception>
    public Branch Fork(long step)
    {
        Recording forked = Recording.Fork(step);
        IScopeSource source = Source.WithRecording(forked);
        var child = new History(source, _options, this, step);
        child.Seek(step);
        var branch = new Branch(this, child, step);
        _branches.Add(branch);
        return branch;
    }

    /// <summary>
    /// Registers <paramref name="child"/>, a history over a writable recording rebuilt from a saved fork's replay, as a branch of this
    /// one forked at <paramref name="forkStep"/>. Reopening a session uses it so a restored fork can be poked and stepped like the original.
    /// </summary>
    /// <exception cref="ArgumentException">The child's recording is read-only, starts at another step, ends before the fork step, or
    /// differs from this recording before <paramref name="forkStep"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="forkStep"/> is outside this history.</exception>
    public Branch Adopt(History child, long forkStep)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (forkStep < StartStep || forkStep > EndStep)
        {
            throw new ArgumentOutOfRangeException(nameof(forkStep), forkStep, $"Step must be {StartStep} to {EndStep}.");
        }

        if (!child.Recording.IsWritable || child.StartStep != StartStep || child.EndStep < forkStep)
        {
            throw new ArgumentException($"The child must be writable, start at step {StartStep} and reach step {forkStep}.", nameof(child));
        }

        if (Recording.FirstDifference(child.Recording) is long first && first < forkStep)
        {
            throw new ArgumentException($"The child's inputs differ from this recording at step {first}, before the fork step {forkStep}.", nameof(child));
        }

        var branch = new Branch(this, child, forkStep);
        _branches.Add(branch);
        return branch;
    }

    /// <summary>
    /// Moves both histories to <paramref name="step"/> and returns the first difference between their worlds, or null.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The step is outside either history.</exception>
    public Difference? Diff(History other, long step)
    {
        ArgumentNullException.ThrowIfNull(other);
        Seek(step);
        other.Seek(step);
        return _world.FirstDifference(other._world);
    }

    internal void AppendAndAdvance(ReadOnlySpan<byte> input)
    {
        if (_step != EndStep)
        {
            throw new InvalidOperationException("Only the end of a recording can be extended.");
        }

        ClearView();
        AdvanceOne(input);
        Recording.Append(input);
    }

    private void AdvanceOne(ReadOnlySpan<byte> input)
    {
        long reached = _step + 1;
        bool recording = reached > _scanned;
        TimelineSink sink = _sink;
        if (recording)
        {
            sink.Begin(reached, recording: true);
        }
        else
        {
            sink = _discard;
        }

        _worldValid = false;
        _world.Advance(input, sink);
        if (_world.Step != reached)
        {
            throw new InvalidOperationException($"The module advanced to step {_world.Step}; expected {reached}.");
        }

        _worldValid = true;
        _step = reached;
        if (recording)
        {
            _scanned = reached;
            CheckRecordedHash(reached, sink);
        }

        if (reached % _options.KeyframeInterval == 0)
        {
            AddKeyframe(reached);
        }
    }

    private void CheckRecordedHash(long reached, TimelineSink sink)
    {
        long i = reached - Recording.StartStep;
        ReadOnlySpan<ulong> expected = Recording.ExpectedHashes.Span;
        if (_firstMismatch is null && (ulong)i < (ulong)expected.Length && expected[(int)i] != _world.Hash())
        {
            _firstMismatch = reached;
            sink.Finding(
                MismatchCode,
                Severity.Error,
                [],
                $"The world's hash at step {reached} is {_world.Hash():x16}; the file recorded {expected[(int)i]:x16}. Playing it here does not reproduce the recording from this step on.");
        }
    }

    private void ClearView()
    {
        _viewAxis = AxisIds.Time;
        _viewPosition = 0;
        _view = null;
    }

    private AxisInfo FindAxis(string axis)
    {
        foreach (AxisInfo info in Source.Info.Axes)
        {
            if (info.Id == axis)
            {
                return info;
            }
        }

        throw new ArgumentException($"The source declares no axis '{axis}'.", nameof(axis));
    }

    private int KeyframeAtOrBefore(long step)
    {
        int lo = 0;
        int hi = _keyframes.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_keyframes[mid].Step <= step)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private void AddKeyframe(long step)
    {
        int i = _keyframes.Count == 0 ? -1 : KeyframeAtOrBefore(step);
        if (i >= 0 && _keyframes[i].Step == step)
        {
            return;
        }

        _scratch.ResetWrittenCount();
        _world.WriteSnapshot(_scratch);
        InsertKeyframe(new Keyframe(step, _scratch.WrittenSpan.ToArray()));
    }

    private void InsertKeyframe(Keyframe frame)
    {
        int i = _keyframes.Count == 0 ? -1 : KeyframeAtOrBefore(frame.Step);
        if (i >= 0 && _keyframes[i].Step == frame.Step)
        {
            return;
        }

        _keyframes.Insert(i + 1, frame);
        while (_keyframes.Count > _options.MaxKeyframes)
        {
            Evict();
        }
    }

    // Drops the interior keyframe whose neighbours are closest together, so the longest re-simulation stays as short as it can.
    private void Evict()
    {
        int best = -1;
        long bestGap = long.MaxValue;
        for (int i = 1; i < _keyframes.Count - 1; i++)
        {
            long gap = _keyframes[i + 1].Step - _keyframes[i - 1].Step;
            if (gap < bestGap)
            {
                bestGap = gap;
                best = i;
            }
        }

        _keyframes.RemoveAt(best);
    }
}
