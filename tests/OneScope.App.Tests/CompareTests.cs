using System.Text.Json;
using OneGame.Control;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>Library A/B in separate load contexts, fork against original and partial against whole, through the operation table.</summary>
[Collection("shell")]
public sealed class CompareTests
{
    private static string Modules => Path.Combine(AppContext.BaseDirectory, "modules");

    private static string TestModuleA => Path.Combine(Modules, "test-rule-a", "OneScope.TestModule.dll");

    private static string TestModuleB => Path.Combine(Modules, "test-rule-b", "OneScope.TestModule.dll");

    private static string PhysicsA => Path.Combine(Modules, "physics-a", "RecursiveAI.OnePhysics.Scope.dll");

    private static string PhysicsB => Path.Combine(Modules, "physics-b", "RecursiveAI.OnePhysics.Scope.dll");

    private static string Q(string s) => JsonSerializer.Serialize(s);

    private static void OpenWith(Harness h, string file, string assembly) =>
        h.Call("open", $$"""{"path":{{Q(Harness.Data(file))}},"moduleAssembly":{{Q(assembly)}}}""");

    [Fact]
    public void TwoVersionsOfAModuleDivergeAtTheStepTheirRuleDiffers()
    {
        using Harness h = new Harness();
        OpenWith(h, "test-10000.ostm", TestModuleA);
        OpenWith(h, "test-10000.ostm", TestModuleB);

        // Opening one file through two copies of a module pairs them without being asked.
        JsonElement c = h.Call("compare", """{"step":100,"limit":3}""");
        Harness.Report($"compare A/B at 100: {c}");
        Assert.Equal("versions", c.GetProperty("kind").GetString());
        JsonElement ag = c.GetProperty("agreement");
        Assert.Equal(TestModule.TestWorld.RuleStep, ag.GetProperty("firstDivergent").GetInt64());
        Assert.True(ag.GetProperty("complete").GetBoolean());
        Assert.True(c.GetProperty("equal").GetBoolean());
        Assert.Equal(0, c.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("difference").ValueKind);
        Assert.Equal(TestModule.TestWorld.RuleStep, ag.GetProperty("nextDivergent").GetInt64());
        Assert.Equal(JsonValueKind.Null, ag.GetProperty("previousDivergent").ValueKind);

        // Step 299 still agrees; step 300 is where the hashes first differ, and diff names the field.
        Assert.True(h.Call("compare", """{"step":299}""").GetProperty("equal").GetBoolean());
        JsonElement at = h.Call("compare", $$"""{"step":{{TestModule.TestWorld.RuleStep}},"limit":3}""");
        Harness.Report($"compare A/B at the rule step: {at}");
        Assert.False(at.GetProperty("equal").GetBoolean());
        Assert.NotEqual(at.GetProperty("hash").GetString(), at.GetProperty("otherHash").GetString());
        Assert.True(ag.GetProperty("divergentSteps").GetInt64() > 0);
        JsonElement d = at.GetProperty("difference");
        Assert.True(d.GetProperty("entity").GetInt64() > 0);
        Assert.NotEqual(d.GetProperty("source").GetString(), d.GetProperty("other").GetString());
        Assert.Equal(3, at.GetProperty("rows").GetArrayLength());
        Assert.True(at.GetProperty("rowCount").GetInt32() >= 3);

        JsonElement diff = h.Call("diff", $$"""{"source":0,"other":1,"step":{{TestModule.TestWorld.RuleStep}}}""");
        Assert.False(diff.GetProperty("equal").GetBoolean());
        Assert.Equal(d.GetProperty("entity").GetInt64(), diff.GetProperty("difference").GetProperty("entity").GetInt64());
        Assert.Equal(d.GetProperty("field").GetString(), diff.GetProperty("difference").GetProperty("field").GetString());
        Assert.True(h.Call("diff", """{"source":0,"other":1,"step":299}""").GetProperty("equal").GetBoolean());

        // Next and previous divergence land on the right steps and move the shared cursor.
        JsonElement first = h.Call("compare", """{"goto":"first"}""");
        Assert.Equal(TestModule.TestWorld.RuleStep, first.GetProperty("step").GetInt64());
        Assert.True(first.GetProperty("moved").GetBoolean());
        Assert.Equal(TestModule.TestWorld.RuleStep, h.Call("sources").GetProperty("step").GetInt64());
        Assert.Equal(JsonValueKind.Null, h.Call("compare", """{"goto":"previous"}""").GetProperty("agreement").GetProperty("previousDivergent").ValueKind);
        long last = ag.GetProperty("lastDivergent").GetInt64();
        Assert.Equal(last, h.Call("compare", """{"goto":"last"}""").GetProperty("step").GetInt64());
        Assert.Equal(last, h.Call("sources").GetProperty("step").GetInt64());
        long beforeLast = h.Call("compare", """{"goto":"previous"}""").GetProperty("step").GetInt64();
        Assert.True(beforeLast < last);
        Assert.Equal(last, h.Call("compare", """{"goto":"next"}""").GetProperty("step").GetInt64());
        // No later divergent step: the cursor stays.
        JsonElement none = h.Call("compare", """{"goto":"next"}""");
        Assert.False(none.GetProperty("moved").GetBoolean());
        Assert.Equal(last, none.GetProperty("step").GetInt64());

        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("compare", """{"goto":"sideways"}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("compare", """{"source":0,"other":1,"kind":"fork"}""")).Code);
    }

    [Fact]
    public void BothContextsUnloadWhenTheShellCloses()
    {
        WeakReference[] weak = OpenAndClose();
        for (int i = 0; i < 20 && weak.Any(w => w.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.All(weak, w => Assert.False(w.IsAlive));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] OpenAndClose()
    {
        var h = new Harness();
        OpenWith(h, "test-10000.ostm", TestModuleA);
        OpenWith(h, "test-10000.ostm", TestModuleB);
        h.Call("compare", """{"goto":"first"}""");
        WeakReference[] weak = h.Ops.LoadedModules.Select(m => m.ContextReference).ToArray();
        Assert.Equal(2, weak.Length);
        h.Dispose();
        return weak;
    }

    [Fact]
    public void TwoCopiesOfThePhysicsModuleInSeparateContextsAgreeWithTheLibraryAtEveryStep()
    {
        foreach (string file in new[] { "physics-2d.oppr", "physics-3d.oppr" })
        {
            using Harness h = new Harness();
            OpenWith(h, file, PhysicsA);
            OpenWith(h, file, PhysicsB);
            JsonElement src = h.Call("sources");
            Assert.NotEqual(src.GetProperty("sources")[0].GetProperty("moduleAssembly").GetString(), src.GetProperty("sources")[1].GetProperty("moduleAssembly").GetString());

            // Each copy reproduces the library's own recorded hash at every step. Both are the same published build in their own load contexts
            // (the second version of a module a test could load must be on nuget.org, and only the final release of each is), so they must
            // agree everywhere; where two builds differ is proved with the test module's second rule (above). The pair still aligns the
            // starts, which would shift the second copy if it numbered the replay differently.
            string[] recorded = File.ReadAllLines(Harness.Data(Path.ChangeExtension(file, ".hashes")));
            long start = src.GetProperty("sources")[0].GetProperty("startStep").GetInt64();
            for (int i = 0; i < recorded.Length; i++)
            {
                JsonElement at = h.Call("compare", $$"""{"step":{{start + i}}}""");
                Assert.True(recorded[i] == at.GetProperty("hash").GetString() && recorded[i] == at.GetProperty("otherHash").GetString(),
                    $"library step {i}: first has {at.GetProperty("hash")}, second has {at.GetProperty("otherHash")}, the library recorded {recorded[i]}");
            }

            JsonElement c = h.Call("compare");
            Harness.Report($"compare physics A/B {file}: offset {c.GetProperty("offset")} steps {c.GetProperty("from")}..{c.GetProperty("to")} agreement {c.GetProperty("agreement")}");
            Assert.Equal("versions", c.GetProperty("kind").GetString());
            Assert.Equal(src.GetProperty("sources")[1].GetProperty("startStep").GetInt64() - start, c.GetProperty("offset").GetInt64());
            Assert.True(c.GetProperty("equal").GetBoolean());
            JsonElement ag = c.GetProperty("agreement");
            Assert.Equal(0, ag.GetProperty("divergentSteps").GetInt64());
            Assert.Equal(JsonValueKind.Null, ag.GetProperty("firstDivergent").ValueKind);
            Assert.True(ag.GetProperty("complete").GetBoolean());
            Assert.True(c.GetProperty("to").GetInt64() >= 60);
            Assert.True(c.GetProperty("rowCount").GetInt32() == 0, c.GetProperty("rows").ToString());
        }
    }

    [Fact]
    public void AForkAgainstItsOriginalNamesTheDivergenceAndTheChangedFields()
    {
        using Harness h = new Harness().Open();
        h.Call("seek", """{"step":100}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":100,"delta":[900,400,0]},"step":true}""");
        h.Call("step", """{"source":1,"delta":20}""");

        JsonElement c = h.Call("compare", """{"step":105}""");
        Harness.Report($"compare fork vs original at 105: {c}");
        Assert.Equal("fork", c.GetProperty("kind").GetString());
        Assert.Equal(1, c.GetProperty("source").GetInt32());
        Assert.Equal(0, c.GetProperty("other").GetInt32());
        Assert.Equal(100, c.GetProperty("divergence").GetInt64());
        Assert.Equal(101, c.GetProperty("agreement").GetProperty("firstDivergent").GetInt64());
        Assert.False(c.GetProperty("equal").GetBoolean());
        Assert.Equal(100, c.GetProperty("difference").GetProperty("entity").GetInt64());
        Assert.True(c.GetProperty("rowCount").GetInt32() >= 1);
        Assert.Contains(c.GetProperty("rows").EnumerateArray(), r => r.GetProperty("entity").GetInt64() == 100);

        Assert.True(h.Call("compare", """{"step":100}""").GetProperty("equal").GetBoolean());
        Assert.Equal(101, h.Call("compare", """{"goto":"first"}""").GetProperty("step").GetInt64());
        Assert.Equal(121, h.Call("compare", """{"goto":"last"}""").GetProperty("step").GetInt64());
        // The same pair is chosen again with no arguments.
        Assert.Equal(121, h.Call("compare").GetProperty("step").GetInt64());
        // Naming one side of the pair compares it with the other side.
        JsonElement named = h.Call("compare", """{"source":0}""");
        Assert.Equal(0, named.GetProperty("source").GetInt32());
        Assert.Equal(1, named.GetProperty("other").GetInt32());
    }

