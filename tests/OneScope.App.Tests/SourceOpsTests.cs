using System.Text.Json;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>The offset and close operations, and the command line's list of operations.</summary>
[Collection("shell")]
public sealed class SourceOpsTests
{
    private static string HashOf(Harness h, int source) => h.Call("sources").GetProperty("sources")[source].GetProperty("hash").GetString()!;

    private static long StepOf(Harness h, int source) => h.Call("sources").GetProperty("sources")[source].GetProperty("step").GetInt64();

    [Fact]
    public void EveryOperationIsKnownToTheCommandLine()
    {
        using var h = new Harness(bundled: true);
        string[] missing = h.Ops.Operations.Select(o => o.Name).Where(n => Array.IndexOf(Program.KnownOps, n) < 0).ToArray();
        Assert.True(missing.Length == 0, "Not in Program.KnownOps: " + string.Join(", ", missing));
        string[] stale = Program.KnownOps.Where(n => !h.Ops.Operations.Any(o => o.Name == n) && n != "follow").ToArray();
        Assert.True(stale.Length == 0, "In Program.KnownOps but not an operation: " + string.Join(", ", stale));
    }

    [Fact]
    public void AnOffsetShowsOneMomentInTwoSourcesThatNumberStepsDifferently()
    {
        using var h = new Harness().Open().Open();
        h.Call("seek", """{"step":105}""");
        string at105 = HashOf(h, 0);
        h.Call("seek", """{"step":100}""");
        string at100 = HashOf(h, 0);
        Assert.NotEqual(at100, at105);

        JsonElement r = h.Call("offset", """{"source":1,"offset":5}""");
        Assert.Equal(5, r.GetProperty("offset").GetInt64());
        Assert.Equal(100, r.GetProperty("step").GetInt64());
        Assert.Equal(105, r.GetProperty("sourceStep").GetInt64());
        Assert.Equal(at100, HashOf(h, 0));
        Assert.Equal(at105, HashOf(h, 1));
        Assert.Equal(105, StepOf(h, 1));

        // The shared axis spans every source's recording in shared numbers: source 1 begins at shared step -5.
        JsonElement s = h.Call("sources");
        Assert.Equal(-5, s.GetProperty("start").GetInt64());
        Assert.Equal(10000, s.GetProperty("steps").GetInt64());
        h.Call("seek", """{"step":10000}""");
        Assert.Equal(10000, StepOf(h, 0));
        Assert.Equal(10000, StepOf(h, 1)); // held at its own last step

        // Reporting without a value changes nothing; zero puts it back.
        Assert.Equal(5, h.Call("offset", """{"source":1}""").GetProperty("offset").GetInt64());
        h.Call("offset", """{"source":1,"offset":0}""");
        h.Call("seek", """{"step":100}""");
        Assert.Equal(at100, HashOf(h, 1));
    }

    [Fact]
    public void ClosingASourceTakesItsForksAndRenumbersTheRest()
    {
        using var h = new Harness().Open().Open().Open();
        h.Call("seek", """{"step":50}""");
        h.Call("fork", """{"source":1}""");
        Assert.Equal(4, h.Call("sources").GetProperty("sources").GetArrayLength());
        int viewsBefore = h.Shell.Views.Count;
        string hash2 = HashOf(h, 2);

        JsonElement r = h.Call("close", """{"source":1}""");
        Assert.Equal([1, 3], r.GetProperty("closed").EnumerateArray().Select(c => c.GetProperty("source").GetInt32()).ToArray());
        Assert.True(r.GetProperty("closed")[1].GetProperty("fork").GetBoolean());
        JsonElement[] moved = r.GetProperty("renumbered").EnumerateArray().ToArray();
        Assert.Single(moved);
        Assert.Equal(2, moved[0].GetProperty("from").GetInt32());
        Assert.Equal(1, moved[0].GetProperty("to").GetInt32());
        Assert.Equal(2, r.GetProperty("sources").GetInt32());
        Assert.True(r.GetProperty("views").GetInt32() < viewsBefore);
        Assert.All(h.Shell.Views, v => Assert.InRange(v.Source, 0, 1));
        Assert.Equal(hash2, HashOf(h, 1));
        Assert.Equal(1, h.Call("entities", """{"source":1,"limit":1}""").GetProperty("source").GetInt32());

        h.Call("close", """{"source":1}""");
        JsonElement last = h.Call("close", """{"source":0}""");
        Assert.Equal(-1, last.GetProperty("active").GetInt32());
        Assert.Equal("no_source", Assert.Throws<OneGame.Control.ControlException>(() => h.Call("seek", """{"step":1}""")).Code);
    }
}
