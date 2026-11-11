using System.Globalization;
using System.Text.Json;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>Fork, poke, drag, diff and save on the OnePath module's sources: the original never changes, the saved fork reproduces itself.</summary>
[Collection("shell")]
public sealed class PathForkTests
{
    private static string Q(string s) => JsonSerializer.Serialize(s);

    private static string P(string action, float x, float y) => JsonSerializer.Serialize(new { action, x, y });

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    private static ulong Entity(Harness h, string kind, string name, int source = 0)
    {
        foreach (JsonElement e in h.Call("entities", $$"""{"source":{{source}},"kind":"{{kind}}"}""").GetProperty("entities").EnumerateArray())
        {
            if (e.GetProperty("name").GetString() == name)
            {
                return e.GetProperty("id").GetUInt64();
            }
        }

        throw new InvalidOperationException($"no {kind} named '{name}'");
    }

    private static JsonElement Row(Harness h, ulong entity, string name, int source = 0) =>
        h.Call("inspect", $$"""{"source":{{source}},"entity":{{entity}}}""").GetProperty("properties").EnumerateArray().Single(r => r.GetProperty("name").GetString() == name).GetProperty("value");

    [Theory]
    [InlineData("path-2d", "[3,20,0]", "[3,3,0]", 2)]
    [InlineData("path-3d", "[3,0,20]", "[10,0,3]", 3)]
    public void EveryPokeOnACrowdForkLeavesTheOriginalAloneAndTheSavedForkReproducesItself(string name, string target, string spot, int dimension)
    {
        string file = Harness.Data(name + ".oprp");
        string shaBefore = Harness.Sha(File.ReadAllBytes(file));
        string[] recorded = File.ReadAllLines(Harness.Data(name + ".hashes"));
        using Harness h = new Harness(960, 540, bundled: true);
        h.Call("open", $$"""{"path":{{Q(file)}},"keyframeInterval":16}""");
        h.Call("seek", """{"step":60}""");
        ulong a1 = Entity(h, "agent", "agent 1");
        ulong a4 = Entity(h, "agent", "agent 4");
        ulong a5 = Entity(h, "agent", "agent 5");
        ulong a6 = Entity(h, "agent", "agent 6");
        string destBefore = Row(h, a1, "destination").GetRawText();

        JsonElement fork = h.Call("fork");
        int branch = fork.GetProperty("branch").GetInt32();
        Assert.Equal(60, fork.GetProperty("fork").GetProperty("forkStep").GetInt64());
        JsonElement pokes = fork.GetProperty("pokes");
        Assert.Equal(["set_target", "add_agent", "remove_agent", "stop_agent", "teleport_agent"], pokes.EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        // Pokes, one by one, each applied by a step of the fork.
        h.Call("poke", $$"""{"source":{{branch}},"poke":"set_target","args":{"agent":{{a1}},"target":{{target}}},"step":true}""");
        h.Call("poke", $$"""{"source":{{branch}},"poke":"stop_agent","args":{"agent":{{a4}}},"step":true}""");
        h.Call("poke", $$"""{"source":{{branch}},"poke":"teleport_agent","args":{"agent":{{a5}},"position":{{spot}}},"step":true}""");
        h.Call("poke", $$"""{"source":{{branch}},"poke":"remove_agent","args":{"agent":{{a6}}},"step":true}""");
        h.Call("poke", $$"""{"source":{{branch}},"poke":"add_agent","args":{"position":{{spot}}},"step":true}""");
        h.Call("step", $$"""{"source":{{branch}},"delta":6}""");
        string destAfter = Row(h, a1, "destination", branch).GetRawText();
        Assert.NotEqual(destBefore, destAfter);
        Assert.Equal(destBefore, Row(h, a1, "destination", 0).GetRawText());
        Harness.Report($"{name}: set_target moved agent 1's destination {destBefore} -> {destAfter} on the fork; the original still says {destBefore}");

        // The original's bytes and hashes are what they were, at steps before and after the fork point.
        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));
        foreach (long step in new long[] { 0, 59, 60, 61, 66, 119, 120 })
        {
            Assert.Equal(recorded[step], PathTests.ShellHash(h, step));
        }

