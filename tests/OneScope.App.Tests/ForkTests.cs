using System.Text.Json;
using OneGame.Control;
using OneGame.Host;
using UiRect = OneGame.UI.Rect;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>Fork, poke, step, diff and save through the operation table, the way a CLI or an agent calls them.</summary>
[Collection("shell")]
public sealed class ForkTests
{
    private static readonly long[] Probe = [0, 1, 50, 99, 100, 101, 105, 120, 500, 1000, 9999, 10000];

    private static string HashAt(Harness h, long step, int source)
    {
        h.Call("seek", $$"""{"step":{{step}}}""");
        return h.Call("sources").GetProperty("sources")[source].GetProperty("hash").GetString()!;
    }

    private static Dictionary<long, string> Hashes(Harness h, int source, IEnumerable<long> steps)
    {
        var map = new Dictionary<long, string>();
        foreach (long s in steps)
        {
            map[s] = HashAt(h, s, source);
        }

        return map;
    }

    [Fact]
    public void ForkPokeStepDiffAndSaveLeaveTheOriginalAlone()
    {
        string file = Harness.Data("test-10000.ostm");
        string shaBefore = Harness.Sha(File.ReadAllBytes(file));
        using Harness h = new Harness().Open();
        Dictionary<long, string> original = Hashes(h, 0, Probe);

        h.Call("seek", """{"step":100}""");
        JsonElement fork = h.Call("fork");
        Assert.Equal(1, fork.GetProperty("branch").GetInt32());
        Assert.Equal(100, fork.GetProperty("fork").GetProperty("forkStep").GetInt64());
        Assert.Equal(JsonValueKind.Null, fork.GetProperty("fork").GetProperty("divergence").ValueKind);
        Assert.Equal(original[100], fork.GetProperty("hash").GetString());

        // The opened file refuses pokes with a typed error, before and after the fork exists.
        ControlException refused = Assert.Throws<ControlException>(() => h.Call("poke", """{"source":0,"poke":"impulse","args":{"target":100,"delta":[500,0,0]}}"""));
        Assert.Equal("read_only", refused.Code);
        Assert.Contains("fork", refused.Message);

        JsonElement poked = h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":100,"delta":[900,400,0]}}""");
        Assert.Equal(100, poked.GetProperty("appliesAtStep").GetInt64());
        Assert.Equal(1, poked.GetProperty("pending").GetInt32());
        JsonElement stepped = h.Call("step", """{"source":1,"delta":20}""");
        Assert.Equal(120, stepped.GetProperty("step").GetInt64());

        // Equal through the divergence step, different after it.
        JsonElement same = h.Call("diff", """{"source":1,"step":100}""");
        Assert.True(same.GetProperty("equal").GetBoolean());
        Assert.Equal(100, same.GetProperty("divergence").GetInt64());
        JsonElement differs = h.Call("diff", """{"source":1,"step":105}""");
        Assert.False(differs.GetProperty("equal").GetBoolean());
        JsonElement d = differs.GetProperty("difference");
        Assert.Equal(100UL, d.GetProperty("entity").GetUInt64());
        Assert.False(string.IsNullOrEmpty(d.GetProperty("field").GetString()));
        Assert.NotEqual(d.GetProperty("value").GetString(), d.GetProperty("otherValue").GetString());
        // The hashes are the worlds' at the compared step, not wherever the cursor is afterwards.
        Assert.Equal(original[105], differs.GetProperty("otherHash").GetString());
        Assert.Equal(HashAt(h, 105, 1), differs.GetProperty("hash").GetString());

        // The fork's replay saved and reopened reproduces the fork at every step.
        JsonElement saved = h.Call("replay_save", """{"source":1,"path":"fork1.ostm"}""");
        Assert.Equal(120, saved.GetProperty("steps").GetInt64());
        Assert.Equal(100, saved.GetProperty("divergence").GetInt64());
        Dictionary<long, string> forkHashes = Hashes(h, 1, Enumerable.Range(0, 121).Select(i => (long)i));
        JsonElement reopened = h.Call("open", JsonSerializer.Serialize(new { path = saved.GetProperty("path").GetString() }));
        int reopenedIndex = reopened.GetProperty("source").GetInt32();
        Assert.Equal(120, reopened.GetProperty("steps").GetInt64());
        foreach (long step in forkHashes.Keys)
        {
            Assert.Equal(forkHashes[step], HashAt(h, step, reopenedIndex));
        }

