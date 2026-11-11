using System.Globalization;
using OneGame.UI;
using OneGame.UI.Kit;
using OneScope.Sdk;

namespace OneScope.App;

/// <summary>
/// The compare panel: for the compare pair (a fork and its original, two versions of a module on one file, or a partial world and a whole one) it says whether
/// the worlds agree at the cursor, jumps to the first, next and previous step where they don't, and lists the differing entities and fields.
/// Choosing a row selects the entity, which every view of the pair outlines.
/// </summary>
internal sealed class ComparePanel
{
    private static readonly IReadOnlyList<CompareRow> NoRows = [];
    private readonly ShellModel _shell;
    private readonly Action<Action> _run;

    public ComparePanel(ShellModel shell, Action<Action> run)
    {
        _shell = shell;
        _run = run;
    }

    /// <summary>Whether the panel is on and there is a pair to show.</summary>
    public bool Visible => _shell.Solo < 0 && _shell.Panels.Compare && _shell.EffectivePair is not null;

    public Element Build()
    {
        Element title = Ui.Label("").Class("panel-title").Padding(Length.Px(3), Length.Px(8)).BindText(() => _shell.Version, _ => Title());
        Element status = Ui.Column(
                Ui.Label("").BindText(() => _shell.Version, _ => Status(false)),
                Ui.Label("").BindText(() => _shell.Version, _ => Status(true)))
            .Padding(Length.Px(2), Length.Px(8)).Shrink(0);
        Element jumps = Ui.Row(
                Tight(UiKit.Button("|< First", () => _run(() => Jump("first")), "compare/first")),
                Tight(UiKit.Button("< Prev", () => _run(() => Jump("previous")), "compare/previous")),
                Tight(UiKit.Button("Next >", () => _run(() => Jump("next")), "compare/next")),
                Tight(UiKit.Button("Last >|", () => _run(() => Jump("last")), "compare/last")))
            .Gap(Length.Px(3)).Padding(Length.Px(1), Length.Px(6)).Shrink(0);
        TableView<CompareRow> table = UiKit.Table<CompareRow>(
            [
                new TableColumn<CompareRow>("Entity", r => EntityText(r), ColumnWidth.Fixed(Length.Px(64))),
                new TableColumn<CompareRow>("Field", r => r.Field, ColumnWidth.Flex(1)),
                new TableColumn<CompareRow>("Source", r => r.Value, ColumnWidth.Flex(1.3f)),
                new TableColumn<CompareRow>("Other", r => r.OtherValue, ColumnWidth.Flex(1.3f)),
            ],
            Rows,
            Length.Px(20),
            onSelect: (row, _) => _run(() => _shell.SelectRow(row)),
            id: "compare/rows",
            version: () => _shell.Version);
        return Ui.Column(title, Ui.Row(Ui.Column(status, jumps).Width(Length.Px(340)).Shrink(0), table.Grow(1).Style(st => st.MinHeight(Length.Px(0)))).Grow(1).Style(st => st.MinHeight(Length.Px(0))))
            .Basis(Length.Px(140)).Shrink(1).Style(st => st.MinHeight(Length.Px(72))).Class("panel").Named("compare-panel").BindVisible(() => Visible);
    }

    /// <summary>A compact button, so a row of them fits a narrow panel.</summary>
    internal static Element Tight(Element button) => button.Padding(Length.Px(1), Length.Px(6));

    private static string EntityText(CompareRow r) =>
        (r.Entity.IsSome ? r.Entity.Value : r.Other.Value).ToString(CultureInfo.InvariantCulture);

    private string Title()
    {
        ComparePair? p = _shell.EffectivePair;
        if (p is null)
        {
            return "Compare";
        }

        string how = p.Kind switch
        {
            CompareKind.Fork => p.First.IsFork || p.Second.IsFork ? "fork vs original" : "same module",
            CompareKind.Versions => "version A vs B",
            _ => "partial vs whole",
        };
        return $"Compare  ·  source {p.First.Index} vs {p.Second.Index}  ·  {how}" + (p.Offset == 0 ? string.Empty : $"  ·  steps offset {p.Offset}");
    }

    private string Status(bool summary)
    {
        ComparePair? p = _shell.EffectivePair;
        if (p is null)
        {
            return string.Empty;
        }

        p.Range(out long from, out long to);
        Agreement ag = p.Agreement;
        long step = _shell.Step;
        if (step < from || step > to)
        {
            return summary ? string.Empty : $"cursor {step} is outside the steps both share ({from} to {to})";
        }

        CompareRows rows = Current(p);
        string here = p.Kind == CompareKind.Shared
            ? (rows.Total == 0 ? "shared entities equal" : $"{rows.Total} shared differences")
            : (ag.IsDivergent(step) ? "hashes differ" : "hashes equal");
        string first = ag.First is long f ? $"first divergence {f}" : "no divergence";
        string through = ag.ScannedThrough < ag.To ? $" (checked through {ag.ScannedThrough})" : string.Empty;
        return summary
            ? $"{first}; {ag.Disagreeing} of {Math.Max(ag.To - ag.From + 1, 0)} steps differ{through}"
            : $"step {step}: {here}";
    }

    private CompareRows Current(ComparePair p)
    {
        p.Range(out long from, out long to);
        long step = _shell.Step;
        return step < from || step > to ? new CompareRows(NoRows, 0, 0, 0, 0) : _shell.CompareAt(p, step, ShellModel.PanelCompareRows);
    }

    private IReadOnlyList<CompareRow> Rows() => _shell.EffectivePair is { } p ? Current(p).Rows : NoRows;

    private void Jump(string where)
    {
        if (_shell.EffectivePair is not { } p)
        {
            return;
        }

        Agreement ag = p.Agreement;
        long step = _shell.Step;
        long? target = where switch
        {
            "first" => ag.First,
            "last" => ag.Last,
            "next" => ag.Next(step),
            _ => ag.Previous(step),
        };
        if (target is long t)
        {
            _shell.Seek(t);
        }
    }
}