        // One step after the first poke the poked agent differs, and diff names it and the field.
        JsonElement diff = h.Call("diff", $$"""{"source":{{branch}},"step":61}""");
        Assert.False(diff.GetProperty("equal").GetBoolean());
        Assert.Equal(60, diff.GetProperty("divergence").GetInt64());
        JsonElement difference = diff.GetProperty("difference");
        Assert.Equal(a1, difference.GetProperty("entity").GetUInt64());
        Harness.Report($"{name} diff at 61: entity {difference.GetProperty("entity")} field {difference.GetProperty("field")} {difference.GetProperty("value")} vs {difference.GetProperty("otherValue")}");
        Assert.True(h.Call("diff", $$"""{"source":{{branch}},"step":60}""").GetProperty("equal").GetBoolean());

        // A pointer drag on the fork: the agent under the pointer is grabbed, every move is one step with one set_target.
        int view = h.Shell.Views.ToList().FindIndex(v => v.Source == branch);
        Assert.True(view >= 0);
        h.Call("capture_frame", """{"path":"layout.png"}""");
        (float x, float y, ulong grabbed) = Locate(h, view, branch);
        long before = h.Call("sources").GetProperty("sources")[branch].GetProperty("steps").GetInt64();
        long poked = h.Call("sources").GetProperty("sources")[branch].GetProperty("fork").GetProperty("pokedSteps").GetInt64();
        const int Moves = 5;
        h.Call("input_pointer", P("down", x, y));
        for (int i = 1; i <= Moves; i++)
        {
            h.Call("input_pointer", P("move", x + (4 * i), y - (3 * i)));
        }

