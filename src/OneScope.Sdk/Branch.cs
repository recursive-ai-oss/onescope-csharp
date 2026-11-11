using System.Buffers;

namespace OneScope.Sdk;

/// <summary>
/// A fork of a <see cref="History"/>: its own recording and history that starts as a copy of the original up to the fork
/// step. Pokes and steps are recorded in the branch's recording only, so replaying that recording reproduces the branch.
/// </summary>
public sealed class Branch
{
    private readonly ArrayBufferWriter<byte> _pending = new();

    internal Branch(History parent, History history, long forkStep)
    {
        Parent = parent;
        History = history;
        ForkStep = forkStep;
    }

    /// <summary>The history this branch was forked from.</summary>
    public History Parent { get; }

    /// <summary>The branch's own history. Its recording is the branch's replay.</summary>
    public History History { get; }

    /// <summary>The step the branch was forked at.</summary>
    public long ForkStep { get; }

    /// <summary>The number of pokes queued for the next step.</summary>
    public int PendingPokes { get; private set; }

    /// <summary>
    /// The first step whose recorded input differs from the original's, or null while the branch has done nothing different.
    /// The two worlds are equal through that step and may differ from the next one. Drawn as the branch point on the timeline.
    /// </summary>
    public long? DivergenceStep => History.Recording.FirstDifference(Parent.Recording);

    /// <summary>
    /// Queues a poke for the branch's next step. The poke takes effect when the branch is stepped. Only possible with the
    /// cursor at the end of the branch's recording.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cursor is not at the end of the branch's recording.</exception>
    /// <exception cref="ArgumentException">The poke is not valid for the current state.</exception>
    public void Poke(in PokeCall call)
    {
        RequireHead();
        History.World.EncodePoke(call, _pending);
        PendingPokes++;
    }

    /// <summary>Drops queued pokes without applying them.</summary>
    public void ClearPending()
    {
        _pending.ResetWrittenCount();
        PendingPokes = 0;
    }

    /// <summary>
    /// Runs one step at the end of the branch's recording with the queued pokes as its input, and records them. Returns true.
    /// With the cursor earlier in the branch's recording, replays one recorded step instead and returns false at the end.
    /// </summary>
    public bool Step()
    {
        if (History.CurrentStep < History.EndStep)
        {
            return History.Step();
        }

        History.AppendAndAdvance(_pending.WrittenSpan);
        ClearPending();
        return true;
    }

    private void RequireHead()
    {
        if (History.CurrentStep != History.EndStep)
        {
            throw new InvalidOperationException("Seek the branch to the end of its recording before poking.");
        }
    }
}
