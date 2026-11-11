using System.Globalization;
using System.Numerics;
using OneGame.Control;
using OneGame.UI;
using OneGame.UI.Kit;
using OneScope.Sdk;
using EntityId = OneScope.Sdk.EntityId;

namespace OneScope.App;

/// <summary>
/// The window's way to poke a fork: one row per poke the module declares, with a field for each typed parameter and an Apply button.
/// The rows come from the module's declarations, so a new module's pokes appear without any app change. A poke is queued on the fork and,
/// with "run a step" on, applied by one step of the fork, exactly as the poke operation does with <c>step:true</c>.
/// </summary>
internal sealed class PokePanel
{
    private readonly ShellModel _shell;
    private readonly Action<Action> _run;
    private readonly Dictionary<string, string> _fields = [];
    private readonly List<PokeRow> _rows = [];
    private (int Source, int Count, int Epoch) _rowsKey = (-1, -1, -1);

    /// <summary>One declared poke of one fork.</summary>
    private readonly record struct PokeRow(int Source, PokeInfo Poke);

    public PokePanel(ShellModel shell, Action<Action> run)
    {
        _shell = shell;
        _run = run;
    }

    /// <summary>Whether a step follows each applied poke.</summary>
    public bool StepAfter { get; set; } = true;

    /// <summary>The panel shows for a fork: an opened file can't be poked.</summary>
    public bool Visible => _shell.Sources.Count > 0 && _shell.Solo < 0 && _shell.Source(Math.Min(_shell.ActiveSource, _shell.Sources.Count - 1)).IsFork;

    /// <summary>Builds the panel's element tree once; the rows follow the active fork.</summary>
    public Element Build()
    {
        // The title is a button: collapsed, the panel is one line and the inspector below gets the room. Open, it scrolls when the column is short.
        Element title = UiKit.Button(() => Collapsed ? "Pokes  [+]" : "Pokes  [-]", () => Collapsed = !Collapsed, "poke/collapse", ButtonKind.Flat)
            .Padding(Length.Px(2), Length.Px(8)).Class("panel-title");
        Element choices = Ui.Row().Wrap().Gap(Length.Px(3)).BindChildren(Rows, ChoiceButton);
        Element fields = Ui.Column().Gap(Length.Px(3)).BindChildren(Params, BuildField);
        Element footer = Ui.Row(
                UiKit.Button("Apply", () => _run(() => Apply(Current())), "poke/apply"),
                UiKit.Toggle("Step after poke", () => StepAfter, on => StepAfter = on, "poke/step-after"),
                UiKit.Button("Step", () => _run(StepActive), "poke/step"))
            .Wrap().Align(Align.Center).Gap(Length.Px(6));
        Element body = Ui.Column(choices, fields, footer).Padding(Length.Px(6)).Gap(Length.Px(6)).Shrink(1).Style(st => st.MinHeight(Length.Px(0)))
            .Overflow(OverflowMode.Auto).Named("pokes-body").BindVisible(() => !Collapsed);
        return Ui.Column(title, body)
            .Class("panel").Class("tight").Named("pokes-panel").BindVisible(() => Visible).Shrink(1).Style(st => st.MinHeight(Length.Px(30)));
    }

    /// <summary>Whether the panel shows only its title.</summary>
    public bool Collapsed { get; set; }

    private IReadOnlyList<PokeRow> Rows()
    {
        int active = Math.Min(_shell.ActiveSource, _shell.Sources.Count - 1);
        var key = (active, _shell.Sources.Count, _shell.SourceEpoch);
        if (key != _rowsKey)
        {
            _rowsKey = key;
            _rows.Clear();
            if (active >= 0 && _shell.Source(active).IsFork)
            {
                foreach (PokeInfo p in _shell.Source(active).Info.Pokes)
                {
                    _rows.Add(new PokeRow(active, p));
                }
            }
        }

        return _rows;
    }

    /// <summary>The poke the fields below edit: the one picked, else the first declared.</summary>
    private PokeRow Current()
    {
        IReadOnlyList<PokeRow> rows = Rows();
        if (rows.Count == 0)
        {
            throw new ControlException("rejected", "The active source declares no pokes.");
        }

        foreach (PokeRow r in rows)
        {
            if (r.Poke.Id == _picked)
            {
                return r;
            }
        }

        return rows[0];
    }

    private readonly List<ParamRow> _params = [];
    private (int Source, string Poke) _paramsKey = (-1, string.Empty);
    private string _picked = string.Empty;

    /// <summary>One typed parameter of the picked poke.</summary>
    private readonly record struct ParamRow(PokeRow Row, ParamInfo Param);

    private IReadOnlyList<ParamRow> Params()
    {
        IReadOnlyList<PokeRow> rows = Rows();
        if (rows.Count == 0)
        {
            _params.Clear();
            _paramsKey = (-1, string.Empty);
            return _params;
        }

        PokeRow current = Current();
        var key = (current.Source, current.Poke.Id);
        if (key != _paramsKey)
        {
            _paramsKey = key;
            _params.Clear();
            foreach (ParamInfo p in current.Poke.Params)
            {
                _params.Add(new ParamRow(current, p));
            }
        }

        return _params;
    }