        h.Call("input_pointer", P("up", x + (4 * Moves), y - (3 * Moves)));
        JsonElement info = h.Call("sources").GetProperty("sources")[branch];
        Assert.Equal(before + Moves, info.GetProperty("steps").GetInt64());
        Assert.Equal(poked + Moves, info.GetProperty("fork").GetProperty("pokedSteps").GetInt64());
        Assert.Equal(grabbed, h.Shell.Selected.Value);
        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));

        // The saved fork reopens to the fork's hashes at every step.
        long steps = info.GetProperty("steps").GetInt64();
        var forkHashes = new List<string>();
        for (long s = 0; s <= steps; s++)
        {
            forkHashes.Add(PathTests.ShellHash(h, s, branch));
        }

        string path = h.Call("replay_save", $$"""{"source":{{branch}},"path":"fork.oprp"}""").GetProperty("path").GetString()!;
        int again = h.Call("open", $$"""{"path":{{Q(path)}},"keyframeInterval":16}""").GetProperty("source").GetInt32();
        Assert.Equal(dimension, h.Call("sources").GetProperty("sources")[again].GetProperty("dimensions").GetInt32());
        for (long s = 0; s <= steps; s++)
        {
            Assert.Equal(forkHashes[(int)s], PathTests.ShellHash(h, s, again));
        }

        Assert.NotEqual(recorded[66], forkHashes[66]);
        Harness.Report($"{name}: fork of {steps} steps (5 pokes and a {Moves}-move drag of entity {grabbed}), saved and reopened; {forkHashes.Count} hashes equal; original sha256 {shaBefore[..16]} before and after");
    }

    /// <summary>A pixel in the fork's view where an agent is under the pointer.</summary>
    private static (float X, float Y, ulong Entity) Locate(Harness h, int view, int source)
    {
        foreach (JsonElement e in h.Call("entities", $$"""{"source":{{source}},"kind":"agent"}""").GetProperty("entities").EnumerateArray())
        {
            ulong id = e.GetProperty("id").GetUInt64();
            JsonElement p = h.Call("project", $$"""{"entity":{{id}},"view":{{view}}}""");
            if (!p.GetProperty("onScreen").GetBoolean())
            {
                continue;
            }

            float x = p.GetProperty("x").GetSingle();
            float y = p.GetProperty("y").GetSingle();
            JsonElement hit = h.Call("pick", $$"""{"x":{{F(x)}},"y":{{F(y)}},"view":{{view}}}""");
            if (hit.GetProperty("entity").ValueKind == JsonValueKind.Number && hit.GetProperty("entity").GetUInt64() == id)
            {
                return (x, y, id);
            }
        }

        throw new InvalidOperationException("no agent could be picked where it was projected");
    }

    [Fact]
    public void AGridRunForkTakesAnEditAndDiffNamesTheCell()
    {
        string file = Harness.Data("path-search.opgr");
        string shaBefore = Harness.Sha(File.ReadAllBytes(file));
        using Harness h = new Harness(960, 540, bundled: true);
        h.Call("open", $$"""{"path":{{Q(file)}},"keyframeInterval":16}""");
        string[] original = Enumerable.Range(0, 81).Select(s => PathTests.ShellHash(h, s)).ToArray();
        h.Call("seek", """{"step":20}""");
        JsonElement fork = h.Call("fork");
        int branch = fork.GetProperty("branch").GetInt32();
        Assert.Equal(["move_goal", "move_start", "set_wall", "set_area"], fork.GetProperty("pokes").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        h.Call("poke", $$"""{"source":{{branch}},"poke":"set_wall","args":{"cell":[10,16,0],"wall":true},"step":true}""");
        ulong goal = Entity(h, "marker", "goal", branch);
        h.Call("poke", $$"""{"source":{{branch}},"poke":"move_goal","args":{"marker":{{goal}},"target":[44,10,0]},"step":true}""");
        h.Call("step", $$"""{"source":{{branch}},"delta":6}""");
        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));
        for (int s = 0; s <= 80; s++)
        {
            Assert.Equal(original[s], PathTests.ShellHash(h, s));
        }

        JsonElement diff = h.Call("diff", $$"""{"source":{{branch}},"step":21}""");
        Assert.False(diff.GetProperty("equal").GetBoolean());
        Harness.Report($"grid fork diff at 21: {diff.GetProperty("difference")}");
    }

    [Fact]
    public void DraggingTheGoalOfAGridForkIsOneMoveGoalPokePerMoveAndReplaysExactly()
    {
        string file = Harness.Data("path-search.opgr");
        using Harness h = new Harness(960, 540, bundled: true);
        h.Call("open", $$"""{"path":{{Q(file)}},"keyframeInterval":16}""");
        h.Call("seek", """{"step":30}""");
        int branch = h.Call("fork").GetProperty("branch").GetInt32();
        h.Call("capture_frame", """{"path":"layout.png"}""");
        int view = h.Shell.Views.ToList().FindIndex(v => v.Source == branch);
        ulong goal = Entity(h, "marker", "goal", branch);
        JsonElement p = h.Call("project", $$"""{"entity":{{goal}},"view":{{view}}}""");
        float x = p.GetProperty("x").GetSingle();
        float y = p.GetProperty("y").GetSingle();
        JsonElement hit = h.Call("pick", $$"""{"x":{{F(x)}},"y":{{F(y)}},"view":{{view}}}""");
        Assert.Equal(goal, hit.GetProperty("entity").GetUInt64());
        h.Call("input_pointer", P("down", x, y));
        for (int i = 1; i <= 4; i++)
        {
            h.Call("input_pointer", P("move", x - (14 * i), y - (11 * i)));
        }

        h.Call("input_pointer", P("up", x - 56, y - 44));
        JsonElement info = h.Call("sources").GetProperty("sources")[branch];
        Assert.Equal(34, info.GetProperty("steps").GetInt64());
        Assert.Equal(4, info.GetProperty("fork").GetProperty("pokedSteps").GetInt64());
        var hashes = Enumerable.Range(0, 35).Select(s => PathTests.ShellHash(h, s, branch)).ToArray();
        string path = h.Call("replay_save", $$"""{"source":{{branch}},"path":"drag.opgr"}""").GetProperty("path").GetString()!;
        int again = h.Call("open", $$"""{"path":{{Q(path)}},"keyframeInterval":16}""").GetProperty("source").GetInt32();
        for (int s = 0; s <= 34; s++)
        {
            Assert.Equal(hashes[s], PathTests.ShellHash(h, s, again));
        }

        Harness.Report("grid fork: dragged the goal marker for 4 moves (4 move_goal pokes); saved and reopened, 35 hashes equal");
    }
}
