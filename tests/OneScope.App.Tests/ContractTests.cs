using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>Fields, links, shared-entity diffs and ghost sources, driven through the same operations an agent uses.</summary>
[Collection("shell")]
public sealed class ContractTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onescope-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Q(string s) => JsonSerializer.Serialize(s);

    // ---- fields ---------------------------------------------------------------------------------------------------------

    private static (string Sha, string Renderer, string File) CaptureField(string file, int seek, string overlay, int view, string size)
    {
        using Harness h = new Harness(960, 540).Open(file);
        h.Call("seek", $$"""{"step":{{seek}}}""");
        h.Call("overlay", $$"""{"view":{{view}},"overlay":"{{overlay}}","on":true}""");
        h.Call("overlay", $$"""{"view":{{view}},"overlay":"Bodies","on":true}""");
        h.Call("view", """{"index":0,"target":[5000,1200,5000],"distance":16500,"halfHeight":5600,"yaw":35,"pitch":50}""");
        h.Call("view", """{"index":1,"target":[5000,5000,0],"halfHeight":5600}""");
        JsonElement r = h.Call("capture_frame", $$"""{"path":"f.png","size":"{{size}}","view":{{view}}}""");
        string path = r.GetProperty("path").GetString()!;
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(path, copy);
        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }

    [Theory]
    [InlineData("field-vertex-2d", "test-field-2d.ostm", 300, "Density field", 0)]
    [InlineData("field-element-3d", "test-field-3d.ostm", 500, "Occupancy field", 0)]
    public void AFieldViewWithItsLegendIsByteIdenticalAcrossThreeRunsAndMatchesTheGolden(string golden, string file, int seek, string overlay, int view)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(CaptureField(file, seek, overlay, view, "480x360"));
            }

            GoldenTests.Check(golden, runs);
        }
        finally
        {
            foreach (var r in runs)
            {
                File.Delete(r.File);
            }
        }
    }

    [Fact]
    public void TheFieldOperationReturnsTheModulesOwnValuesAtAnEntityAndAtAPoint()
    {
        using Harness h = new Harness().Open("test-field-2d.ostm");
        h.Call("seek", """{"step":300}""");
        JsonElement list = h.Call("field");
        Assert.Equal(2, list.GetProperty("fields").GetArrayLength());
        Assert.Equal("density", list.GetProperty("fields")[0].GetProperty("id").GetString());
        Assert.Equal("viridis", list.GetProperty("fields")[0].GetProperty("map").GetString());

        IScopeWorld world = h.Shell.Source(0).History.World;
        JsonElement atBody = h.Call("field", """{"field":"density","entity":104}""");
        Assert.True(world.TryReadField("density", new EntityId(104), out double expected));
        Assert.Equal(expected, atBody.GetProperty("value").GetDouble());
        Harness.Report($"field at entity: {atBody}");

        JsonElement atPoint = h.Call("field", """{"field":"occupancy","point":[1250,3750,0]}""");
        Assert.True(world.TryReadFieldAt("occupancy", new Vector3(1250, 3750, 0), out double cell));
        Assert.Equal(cell, atPoint.GetProperty("value").GetDouble());
        Harness.Report($"field at point: {atPoint}");

        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("field", """{"field":"nope","entity":104}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("field", """{"field":"density"}""")).Code);
        Assert.Equal("no_value", Assert.Throws<ControlException>(() => h.Call("field", """{"field":"density","point":[-5,0,0]}""")).Code);
        Assert.Equal("no_value", Assert.Throws<ControlException>(() => h.Call("field", """{"field":"density","entity":99999}""")).Code);

        JsonElement sources = h.Call("sources");
        Assert.Equal("occupancy", sources.GetProperty("sources")[0].GetProperty("fields")[1].GetProperty("id").GetString());
    }

    // ---- links ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSelectionFollowsATransferAcrossSourcesBothWays()
    {
        using Harness h = new Harness().Open("test-link-left.ostm").Open("test-link-right.ostm");
        Assert.Equal("tm-left", h.Call("sources").GetProperty("sources")[0].GetProperty("linkKey").GetString());
        JsonElement links = h.Call("links");
        Assert.Single(links.GetProperty("links").EnumerateArray());
        Assert.Equal(150, links.GetProperty("links")[0].GetProperty("step").GetInt64());
        Assert.False(links.GetProperty("links")[0].GetProperty("crossed").GetBoolean());

        h.Call("seek", """{"step":100}""");
        JsonElement before = h.Call("inspect", """{"source":0,"entity":103}""");
        JsonElement beforeSel = h.Call("selection");
        Harness.Report($"inspect before the transfer: source {before.GetProperty("source")} entity {before.GetProperty("entity")} step {before.GetProperty("step")}");
        Assert.Equal(0, beforeSel.GetProperty("source").GetInt32());
        Assert.Equal(103, beforeSel.GetProperty("entity").GetInt64());

        // Across the transfer step the entity is selected in the other source, with that source's own properties.
        h.Call("seek", """{"step":151}""");
        JsonElement after = h.Call("selection");
        Harness.Report($"selection after the transfer: {after}");
        Assert.Equal(1, after.GetProperty("source").GetInt32());
        Assert.Equal(103, after.GetProperty("entity").GetInt64());
        Assert.Equal(151, after.GetProperty("step").GetInt64());
        Assert.Equal("body", after.GetProperty("kind").GetString());
        Assert.True(h.Call("links").GetProperty("links")[0].GetProperty("crossed").GetBoolean());

        // Back across it, the selection returns to where the entity lived at that step.
        h.Call("seek", """{"step":149}""");
        Assert.Equal(0, h.Call("selection").GetProperty("source").GetInt32());
        h.Call("seek", """{"step":150}""");
        Assert.Equal(1, h.Call("selection").GetProperty("source").GetInt32());

        // The inspector panel's model follows too.
        Assert.Contains(h.Shell.Inspected(), r => r.Name == "position");
        Assert.Equal(1, h.Shell.ActiveSource);
    }

    [Fact]
    public void ATrackingViewFollowsTheEntityIntoTheOtherSource()
    {
        using Harness h = new Harness().Open("test-link-left.ostm").Open("test-link-right.ostm");
        h.Call("seek", """{"step":100}""");
        h.Call("inspect", """{"source":0,"entity":103}""");
        JsonElement on = h.Call("track", """{"view":0,"on":true}""");
        Assert.Equal(0, on.GetProperty("source").GetInt32());
        Assert.True(on.GetProperty("track").GetBoolean());
        Vector3 tracked = h.Shell.Views[0].Target;
        IScopeWorld left = h.Shell.Source(0).History.World;
        var rows = new PropertyList();
        Assert.True(left.Inspect(new EntityId(103), rows));
        Vector3 position = rows.Rows.First(r => r.Name == "position").Value.GetVector();
        Assert.Equal(position.X, tracked.X);
        Assert.Equal(position.Y, tracked.Y);

        h.Call("seek", """{"step":160}""");
        Assert.Equal(1, h.Shell.Views[0].Source);
        IScopeWorld right = h.Shell.Source(1).History.World;
        Assert.True(right.Inspect(new EntityId(103), rows));
        position = rows.Rows.First(r => r.Name == "position").Value.GetVector();
        Assert.Equal(position.X, h.Shell.Views[0].Target.X);
        Harness.Report($"tracking view moved to source {h.Shell.Views[0].Source}, target {h.Shell.Views[0].Target}");
    }

    // ---- shared-entity diff ---------------------------------------------------------------------------------------------

    [Fact]
    public void ASharedDiffIgnoresWhatOnlyTheWholeWorldHoldsAndNamesAPlantedDifference()
    {
        using Harness h = new Harness().Open("test-2d-600.ostm");
        h.Call("seek", """{"step":200}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"remove","args":{"target":105},"step":true}""");
        h.Call("step", """{"source":1,"delta":3}""");

        JsonElement partial = h.Call("diff_shared", """{"source":1}""");
        Harness.Report($"diff_shared partial vs whole: {partial}");
        Assert.True(partial.GetProperty("equal").GetBoolean());
        Assert.Equal(13, partial.GetProperty("shared").GetInt32());
        Assert.Equal(0, partial.GetProperty("onlySource").GetInt32());
        Assert.Equal(1, partial.GetProperty("onlyOther").GetInt32());
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("difference").ValueKind);
        // The ordinary diff calls the removed body a difference.
        Assert.False(h.Call("diff", """{"source":1}""").GetProperty("equal").GetBoolean());

        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":102,"delta":[500,0,0]},"step":true}""");
        JsonElement planted = h.Call("diff_shared", """{"source":1,"step":205}""");
        Harness.Report($"diff_shared with a planted difference: {planted}");
        Assert.False(planted.GetProperty("equal").GetBoolean());
        Assert.Equal(102, planted.GetProperty("difference").GetProperty("entity").GetInt64());
        Assert.Equal("position", planted.GetProperty("difference").GetProperty("field").GetString());
        Assert.Equal(1, planted.GetProperty("onlyOther").GetInt32());

        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("diff_shared", """{"source":0}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("diff_shared", """{"source":1,"map":"x"}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("diff_shared", """{"source":1,"step":5000}""")).Code);
    }

    [Fact]
    public void ALinkedPairIsAPartialWorldAgainstAWholeOneUntilTheTransfer()
    {
        using Harness h = new Harness().Open("test-link-left.ostm").Open("test-link-right.ostm");
        // Before the transfer the left world holds body 103 and the right one does not; the other bodies move the same in both.
        JsonElement before = h.Call("diff_shared", """{"source":1,"other":0,"step":100}""");
        Harness.Report($"diff_shared right vs left before the transfer: {before}");
        Assert.True(before.GetProperty("equal").GetBoolean());
        Assert.Equal(9, before.GetProperty("shared").GetInt32());
        Assert.Equal(0, before.GetProperty("onlySource").GetInt32());
        Assert.Equal(1, before.GetProperty("onlyOther").GetInt32());

        // The declared link pairs 103 with 103, which no step holds in both worlds: nothing is shared.
        JsonElement linked = h.Call("diff_shared", """{"source":0,"other":1,"step":300,"map":"links"}""");
        Assert.Equal(0, linked.GetProperty("shared").GetInt32());
        Assert.Equal("links", linked.GetProperty("map").GetString());
    }

    // ---- ghosts ---------------------------------------------------------------------------------------------------------

    private static string GhostFolder()
    {
        string dir = TempDir();
        File.Copy(Harness.Data("test-2d-600.ostm"), Path.Combine(dir, "test-2d-600.ostm"));
        File.Copy(Harness.Data("test-2d-ghost.ospc"), Path.Combine(dir, "test-2d-ghost.ospc"));
        return dir;
    }

    [Fact]
    public void AGhostOverARealSourceScrubsWithItAndRefusesToFork()
    {
        string dir = GhostFolder();
        try
        {
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-600.ostm"))}}}""");
            JsonElement ghostOpen = h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-ghost.ospc"))}}}""");
            Assert.Equal("oscope.posecache", ghostOpen.GetProperty("module").GetString());
            Assert.Equal(1, ghostOpen.GetProperty("source").GetInt32());

            // It is drawn over the real source's views and has none of its own.
            JsonElement ghosts = h.Call("ghost");
            Assert.Equal(1, ghosts.GetProperty("ghosts")[0].GetProperty("source").GetInt32());
            Assert.Equal(0, ghosts.GetProperty("ghosts")[0].GetProperty("views")[0].GetInt32());
            Assert.DoesNotContain(h.Shell.Views, v => v.Source == 1);

            // The offer to open the real simulation, with what the cache names.
            JsonElement offer = h.Call("sources").GetProperty("sources")[1].GetProperty("ghost");
            Harness.Report($"ghost offer in sources: {offer}");
            Assert.Equal("oscope.test", offer.GetProperty("module").GetString());
            Assert.Equal("1.0", offer.GetProperty("libraryVersion").GetString());
            Assert.Equal("test-box", offer.GetProperty("scene").GetString());
            Assert.Equal("test-2d-600.ostm", offer.GetProperty("replay").GetString());
            Assert.Equal("open_real", offer.GetProperty("offer").GetProperty("op").GetString());
            Assert.True(offer.GetProperty("offer").GetProperty("available").GetBoolean());

            // Seeking gives the ghost's pose: the real position moved by the dump's 400 mm offset, at the cache's frame for that step.
            PoseCache cache = PoseCache.Parse(File.ReadAllBytes(Path.Combine(dir, "test-2d-ghost.ospc")), "c");
            foreach (long step in new long[] { 200, 217, 239, 250 })
            {
                h.Call("seek", $$"""{"step":{{step}}}""");
                JsonElement ghost = h.Call("inspect", """{"source":1,"entity":104}""");
                JsonElement real = h.Call("inspect", """{"source":0,"entity":104}""");
                int frame = (int)Math.Min(step - 200, cache.FrameCount - 1);
                int entity = 4;
                Assert.True(cache.TryGetPose(frame, entity, out Vector3 expected, out _));
                JsonElement gp = ghost.GetProperty("properties").EnumerateArray().First(p => p.GetProperty("name").GetString() == "position").GetProperty("value");
                JsonElement rp = real.GetProperty("properties").EnumerateArray().First(p => p.GetProperty("name").GetString() == "position").GetProperty("value");
                Assert.Equal(expected.X, (float)gp[0].GetDouble());
                Assert.Equal(expected.Y, (float)gp[1].GetDouble());
                if (step <= 239)
                {
                    Assert.Equal(rp[0].GetDouble() + 400, gp[0].GetDouble());
                    Assert.Equal(rp[1].GetDouble(), gp[1].GetDouble());
                }
            }

            h.Call("seek", """{"step":210}""");
            ControlException refused = Assert.Throws<ControlException>(() => h.Call("fork", """{"source":1}"""));
            Assert.Equal("ghost_source", refused.Code);
            Assert.Contains("ghost", refused.Message);
            Assert.Equal("read_only", Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":104,"delta":[1,1,0]}}""")).Code);
            Assert.Equal(2, h.Shell.Sources.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OpeningTheRealSimulationFromAGhostLandsOnTheGhostsFrame()
    {
        string dir = GhostFolder();
        try
        {
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-ghost.ospc"))}}}""");
            Assert.Equal(200, h.Call("sources").GetProperty("start").GetInt64());
            h.Call("seek", """{"step":215}""");
            Assert.Equal(215, h.Call("sources").GetProperty("step").GetInt64());

            JsonElement real = h.Call("open_real", """{"source":0}""");
            Harness.Report($"open_real: {real}");
            Assert.Equal(1, real.GetProperty("source").GetInt32());
            Assert.Equal("oscope.test", real.GetProperty("module").GetString());
            Assert.Equal(215, real.GetProperty("step").GetInt64());
            Assert.Equal(Harness.StraightHashes("test-2d-600.ostm")[215], Convert.ToUInt64(real.GetProperty("hash").GetString(), 16));
            Assert.Equal(1, h.Call("ghost").GetProperty("ghosts")[0].GetProperty("views").GetArrayLength());
            Assert.NotEmpty(h.Call("fork", """{"source":1}""").GetProperty("fork").EnumerateObject());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AGhostSurvivesASessionSaveAndOpenAndStaysUnforkable()
    {
        string dir = GhostFolder();
        try
        {
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-600.ostm"))}}}""");
            h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-ghost.ospc"))}}}""");
            h.Call("seek", """{"step":230}""");
            string saved = h.Call("session_save", """{"path":"s.oscope"}""").GetProperty("path").GetString()!;
            JsonElement opened = h.Call("session_open", $$"""{"path":{{Q(saved)}}}""");
            Assert.Equal(2, opened.GetProperty("sources").GetArrayLength());
            JsonElement offer = h.Call("sources").GetProperty("sources")[1].GetProperty("ghost");
            Assert.Equal("test-2d-600.ostm", offer.GetProperty("replay").GetString());
            Assert.Equal("ghost_source", Assert.Throws<ControlException>(() => h.Call("fork", """{"source":1}""")).Code);
            JsonElement ghosts = h.Call("ghost");
            Assert.Equal(0, ghosts.GetProperty("ghosts")[0].GetProperty("views")[0].GetInt32());
            Harness.Report($"session round trip with a ghost: {ghosts}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ACacheNamingAnUnsafeOrMissingReplayOffersNothing()
    {
        string dir = TempDir();
        try
        {
            var info = new PoseCacheInfo(2, LengthUnit.Millimetre, 0, "oscope.test", "1.0", "s", "../test-2d-600.ostm", "x");
            PoseEntity[] entities = [new(new EntityId(100), "a", PoseShape.Point, default)];
            string path = Path.Combine(dir, "evil.ospc");
            File.WriteAllBytes(path, TestGhost.ToBytes(PoseCache.Create(info, entities, [1, 2, 0, 0, 0, 0, 1])));
            File.Copy(Harness.Data("test-2d-600.ostm"), Path.Combine(Path.GetDirectoryName(dir)!, "test-2d-600.ostm"), overwrite: true);
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(path)}}}""");
            Assert.False(h.Call("sources").GetProperty("sources")[0].GetProperty("ghost").GetProperty("offer").GetProperty("available").GetBoolean());
            Assert.Equal("not_found", Assert.Throws<ControlException>(() => h.Call("open_real", """{"source":0}""")).Code);
        }
        finally
        {
            File.Delete(Path.Combine(Path.GetDirectoryName(dir)!, "test-2d-600.ostm"));
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AGhostOverARealSourceIsDrawnAsAGhostAndMatchesTheGolden()
    {
        string dir = GhostFolder();
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                using Harness h = new Harness(960, 540, bundled: true);
                h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-600.ostm"))}}}""");
                h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-ghost.ospc"))}}}""");
                h.Call("seek", """{"step":220}""");
                h.Call("view", """{"index":0,"target":[5400,5000,0],"halfHeight":5600}""");
                JsonElement r = h.Call("capture_frame", """{"path":"g.png","size":"480x360","view":0}""");
                string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
                File.Copy(r.GetProperty("path").GetString()!, copy);
                runs.Add((r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy));
            }

            GoldenTests.Check("ghost-view-2d", runs);
        }
        finally
        {
            foreach (var r in runs)
            {
                File.Delete(r.File);
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheGpuValidationLayerHasNothingToSayAboutFieldAndGhostViews()
    {
        if (Environment.GetEnvironmentVariable("ONEGAME_GPU_VALIDATION") != "1")
        {
            Assert.Skip("Set ONEGAME_GPU_VALIDATION=1 (with the Khronos validation layer on VK_ADD_LAYER_PATH) to run this.");
        }

        string dir = GhostFolder();
        try
        {
            using Harness h = new Harness(960, 540, bundled: true);
            h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-600.ostm"))}}}""");
            h.Call("open", $$"""{"path":{{Q(Path.Combine(dir, "test-2d-ghost.ospc"))}}}""");
            h.Call("open", $$"""{"path":{{Q(Harness.Data("test-field-3d.ostm"))}}}""");
            h.Call("seek", """{"step":220}""");
            h.Call("capture_frame", """{"path":"a.png"}""");
            h.Call("overlay", """{"view":2,"overlay":"Density field","on":true}""");
            h.Call("overlay", """{"view":2,"overlay":"Occupancy field","on":true}""");
            h.Call("capture_frame", """{"path":"b.png","view":2,"size":"640x360"}""");
            h.Call("capture_frame", """{"path":"c.png","view":3,"size":"640x360"}""");
            h.Call("capture_frame", """{"path":"d.png","view":0,"size":"640x360"}""");
            string[] messages = h.Game.TakeGpuValidationMessages();
            Harness.Report($"validation: {messages.Length} messages over 4 captures of vertex- and element-field views (2D and 3D), a ghost overlay and the whole shell");
            Assert.Empty(messages);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