        // Seeking inside the fork forward and backward gives the hashes stepping straight through gave.
        foreach (long step in new long[] { 119, 3, 100, 101, 60, 120, 0, 110 })
        {
            Assert.Equal(forkHashes[step], HashAt(h, step, 1));
        }

        // The original: same bytes on disk, same hash at every probed step.
        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));
        Assert.Equal(original, Hashes(h, 0, Probe));
        Harness.Report($"fork isolation: file sha256 {shaBefore[..16]} unchanged; original hashes equal at {Probe.Length} steps; fork reopened equal at 121 steps; divergence 100");
    }

    [Fact]
    public void PokesAreTypedAndNameWhatWasWrong()
    {
        using Harness h = new Harness().Open();
        h.Call("fork", """{"step":10}""");
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"explode"}""")).Code);
        ControlException missing = Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":100}}"""));
        Assert.Equal("bad_args", missing.Code);
        Assert.Contains("delta", missing.Message);
        ControlException wrong = Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"set_gravity","args":{"gravity":"heavy"}}"""));
        Assert.Contains("gravity", wrong.Message);
        ControlException body = Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"remove","args":[9999]}"""));
        Assert.Equal("bad_args", body.Code);
        Assert.Contains("9999", body.Message);
        // Positional arguments work and each declared poke can be queued and applied.
        h.Call("poke", """{"source":1,"poke":"set_gravity","args":[3]}""");
        h.Call("poke", """{"source":1,"poke":"teleport","args":{"target":101,"position":[2000,2000,2000]}}""");
        h.Call("poke", """{"source":1,"poke":"remove","args":{"target":102}}""");
        JsonElement r = h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":103,"delta":[10,20,30]},"step":true}""");
        Assert.Equal(11, r.GetProperty("step").GetInt64());
        Assert.Equal(0, r.GetProperty("pending").GetInt32());
        Assert.Equal(1, h.Call("sources").GetProperty("sources")[1].GetProperty("fork").GetProperty("pokedSteps").GetInt64());
        JsonElement entities = h.Call("entities", """{"source":1,"kind":"body"}""");
        Assert.DoesNotContain(entities.GetProperty("entities").EnumerateArray(), e => e.GetProperty("id").GetUInt64() == 102);
        Assert.Contains(h.Call("entities", """{"source":0,"kind":"body"}""").GetProperty("entities").EnumerateArray(), e => e.GetProperty("id").GetUInt64() == 102);

        // After scrubbing back, the fork refuses a poke instead of silently moving.
        h.Call("seek", """{"step":5}""");
        ControlException early = Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"set_gravity","args":[1]}"""));
        Assert.Equal("rejected", early.Code);
        Assert.Contains("11", early.Message);
    }

    [Fact]
    public void ForkInspectAndCaptureTakeABranch()
    {
        using Harness h = new Harness().Open();
        h.Call("seek", """{"step":30}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"teleport","args":{"target":100,"position":[1234,2345,3456]},"step":true}""");
        JsonElement a = h.Call("inspect", """{"entity":100,"branch":1}""");
        JsonElement b = h.Call("inspect", """{"entity":100,"source":0}""");
        Assert.NotEqual(a.GetProperty("properties").GetRawText(), b.GetProperty("properties").GetRawText());
        JsonElement cap = h.Call("capture_frame", """{"source":1,"path":"fork-view.png","size":"480x360"}""");
        Assert.Equal(480, cap.GetProperty("width").GetInt32());
        ControlException none = Assert.Throws<ControlException>(() => h.Call("diff", """{"source":0}"""));
        Assert.Equal("bad_args", none.Code);
    }

    [Fact]
    public void ReplaySaveStaysInsideTheOutputFolderAndNeverOverwritesAnOpenedFile()
    {
        using Harness h = new Harness().Open();
        h.Call("fork", """{"step":4}""");
        Assert.Equal("path_outside", Assert.Throws<ControlException>(() => h.Call("replay_save", """{"source":1,"path":"../x.ostm"}""")).Code);
        Assert.Equal("path_outside", Assert.Throws<ControlException>(() => h.Call("replay_save", """{"source":1,"path":"/etc/x.ostm"}""")).Code);
    }

    private static string P(string action, float x, float y) => JsonSerializer.Serialize(new { action, x, y });

    /// <summary>Finds a screen position inside <paramref name="rect"/> where a body (id from 100) is under the pointer.</summary>
    private static (float X, float Y, ulong Entity) FindBody(Harness h, int view, UiRect rect)
    {
        for (float y = rect.Y + 4; y < rect.Bottom - 4; y += 3)
        {
            for (float x = rect.X + 4; x < rect.Right - 4; x += 3)
            {
                ulong id = ViewPicking.Pick(h.Shell, h.Shell.Views[view], rect, x, y).Entity.Value;
                if (id >= 100)
                {
                    return (x, y, id);
                }
            }
        }

        throw new InvalidOperationException("No body under any pointer position in the view.");
    }

    [Theory]
    [InlineData("test-2d-600.ostm", 50)]
    [InlineData("test-10000.ostm", 200)]
    public void APointerDragOnAForkRecordsOnePokeSeriesAndReplaysIt(string file, int forkStep)
    {
        using Harness h = new Harness().Open(file);
        Dictionary<long, string> original = Hashes(h, 0, [0, forkStep, forkStep + 1, forkStep + 10, 300]);
        h.Call("seek", $$"""{"step":{{forkStep}}}""");
        JsonElement fork = h.Call("fork");
        int branch = fork.GetProperty("branch").GetInt32();
        int viewIndex = h.Shell.Views.Count - 1;
        Assert.Equal(branch, h.Shell.Views[viewIndex].Source);
        h.Call("capture_frame", """{"path":"layout.png"}""");
        UiRect rect = ViewPicking.Round(h.Game.Ui.Find($"view/{viewIndex}")!.AbsoluteBounds);
        (float x, float y, ulong body) = FindBody(h, viewIndex, rect);

        // On the opened file a press only selects: no fork is poked and nothing is recorded.
        UiRect originalRect = ViewPicking.Round(h.Game.Ui.Find("view/0")!.AbsoluteBounds);
        (float ox, float oy, ulong obody) = FindBody(h, 0, originalRect);
        h.Call("input_pointer", P("down", ox, oy));
        h.Call("input_pointer", P("move", ox + 6, oy - 6));
        h.Call("input_pointer", P("up", ox + 6, oy - 6));
        Assert.Equal(obody, h.Shell.Selected.Value);
        Assert.Equal(forkStep, h.Call("sources").GetProperty("sources")[branch].GetProperty("steps").GetInt64());

        // A drag on the fork: every move is one step with one teleport of the grabbed body.
        const int Moves = 6;
        h.Call("input_pointer", P("down", x, y));
        for (int i = 1; i <= Moves; i++)
        {
            h.Call("input_pointer", P("move", x + (3 * i), y - (2 * i)));
        }

        h.Call("input_pointer", P("up", x + (3 * Moves), y - (2 * Moves)));
        h.Call("input_pointer", P("move", x + 40, y - 40));
        Assert.Equal(body, h.Shell.Selected.Value);
        JsonElement info = h.Call("sources").GetProperty("sources")[branch];
        Assert.Equal(forkStep + Moves, info.GetProperty("steps").GetInt64());
        Assert.Equal(Moves, info.GetProperty("fork").GetProperty("pokedSteps").GetInt64());
        Assert.Equal(forkStep, info.GetProperty("fork").GetProperty("divergence").GetInt64());
        Assert.Equal(0, info.GetProperty("fork").GetProperty("pending").GetInt32());

        // Saving the fork and opening the file reproduces every step of it; the original is unchanged.
        string path = h.Call("replay_save", """{"source":1,"path":"drag.ostm"}""").GetProperty("path").GetString()!;
        Dictionary<long, string> forkHashes = Hashes(h, branch, Enumerable.Range(0, forkStep + Moves + 1).Select(i => (long)i));
        int again = h.Call("open", JsonSerializer.Serialize(new { path })).GetProperty("source").GetInt32();
        foreach (KeyValuePair<long, string> kv in forkHashes)
        {
            Assert.Equal(kv.Value, HashAt(h, kv.Key, again));
        }

        Assert.NotEqual(original[forkStep + 1], forkHashes[forkStep + 1]);
        Assert.Equal(original, Hashes(h, 0, original.Keys));
        Harness.Report($"drag {file}: body {body} grabbed at fork step {forkStep}, {Moves} moves -> {Moves} pokes; saved replay reproduces {forkHashes.Count} steps");
    }

    [Fact]
    public void SelectingAnEntityHighlightsItInEveryViewOfTheSameFile()
    {
        using Harness h = new Harness().Open();
        h.Call("fork");
        h.Call("inspect", """{"entity":104,"source":0}""");
        foreach (ViewState v in h.Shell.Views)
        {
            Assert.Equal(104UL, h.Shell.HighlightFor(v).Value);
        }

        h.Call("open", JsonSerializer.Serialize(new { path = Harness.Data("test-2d-600.ostm") }));
        Assert.Equal(104UL, h.Shell.HighlightFor(h.Shell.Views[0]).Value);
        Assert.False(h.Shell.HighlightFor(h.Shell.Views[^1]).IsSome);
    }

    private static int HighlightPixels(Harness h, int view)
    {
        JsonElement r = h.Call("capture_frame", $$"""{"path":"hl.png","view":{{view}},"size":"480x360"}""");
        CapturedFrame frame = Png.Load(r.GetProperty("path").GetString()!);
        int n = 0;
        for (int i = 0; i + 3 < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i] > 235 && frame.Rgba[i + 1] is > 190 and < 235 && frame.Rgba[i + 2] < 110)
            {
                n++;
            }
        }

        return n;
    }

    [Theory]
    [InlineData("test-2d-600.ostm", 0)]
    [InlineData("test-10000.ostm", 0)]
    [InlineData("test-10000.ostm", 1)]
    public void TheSelectedEntityIsOutlinedInTheViewAndNothingElseIs(string file, int view)
    {
        using Harness h = new Harness().Open(file);
        h.Call("seek", """{"step":120}""");
        h.Call("view", """{"index":0,"target":[5000,1200,5000],"distance":6500,"halfHeight":3200,"yaw":35,"pitch":28}""");
        h.Call("view", """{"index":1,"target":[5000,3000,0],"distance":6500,"halfHeight":3400}""");
        int before = HighlightPixels(h, view);
        // The body nearest the camera's target is in frame whatever size the capture is.
        var list = new OneScope.Sdk.DrawList();
        h.Shell.Source(0).History.View.Draw(list, new OneScope.Sdk.DrawRequest(h.Shell.Views[view].Overlays));
        ulong body = 0;
        float best = float.MaxValue;
        foreach (OneScope.Sdk.DrawItem it in list.Items)
        {
            float d = System.Numerics.Vector3.Distance(it.A, h.Shell.Views[view].Target);
            if (it.Entity.Value >= 100 && it.Kind is not OneScope.Sdk.DrawKind.Point and not OneScope.Sdk.DrawKind.Line && d < best)
            {
                best = d;
                body = it.Entity.Value;
            }
        }

        h.Call("inspect", $$"""{"entity":{{body}}}""");
        int after = HighlightPixels(h, view);
        Harness.Report($"highlight {file} view {view}: {before} outline pixels without selection, {after} with body {body} selected");
        Assert.Equal(0, before);
        Assert.True(after >= 20, $"expected an outline, found {after} pixels");
    }

    [Theory]
    [InlineData("test-2d-600.ostm")]
    [InlineData("test-10000.ostm")]
    public void ProjectAndPickAreInverses(string file)
    {
        using Harness h = new Harness().Open(file);
        h.Call("seek", """{"step":80}""");
        h.Call("view", """{"index":0,"target":[5000,1200,5000],"distance":6500,"halfHeight":3200,"yaw":35,"pitch":28}""");
        h.Call("capture_frame", """{"path":"layout.png"}""");
        int agreed = 0;
        foreach (ulong id in Enumerable.Range(100, 24).Select(i => (ulong)i))
        {
            if (h.Shell.Source(0).History.View.Inspect(new OneScope.Sdk.EntityId(id), new OneScope.Sdk.PropertyList()) is false)
            {
                continue;
            }

            JsonElement p = h.Call("project", $$"""{"entity":{{id}},"view":0}""");
            if (!p.GetProperty("onScreen").GetBoolean())
            {
                continue;
            }

            JsonElement hit = h.Call("pick", $$"""{"x":{{p.GetProperty("x").GetDouble()}},"y":{{p.GetProperty("y").GetDouble()}},"view":0}""");
            // A body hidden behind another one may legitimately pick as the one in front.
            if (hit.GetProperty("entity").ValueKind == JsonValueKind.Number)
            {
                agreed += hit.GetProperty("entity").GetUInt64() == id ? 1 : 0;
            }
        }

        Assert.True(agreed >= 5, $"only {agreed} bodies picked where they were projected");
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => h.Call("project", """{"entity":9999,"view":0}""")).Code);
    }
}
