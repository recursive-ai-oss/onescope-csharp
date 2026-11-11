using OneGame.Control;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>Operations that change how sources sit on the shared axis or leave it: offset and close.</summary>
internal sealed partial class ScopeEndpoint
{
    private string OffsetOp(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        if (a.Long("offset") is long offset)
        {
            if (Math.Abs(offset) > 1L << 40)
            {
                throw new ControlException("bad_args", "Argument 'offset' must be between -1099511627776 and 1099511627776 steps.");
            }

            s.Offset = offset;
            _shell.Seek(Math.Clamp(_shell.Step, _shell.MinSteps, _shell.MaxSteps));
        }

        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("source", s.Index);
            w.WriteNumber("offset", s.Offset);
            w.WriteNumber("step", _shell.Step);
            w.WriteNumber("sourceStep", s.History.CurrentStep);
            w.WriteNumber("start", _shell.MinSteps);
            w.WriteNumber("steps", _shell.MaxSteps);
            w.WriteString("hash", JsonOut.Hex(s.History.StateHash));
            w.WriteEndObject();
        });
    }

    private string CloseOp(Args a)
    {
        RequireSources();
        SourceSession s = SourceArg(a);
        var before = _shell.Sources.Select(x => (Session: x, Old: x.Index)).ToList();
        IReadOnlyList<ClosedSource> closed = _shell.Close(s);
        return JsonOut.Build(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("closed");
            foreach (ClosedSource c in closed)
            {
                w.WriteStartObject();
                w.WriteNumber("source", c.Index);
                w.WriteString("label", c.Label);
                w.WriteBoolean("fork", c.Fork);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("renumbered");
            foreach ((SourceSession session, int old) in before)
            {
                if (_shell.Sources.Contains(session) && session.Index != old)
                {
                    w.WriteStartObject();
                    w.WriteNumber("from", old);
                    w.WriteNumber("to", session.Index);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteNumber("sources", _shell.Sources.Count);
            w.WriteNumber("active", _shell.Sources.Count == 0 ? -1 : _shell.ActiveSource);
            w.WriteNumber("views", _shell.Views.Count);
            w.WriteNumber("step", _shell.Step);
            w.WriteEndObject();
        });
    }
}
