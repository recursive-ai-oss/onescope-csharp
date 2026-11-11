using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using OneScope.Attach;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Operations that move through time: seek, step and play.</summary>
internal sealed partial class ScopeEndpoint
{
    private string Seek(Args a)
    {
        string axis = a.String("axis") ?? AxisIds.Time;
        if (axis == AxisIds.Time)
        {
            long step = a.Long("position") ?? a.Long("step") ?? throw new ControlException("bad_args", $"seek needs 'step' (an integer from {_shell.MinSteps} to {_shell.MaxSteps}).");
            RequireSources();
            _shell.Seek(step);
        }
        else
        {
            long position = a.Long("position") ?? throw new ControlException("bad_args", $"seek on axis '{axis}' needs 'position'.");
            RequireSources();
            _shell.SeekAxis(a.Int("source") ?? _shell.ActiveSource, axis, position);
        }

        return Cursor();
    }

    private string StepOp(Args a)
    {
        RequireSources();
        long delta = a.Long("delta") ?? 1;
        string axis = a.String("axis") ?? AxisIds.Time;
        if (axis == AxisIds.Time)
        {
            // Stepping forward on a fork that is at the end of its recording runs new steps in it (carrying queued pokes);
            // everything else moves the shared cursor.
            if (delta > 1_000_000 || delta < -1_000_000_000)
            {
                throw new ControlException("bad_args", "Argument 'delta' is too large; step forward at most 1000000 at a time.");
            }

            _shell.StepTime(delta, SourceArg(a));
        }
        else
        {
            SourceSession s = SourceArg(a);
            if (!s.Info.Axes.Any(x => x.Id == axis))
            {
                throw new ControlException("bad_args", $"Source {s.Index} has no axis '{axis}'.");
            }

            try
            {
                s.History.Step(axis, delta);
            }
            catch (InvalidOperationException ex)
            {
                throw new ControlException("rejected", ex.Message);
            }

            _shell.Touch();
        }

        return Cursor();
    }

    private string Play(Args a)
    {
        double? speed = a.Double("speed");
        if (speed is not null)
        {
            if (Math.Abs(speed.Value) > 1e6)
            {
                throw new ControlException("bad_args", "Argument 'speed' must be between -1000000 and 1000000 steps per second.");
            }

            _shell.SetSpeed((long)Math.Round(speed.Value * 1000));
        }

        if (a.Bool("loop") is bool loop)
        {
            _shell.Loop = loop;
        }

        int frames = a.Int("frames") ?? 0;
        if (frames is < 0 or > 1_000_000)
        {
            throw new ControlException("bad_args", "Argument 'frames' must be 0 to 1000000.");
        }

        long moved = _shell.AdvancePlayback(frames);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("speed", _shell.SpeedMilliStepsPerSecond / 1000.0);
            w.WriteBoolean("playing", _shell.SpeedMilliStepsPerSecond != 0);
            w.WriteBoolean("loop", _shell.Loop);
            w.WriteNumber("frames", frames);
            w.WriteNumber("moved", moved);
            w.WriteNumber("step", _shell.Step);
            w.WriteStartArray("hashes");
            foreach (SourceSession s in _shell.Sources)
            {
                w.WriteStringValue(JsonOut.Hex(s.History.StateHash));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