    private Element ChoiceButton(PokeRow row) =>
        UiKit.ToggleButton(row.Poke.Name, () => Rows().Count > 0 && Current().Poke.Id == row.Poke.Id, on =>
        {
            if (on)
            {
                _picked = row.Poke.Id;
            }
        }, "poke/pick/" + row.Poke.Id);

    private Element BuildField(ParamRow item)
    {
        PokeRow row = item.Row;
        ParamInfo p = item.Param;
        string key = row.Poke.Id + "/" + p.Name;
        Element label = Ui.Label(p.Name).Width(Length.Px(70));
        Element input;
        if (p.Kind == ValueKind.Bool)
        {
            input = UiKit.Toggle(p.Name, () => Text(row, p) == "true", on => _fields[key] = on ? "true" : "false", "poke/" + key);
        }
        else
        {
            string placeholder = p.Kind switch
            {
                ValueKind.Entity => "id, or the selection",
                ValueKind.Vector => "x,y,z",
                _ => p.Kind.ToString().ToLowerInvariant(),
            };
            input = UiKit.TextInput(() => Text(row, p), t => _fields[key] = t, placeholder, id: "poke/" + key).Grow(1).MinWidth(0);
        }

        return Ui.Row(label, input).Align(Align.Center).Gap(Length.Px(4));
    }

    /// <summary>What a field shows: what the user typed, else a default (a vector starts at the active view's centre, so a spawn lands where you are looking).</summary>
    private string Text(PokeRow row, ParamInfo p)
    {
        if (_fields.TryGetValue(row.Poke.Id + "/" + p.Name, out string? typed))
        {
            return typed;
        }

        return p.Kind switch
        {
            ValueKind.Vector => ViewCentre(),
            ValueKind.Bool => "false",
            _ => string.Empty,
        };
    }

    private string ViewCentre()
    {
        if (_shell.ActiveView < _shell.Views.Count)
        {
            Vector3 t = _shell.Views[_shell.ActiveView].Target;
            return string.Create(CultureInfo.InvariantCulture, $"{MathF.Round(t.X)},{MathF.Round(t.Y)},{MathF.Round(t.Z)}");
        }

        return "0,0,0";
    }

    private void StepActive()
    {
        SourceSession s = _shell.Source(Math.Min(_shell.ActiveSource, _shell.Sources.Count - 1));
        ShellModel.RequireFork(s);
        _shell.Seek(s.History.EndStep);
        _shell.StepFork(s, 1);
    }

    private void Apply(PokeRow row)
    {
        SourceSession s = _shell.Source(row.Source);
        ShellModel.RequireFork(s);
        var values = new ScopeValue[row.Poke.Params.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Parse(row.Poke, row.Poke.Params[i], Text(row, row.Poke.Params[i]));
        }

        // Pokes apply at the end of the fork's recording, so a fork scrubbed back first runs to its end, as a drag does.
        _shell.Seek(s.History.EndStep);
        _shell.Poke(s, new PokeCall(row.Poke.Id, values));
        if (StepAfter)
        {
            _shell.StepFork(s, 1);
        }

        _shell.AddLog(s.Index, "poke", $"{row.Poke.Id} applied at step {s.History.CurrentStep}");
        _shell.Touch();
    }

    private ScopeValue Parse(PokeInfo poke, ParamInfo p, string text)
    {
        string where = $"Poke '{poke.Id}' argument '{p.Name}'";
        string t = text.Trim();
        switch (p.Kind)
        {
            case ValueKind.Entity:
                if (t.Length == 0)
                {
                    return _shell.Selected.IsSome
                        ? ScopeValue.OfEntity(_shell.Selected)
                        : throw new ControlException("bad_args", $"{where} needs an entity: select one in a view or the tree, or type its id.");
                }

                return long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out long e) && e > 0
                    ? ScopeValue.OfEntity(new EntityId((ulong)e))
                    : throw new ControlException("bad_args", $"{where} must be a positive entity id, found '{t}'.");
            case ValueKind.Integer:
                return long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)
                    ? ScopeValue.OfInteger(l)
                    : throw new ControlException("bad_args", $"{where} must be an integer, found '{t}'.");
            case ValueKind.Number:
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
                    ? ScopeValue.OfNumber(d)
                    : throw new ControlException("bad_args", $"{where} must be a finite number, found '{t}'.");
            case ValueKind.Bool:
                return ScopeValue.OfBool(t == "true");
            case ValueKind.Text:
                return ScopeValue.OfText(text);
            case ValueKind.Vector:
                {
                    string[] parts = t.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is not (2 or 3))
                    {
                        throw new ControlException("bad_args", $"{where} must be 2 or 3 numbers like 100,200,0, found '{t}'.");
                    }

                    Span<float> c = stackalloc float[3];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f))
                        {
                            throw new ControlException("bad_args", $"{where} has '{parts[i]}', which is not a finite number.");
                        }

                        c[i] = f;
                    }

                    return ScopeValue.OfVector(new Vector3(c[0], c[1], c[2]));
                }

            default:
                throw new ControlException("bad_args", $"{where} has a kind the window can't edit ({p.Kind}); use the poke operation.");
        }
    }
}
