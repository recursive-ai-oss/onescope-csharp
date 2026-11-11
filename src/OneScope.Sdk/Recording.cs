namespace OneScope.Sdk;

/// <summary>
/// A world's setup plus one opaque input per step: what a replay holds. A world at step k is the setup plus
/// inputs 0 to k-1 applied in order. Recordings opened from a file are read-only; a fork gets its own writable copy.
/// Only the module interprets the bytes.
/// </summary>
public sealed class Recording
{
    /// <summary>The most bytes one step's input may hold.</summary>
    public const int MaxInputBytes = 1 << 20;

    private readonly List<ReadOnlyMemory<byte>> _inputs;

    private Recording(ReadOnlyMemory<byte> setup, List<ReadOnlyMemory<byte>> inputs, bool writable)
    {
        Setup = setup;
        _inputs = inputs;
        IsWritable = writable;
    }

    /// <summary>The module's setup bytes (scene, initial state, parameters).</summary>
    public ReadOnlyMemory<byte> Setup { get; }

    /// <summary>The number of steps recorded; the world can reach step 0 to this value.</summary>
    public long StepCount => _inputs.Count;

    /// <summary>True when <see cref="Append"/> is allowed (forks only).</summary>
    public bool IsWritable { get; }

    /// <summary>Creates a read-only recording. The memory is not copied; do not change it afterwards.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An input is longer than <see cref="MaxInputBytes"/>.</exception>
    public static Recording Create(ReadOnlyMemory<byte> setup, IEnumerable<ReadOnlyMemory<byte>> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        List<ReadOnlyMemory<byte>> list = [.. inputs];
        foreach (ReadOnlyMemory<byte> input in list)
        {
            CheckLength(input);
        }

        return new Recording(setup, list, writable: false);
    }

    /// <summary>The input run by the step that takes the world from <paramref name="step"/> to <c>step + 1</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not in 0 to <see cref="StepCount"/> - 1.</exception>
    public ReadOnlyMemory<byte> InputAt(long step)
    {
        if ((ulong)step >= (ulong)_inputs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Step must be 0 to {_inputs.Count - 1}.");
        }

        return _inputs[(int)step];
    }

    /// <summary>Makes a writable recording with the same setup and the inputs before <paramref name="step"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not in 0 to <see cref="StepCount"/>.</exception>
    public Recording Fork(long step)
    {
        if ((ulong)step > (ulong)_inputs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Step must be 0 to {_inputs.Count}.");
        }

        return new Recording(Setup, _inputs.GetRange(0, (int)step), writable: true);
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
    /// The first step whose input differs from <paramref name="other"/>'s, comparing the steps both hold,
    /// or null when every shared step has equal input. Worlds built from the two are equal up to and including that step
    /// and may differ at the next one.
    /// </summary>
    public long? FirstDifference(Recording other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int shared = Math.Min(_inputs.Count, other._inputs.Count);
        for (int i = 0; i < shared; i++)
        {
            if (!_inputs[i].Span.SequenceEqual(other._inputs[i].Span))
            {
                return i;
            }
        }

        return null;
    }

    private static void CheckLength(ReadOnlyMemory<byte> input)
    {
        if (input.Length > MaxInputBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.Length, $"An input may hold at most {MaxInputBytes} bytes.");
        }
    }
}