    [Fact]
    public void ASourceWithNoPartnerNeedsOne()
    {
        using Harness h = new Harness().Open();
        ControlException e = Assert.Throws<ControlException>(() => h.Call("compare"));
        Assert.Equal("bad_args", e.Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("compare", """{"source":0}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("compare", """{"source":0,"other":0}""")).Code);
    }

    [Fact]
    public void APartialWorldAgainstAWholeOneComparesOnlyWhatBothHold()
    {
        using Harness h = new Harness().Open("test-2d-600.ostm");
        h.Call("seek", """{"step":200}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"remove","args":{"target":105},"step":true}""");
        h.Call("step", """{"source":1,"delta":3}""");

        JsonElement whole = h.Call("compare", """{"source":1,"kind":"shared"}""");
        Harness.Report($"compare shared: {whole}");
        Assert.Equal("shared", whole.GetProperty("kind").GetString());
        Assert.True(whole.GetProperty("equal").GetBoolean());
        Assert.Equal(13, whole.GetProperty("shared").GetInt32());
        Assert.Equal(1, whole.GetProperty("onlyOther").GetInt32());
        Assert.Equal(0, whole.GetProperty("agreement").GetProperty("divergentSteps").GetInt64());

        // The ordinary comparison of the same pair sees the removed body.
        JsonElement plain = h.Call("compare", """{"source":1,"kind":"fork"}""");
        Assert.False(plain.GetProperty("equal").GetBoolean());

        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":102,"delta":[500,0,0]},"step":true}""");
        JsonElement planted = h.Call("compare", """{"source":1,"kind":"shared","step":205}""");
        Assert.False(planted.GetProperty("equal").GetBoolean());
        Assert.Equal(102, planted.GetProperty("difference").GetProperty("entity").GetInt64());
        Assert.Equal("position", planted.GetProperty("difference").GetProperty("field").GetString());
        Assert.Equal(205, planted.GetProperty("agreement").GetProperty("firstDivergent").GetInt64());
    }

