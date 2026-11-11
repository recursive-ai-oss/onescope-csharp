namespace OneScope.Sdk;

/// <summary>
/// A world's setup plus one opaque input per step: what a replay holds. The world starts at <see cref="StartStep"/>
/// (0 for a replay from the beginning; the host's step for a live attach) and the world at step k is the setup plus
/// the inputs for steps <see cref="StartStep"/> to k-1 applied in order. Recordings opened from a file are read-only;
/// a fork gets its own writable copy. Only the module interprets the bytes.
/// </summary>
public sealed class Recording
{
    /// <summary>
    /// The most bytes one step's input may hold: 16 MiB, the same as the probe's largest record, so any record a host
    /// sends fits. Inputs are held in memory, so a module reading a file must still bound the number of steps and the total
    /// size it will accept before it allocates (the SDK cannot know a file's size from its inputs).
    /// </summary>
    public const int MaxInputBytes = 16 << 20;

    private readonly List<ReadOnlyMemory<byte>> _inputs;

    private Recording(ReadOnlyMemory<byte> setup, List<ReadOnlyMemory<byte>> inputs, bool writable, long startStep, ReadOnlyMemory<ulong> expectedHashes)
    {
        Setup = setup;
        _inputs = inputs;
        IsWritable = writable;
        StartStep = startStep;
        ExpectedHashes = expectedHashes;
    }

    /// <summary>The module's setup bytes (scene, initial state, parameters).</summary>
    public ReadOnlyMemory<byte> Setup { get; }

    /// <summary>The step the world built from <see cref="Setup"/> is at; the first step the history can show. At least 0.</summary>
    public long StartStep { get; }

    /// <summary>The number of steps recorded after <see cref="StartStep"/>.</summary>
    public long StepCount => _inputs.Count;

    /// <summary>The last step the world can reach: <see cref="StartStep"/> + <see cref="StepCount"/>.</summary>
    public long EndStep => StartStep + _inputs.Count;

    /// <summary>
    /// The per-step world hashes the file recorded, or empty when it recorded none. Entry i is the hash the world must have
    /// at step <see cref="StartStep"/> + i, the same value <see cref="IScopeWorld.Hash"/> returns there. It may be shorter than
    /// <see cref="StepCount"/> + 1; steps past its end are unchecked. The history raises a finding at the first step that
    /// disagrees. A fork's recording has none, since pokes make it differ on purpose.
    /// </summary>
    public ReadOnlyMemory<ulong> ExpectedHashes { get; }

    /// <summary>True when <see cref="Append"/> is allowed (forks only).</summary>
    public bool IsWritable { get; }

    /// <summary>Creates a read-only recording. The memory is not copied; do not change it afterwards.</summary>
    /// <param name="setup">The module's setup bytes.</param>
    /// <param name="inputs">One input per step, the first for step <paramref name="startStep"/>.</param>
    /// <param name="startStep">The step the world starts at.</param>
    /// <param name="expectedHashes">The file's recorded hashes, or empty; see <see cref="ExpectedHashes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An input is longer than <see cref="MaxInputBytes"/>, or <paramref name="startStep"/> is negative.</exception>
    public static Recording Create(ReadOnlyMemory<byte> setup, IEnumerable<ReadOnlyMemory<byte>> inputs, long startStep = 0, ReadOnlyMemory<ulong> expectedHashes = default)
    {
        CheckStart(startStep);
        ArgumentNullException.ThrowIfNull(inputs);
        List<ReadOnlyMemory<byte>> list = [.. inputs];
        foreach (ReadOnlyMemory<byte> input in list)
        {
            CheckLength(input);
        }

        return new Recording(setup, list, writable: false, startStep, expectedHashes);
    }

    /// <summary>The input run by the step that takes the world from <paramref name="step"/> to <c>step + 1</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not in <see cref="StartStep"/> to <see cref="EndStep"/> - 1.</exception>
    public ReadOnlyMemory<byte> InputAt(long step)
    {
        long i = step - StartStep;
        if ((ulong)i >= (ulong)_inputs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Step must be {StartStep} to {EndStep - 1}.");
        }

        return _inputs[(int)i];
    }

    /// <summary>Makes a writable recording with the same setup and the inputs before <paramref name="step"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not in <see cref="StartStep"/> to <see cref="EndStep"/>.</exception>
    public Recording Fork(long step)
    {
        long i = step - StartStep;
        if ((ulong)i > (ulong)_inputs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Step must be {StartStep} to {EndStep}.");
        }

        return new Recording(Setup, _inputs.GetRange(0, (int)i), writable: true, StartStep, default);
    }

    /// <summary>Adds the input for the next step. The bytes are copied.</summary>
    /// <exception cref="InvalidOperationException">The recording is read-only.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The input is longer than <see cref="MaxInputBytes"/>.</exception>
    public void Append(ReadOnlySpan<byte> input)
    {
        if (!IsWritable)
        {
            throw new InvalidOperationException("The recording is read-only; fork it to add steps.");
        }

        if (input.Length > MaxInputBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.Length, $"An input may hold at most {MaxInputBytes} bytes.");
        }

        _inputs.Add(input.IsEmpty ? ReadOnlyMemory<byte>.Empty : input.ToArray());
    }

    /// <summary>
    /// The first step whose input differs from <paramref name="other"/>'s, comparing the steps both hold (by step number,
    /// so recordings with different start steps compare where they overlap), or null when every shared step has equal input. Worlds built from the two are equal up to and including that step
    /// and may differ at the next one.
    /// </summary>
    public long? FirstDifference(Recording other)
    {
        ArgumentNullException.ThrowIfNull(other);
        long from = Math.Max(StartStep, other.StartStep);
        long to = Math.Min(EndStep, other.EndStep);
        for (long step = from; step < to; step++)
        {
            if (!_inputs[(int)(step - StartStep)].Span.SequenceEqual(other._inputs[(int)(step - other.StartStep)].Span))
            {
                return step;
            }
        }

        return null;
    }

    private static void CheckStart(long startStep)
    {
        if (startStep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startStep), startStep, "The start step must be 0 or more.");
        }
    }

    private static void CheckLength(ReadOnlyMemory<byte> input)
    {
        if (input.Length > MaxInputBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.Length, $"An input may hold at most {MaxInputBytes} bytes.");
        }
    }
}