    [Fact]
    public void ThePanelsJumpButtonsLandOnTheDivergentStepsAndARowSelectsTheEntityInBothViews()
    {
        using Harness h = new Harness(1280, 720);
        OpenWith(h, "test-2d-600.ostm", TestModuleA);
        OpenWith(h, "test-2d-600.ostm", TestModuleB);
        Assert.True(h.Call("layout").GetProperty("compare").GetBoolean());
        h.Call("seek", """{"step":10}""");
        h.Call("ui_click", """{"id":"compare/next"}""");
        Assert.Equal(TestModule.TestWorld.RuleStep, h.Shell.Step);
        h.Call("ui_click", """{"id":"compare/last"}""");
        Assert.Equal(600, h.Shell.Step);
        h.Call("ui_click", """{"id":"compare/previous"}""");
        Assert.Equal(599, h.Shell.Step);
        h.Call("ui_click", """{"id":"compare/first"}""");
        Assert.Equal(TestModule.TestWorld.RuleStep, h.Shell.Step);

        // Choosing a row selects its entity, and both sources' views outline it.
        CompareRows rows = h.Shell.CompareAt(h.Shell.EffectivePair!, h.Shell.Step, 10);
        Assert.NotEmpty(rows.Rows);
        h.Shell.SelectRow(rows.Rows[0]);
        Assert.Equal(rows.Rows[0].Entity, h.Shell.Selected);
        Assert.All(h.Shell.Views, v => Assert.Equal(rows.Rows[0].Entity, h.Shell.HighlightFor(v)));
    }
}
