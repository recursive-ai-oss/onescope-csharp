using System.Numerics;
using System.Text.Json;
using OneGame.Control;
using OneScope.Sdk;
using Xunit;
using UiRect = OneGame.UI.Rect;

namespace OneScope.App.Tests;

/// <summary>The shell's window features driven as a user or an agent drives them: the poke panel, timeline zoom, default framing, attach and the live cap.</summary>
[Collection("shell")]
public sealed class UsabilityTests
{
    private static string P(string action, float x, float y) => JsonSerializer.Serialize(new { action, x, y });

    /// <summary>Runs two frames so bindings re-evaluate and the UI is laid out before the next click.</summary>
    private static void Settle(Harness h) => h.Game.Step(2);

    private static void Click(Harness h, string id)
    {
        h.Call("ui_click", JsonSerializer.Serialize(new { id }));
        Settle(h);
    }

    /// <summary>Focuses a text field, selects what is in it and types over it.</summary>
    private static void SetField(Harness h, string id, string text)
    {
        Click(h, id);
        h.Call("input_key", """{"key":"a","modifiers":["Control"]}""");
        h.Call("input_text", JsonSerializer.Serialize(new { text }));
        Settle(h);
    }

    private static string HashAt(Harness h, long step, int source)
    {
        h.Call("seek", $$"""{"step":{{step}}}""");
        return h.Call("sources").GetProperty("sources")[source].GetProperty("hash").GetString()!;
    }

    private static Dictionary<long, string> Hashes(Harness h, int source, IEnumerable<long> steps) =>
        steps.ToDictionary(s => s, s => HashAt(h, s, source));

    private static int EntityCount(Harness h, int source) => h.Call("entities", $$"""{"source":{{source}},"kind":"body"}""").GetProperty("count").GetInt32();

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

    [Fact]
    public void TheWindowPokesAForkThroughThePanelAndTheSavedReplayReproducesIt()
    {
        using Harness h = new Harness(1280, 720).Open("test-10000.ostm");
        string file = Harness.Data("test-10000.ostm");
        string shaBefore = Harness.Sha(File.ReadAllBytes(file));
        long[] probe = [0, 100, 200, 201, 205, 210, 400];
        h.Call("seek", """{"step":200}""");
        Dictionary<long, string> original = Hashes(h, 0, probe);
        h.Call("seek", """{"step":200}""");

        // The panel belongs to forks: the opened file shows none.
        Settle(h);
        Assert.False(h.Game.Ui.Find("pokes-panel")?.IsEffectivelyVisible ?? false);
        Click(h, "fork");
        Assert.Equal(2, h.Shell.Sources.Count);
        Assert.True(h.Game.Ui.Find("pokes-panel")!.IsEffectivelyVisible);
        Assert.Equal(24, EntityCount(h, 1));

        // Spawn: a vector typed into the field.
        Click(h, "poke/pick/spawn");
        SetField(h, "poke/spawn/position", "5000,5000,5000");
        Click(h, "poke/apply");
        Assert.Equal(201, h.Shell.Sources[1].History.EndStep);
        Assert.Equal(25, EntityCount(h, 1));

        // Remove: the target field is empty, so the selected entity is the target.
        h.Call("inspect", """{"entity":101,"source":1}""");
        Click(h, "poke/pick/remove");
        Click(h, "poke/apply");
        Assert.Equal(202, h.Shell.Sources[1].History.EndStep);
        Assert.Equal(24, EntityCount(h, 1));
        Assert.DoesNotContain(h.Call("entities", """{"source":1}""").GetProperty("entities").EnumerateArray(), e => e.GetProperty("id").GetInt64() == 101);

        // Set a parameter: gravity, an integer.
        Click(h, "poke/pick/set_gravity");
        SetField(h, "poke/set_gravity/gravity", "-12");
        Click(h, "poke/apply");
        Assert.Equal(203, h.Shell.Sources[1].History.EndStep);
        JsonElement world = h.Call("inspect", """{"entity":1,"source":1}""");
        Assert.Contains(world.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "gravity" && p.GetProperty("value").GetInt64() == -12);

        // A poke with two typed fields, and the log says what happened.
        Click(h, "poke/pick/impulse");
        SetField(h, "poke/impulse/target", "102");
        SetField(h, "poke/impulse/delta", "400,900,0");
        Click(h, "poke/apply");
        Assert.Equal(204, h.Shell.Sources[1].History.EndStep);

        // A bad entry is refused and logged, not thrown into the frame, and queues nothing.
        SetField(h, "poke/impulse/delta", "1,2,x");
        Click(h, "poke/apply");
        Assert.Equal(204, h.Shell.Sources[1].History.EndStep);
        Assert.Contains(h.Shell.Log(), r => r.Kind == "error" && r.Text.Contains("'x'", StringComparison.Ordinal));

        // The poke applied without a following step waits for the Step button.
        SetField(h, "poke/impulse/delta", "10,10,0");
        Click(h, "poke/step-after");
        Click(h, "poke/apply");
        Assert.Equal(204, h.Shell.Sources[1].History.EndStep);
        Assert.Equal(1, h.Shell.Sources[1].Branch!.PendingPokes);
        Click(h, "poke/step");
        Assert.Equal(205, h.Shell.Sources[1].History.EndStep);

        // A drag of a body, as before, on the fork's own view.
        int viewIndex = h.Shell.Views.Count - 1;
        h.Call("capture_frame", """{"path":"layout.png"}""");
        UiRect rect = ViewPicking.Round(h.Game.Ui.Find($"view/{viewIndex}")!.AbsoluteBounds);
        (float x, float y, ulong body) = FindBody(h, viewIndex, rect);
        h.Call("input_pointer", P("down", x, y));
        for (int i = 1; i <= 5; i++)
        {
            h.Call("input_pointer", P("move", x + (3 * i), y - (2 * i)));
        }

        h.Call("input_pointer", P("up", x + 15, y - 10));
        Assert.Equal(210, h.Shell.Sources[1].History.EndStep);
        Assert.Equal(body, h.Shell.Selected.Value);

        // The saved replay reproduces every step of the fork; the original is unchanged.
        string saved = h.Call("replay_save", """{"source":1,"path":"panel.ostm"}""").GetProperty("path").GetString()!;
        Dictionary<long, string> forkHashes = Hashes(h, 1, Enumerable.Range(0, 211).Select(i => (long)i));
        int again = h.Call("open", JsonSerializer.Serialize(new { path = saved })).GetProperty("source").GetInt32();
        foreach (KeyValuePair<long, string> kv in forkHashes)
        {
            Assert.Equal(kv.Value, HashAt(h, kv.Key, again));
        }

        Assert.NotEqual(original[201], forkHashes[201]);
        Assert.Equal(original, Hashes(h, 0, probe));
        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));
        Harness.Report($"window pokes: spawn, remove, set_gravity, impulse, typed-error, step button and a drag on the fork; {forkHashes.Count} steps reproduced from the saved replay; original sha256 unchanged {shaBefore[..16]}");
    }

    [Fact]
    public void SpawnIsAPokeOfTheTestModuleAndReplays()
    {
        using Harness h = new Harness().Open("test-10000.ostm");
        h.Call("fork", """{"step":50}""");
        h.Call("poke", """{"source":1,"poke":"spawn","args":{"position":[3000,4000,2000]},"step":true}""");
        JsonElement spawned = h.Call("inspect", """{"entity":124,"source":1}""");
        Assert.Contains(spawned.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "position");
        h.Call("step", """{"source":1,"delta":30}""");
        string saved = h.Call("replay_save", """{"source":1,"path":"spawn.ostm"}""").GetProperty("path").GetString()!;
        Dictionary<long, string> a = Hashes(h, 1, [50, 51, 60, 80]);
        int again = h.Call("open", JsonSerializer.Serialize(new { path = saved })).GetProperty("source").GetInt32();
        foreach (KeyValuePair<long, string> kv in a)
        {
            Assert.Equal(kv.Value, HashAt(h, kv.Key, again));
        }

        // Fill the world: spawns past 64 bodies do nothing, the same way on every replay.
        h.Call("seek", """{"step":81}""");
        for (int i = 0; i < 39; i++)
        {
            h.Call("poke", """{"source":1,"poke":"spawn","args":{"position":[1000,1000,1000]},"step":true}""");
        }

        Assert.Equal(64, EntityCount(h, 1));
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("poke", """{"source":1,"poke":"spawn","args":{"position":[1,1,1]}}""")).Code);
    }

    [Fact]
    public void TheTimelineFitsAFiveStepForkInATenThousandStepHistoryAndZooms()
    {
        using Harness h = new Harness(1280, 720).Open("test-10000.ostm");
        h.Call("seek", """{"step":4000}""");
        h.Call("fork");
        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":104,"delta":[3000,4000,0]},"step":true}""");
        h.Call("step", """{"source":1,"delta":4}""");
        Assert.Equal(4005, h.Shell.Sources[1].History.EndStep);

        JsonElement all = h.Call("timeline");
        Assert.False(all.GetProperty("zoomed").GetBoolean());
        Assert.Equal(10000, all.GetProperty("to").GetInt64());

        JsonElement fit = h.Call("timeline", """{"fit":"fork"}""");
        long from = fit.GetProperty("from").GetInt64();
        long to = fit.GetProperty("to").GetInt64();
        Assert.True(fit.GetProperty("zoomed").GetBoolean());
        Assert.True(from <= 4000 && to >= 4005, $"{from}..{to}");
        Assert.True(to - from <= 20, $"{from}..{to}");

        // Readable: every step of the fork gets at least 40 pixels of a 1280 wide window.
        h.Call("capture_frame", """{"path":"layout.png"}""");
        UiRect bounds = h.Game.Ui.Find("timeline")!.AbsoluteBounds;
        Assert.True(TimelineLayout.Width(bounds) / (to - from) >= 40, $"{TimelineLayout.Width(bounds) / (to - from)} px per step");

        // Clicking the timeline lands on the step under the pointer in the zoomed window.
        float px = TimelineLayout.XOf(bounds, 4003, h.Shell.TimelineRange);
        h.Call("input_pointer", P("down", px, bounds.Y + 40));
        h.Call("input_pointer", P("up", px, bounds.Y + 40));
        Assert.Equal(4003, h.Shell.Step);

        // Fit all, zoom and pan keep the window inside the axis; the buttons do the same.
        Assert.False(h.Call("timeline", """{"fit":"all"}""").GetProperty("zoomed").GetBoolean());
        JsonElement zoom = h.Call("timeline", """{"zoom":8,"center":4003}""");
        Assert.InRange(zoom.GetProperty("to").GetInt64() - zoom.GetProperty("from").GetInt64(), 1200, 1300);
        JsonElement pan = h.Call("timeline", """{"pan":100000}""");
        Assert.Equal(10000, pan.GetProperty("to").GetInt64());
        Click(h, "timeline-fit-all");
        Assert.False(h.Shell.TimelineZoomed);
        Click(h, "timeline-zoom-in");
        Assert.True(h.Shell.TimelineZoomed);
        long span = h.Shell.TimelineRange.Length;
        Click(h, "timeline-zoom-out");
        Assert.True(h.Shell.TimelineRange.Length > span || !h.Shell.TimelineZoomed);
        Click(h, "timeline-fit-fork");
        Assert.True(h.Shell.TimelineRange.Min <= 4000 && h.Shell.TimelineRange.Max >= 4005);
        h.Call("input_pointer", JsonSerializer.Serialize(new { action = "wheel", x = px, y = bounds.Y + 40, dy = 3 }));
        Assert.True(h.Shell.TimelineRange.Min <= 4005);

        // Bad requests name what was wrong; nothing moves a world.
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("timeline", """{"from":50,"to":10}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("timeline", """{"zoom":-1}""")).Code);
        using Harness plain = new Harness().Open("test-2d-600.ostm");
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => plain.Call("timeline", """{"fit":"fork"}""")).Code);
    }

    private static (string Sha, string Renderer, string File) CaptureTimeline(bool zoomed)
    {
        using Harness h = new Harness(1280, 720).Open("test-10000.ostm");
        h.Call("seek", """{"step":4000}""");
        h.Call("fork", """{"view":false}""");
        h.Call("poke", """{"source":1,"poke":"impulse","args":{"target":104,"delta":[3000,4000,0]},"step":true}""");
        h.Call("step", """{"source":1,"delta":4}""");
        h.Call("seek", """{"step":4003}""");
        h.Call("view", """{"index":0,"target":[5000,1200,5000],"distance":6500,"yaw":35,"pitch":28}""");
        h.Call("view", """{"index":1,"target":[5000,3000,0],"halfHeight":3400}""");
        h.Call("timeline", zoomed ? """{"from":3900,"to":4300}""" : """{"fit":"fork"}""");
        JsonElement r = h.Call("capture_frame", """{"path":"timeline.png"}""");
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(r.GetProperty("path").GetString()!, copy);
        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }

    [Theory]
    [InlineData("timeline-fit-fork", false)]
    [InlineData("timeline-zoomed", true)]
    public void TimelineCapturesAreByteIdenticalAcrossThreeRunsAndMatchTheGolden(string golden, bool zoomed)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(CaptureTimeline(zoomed));
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

    private static (string Sha, string Renderer, string File) CaptureFramed(string file, bool bundled)
    {
        // No camera is set: what the view got when the source opened is what is captured.
        using Harness h = new Harness(960, 540, bundled).Open(file);
        h.Call("seek", """{"step":30}""");
        JsonElement r = h.Call("capture_frame", """{"path":"framing.png","view":0,"size":"480x360"}""");
        string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
        File.Copy(r.GetProperty("path").GetString()!, copy);
        return (r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy);
    }

    [Theory]
    [InlineData("framing-test-3d", "test-10000.ostm", false)]
    [InlineData("framing-physics-3d", "physics-3d.oppr", true)]
    public void DefaultFramedViewsAreByteIdenticalAcrossThreeRunsAndMatchTheGolden(string golden, string file, bool bundled)
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                runs.Add(CaptureFramed(file, bundled));
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

    /// <summary>The share of the view's smaller side the drawn bodies cover, and whether all of them are inside it.</summary>
    private static (double Fill, bool AllInside) BodyFill(Harness h, int view, Func<DrawItem, bool> isBody)
    {
        h.Call("capture_frame", """{"path":"layout.png"}""");
        UiRect rect = ViewPicking.Round(h.Game.Ui.Find($"view/{view}")!.AbsoluteBounds);
        var list = new DrawList();
        h.Shell.Sources[h.Shell.Views[view].Source].History.View.Draw(list, new DrawRequest(h.Shell.Views[view].Overlays));
        Vector2 lo = new(float.MaxValue);
        Vector2 hi = new(float.MinValue);
        bool inside = true;
        int n = 0;

        // A floor far larger than the bodies is scenery, as it is for the framing itself.
        float[] sizes = [.. list.Items.ToArray().Where(i => i.Kind is not (DrawKind.Mesh or DrawKind.Line or DrawKind.Arrow or DrawKind.Label) && isBody(i)).Select(i => i.Size.Length()).Order()];
        float limit = sizes.Length == 0 ? float.MaxValue : sizes[sizes.Length / 2] * DrawBounds.SceneryFactor;
        foreach (ref readonly DrawItem item in list.Items)
        {
            if (item.Kind is DrawKind.Mesh or DrawKind.Line or DrawKind.Arrow or DrawKind.Label || !isBody(item) || item.Size.Length() > limit)
            {
                continue;
            }

            Vector2 s = ViewPicking.ToScreen(h.Shell.Views[view], rect, item.A);
            lo = Vector2.Min(lo, s);
            hi = Vector2.Max(hi, s);
            inside &= rect.Contains(s.X, s.Y);
            n++;
        }

        Assert.True(n > 0);
        return (Math.Max((hi.X - lo.X) / rect.Width, (hi.Y - lo.Y) / rect.Height), inside);
    }

    [Theory]
    [InlineData("test-10000.ostm", false, 0)]
    [InlineData("test-10000.ostm", false, 1)]
    [InlineData("test-2d-600.ostm", false, 0)]
    [InlineData("physics-3d.oppr", true, 0)]
    [InlineData("physics-3d.oppr", true, 1)]
    [InlineData("physics-2d.oppr", true, 0)]
    public void DefaultFramingFillsTheViewWithTheMovingBodies(string file, bool bundled, int view)
    {
        using Harness h = new Harness(1280, 720, bundled).Open(file);
        h.Call("seek", """{"step":30}""");
        h.Call("view", $$"""{"index":{{view}},"frame":true}""");
        var kinds = new Dictionary<ulong, string>();
        var all = new List<EntityInfo>();
        h.Shell.Sources[0].History.View.CollectEntities(all);
        foreach (EntityInfo e in all)
        {
            kinds[e.Id.Value] = e.Kind;
        }

        (double fill, bool inside) = BodyFill(h, view, item => !item.Entity.IsSome || kinds.GetValueOrDefault(item.Entity.Value) is not ("static" or "sensor"));
        Harness.Report($"framing {file} view {view}: bodies cover {fill:P0} of the view's longer side, all inside: {inside}");
        Assert.True(inside);
        Assert.InRange(fill, 0.25, 0.95);
    }

    [Fact]
    public void FrameSelectionCentresTheSelectedEntityAndFrameIgnoresAHugeStaticFloor()
    {
        using Harness h = new Harness().Open("test-10000.ostm");
        h.Call("seek", """{"step":30}""");
        h.Call("view", """{"index":1,"frame":true}""");
        float whole = h.Shell.Views[1].HalfHeight;

        // The floor and the sensor zone are 10 m, but they are static or sensor: the frame is the bodies', and a missing body doesn't matter.
        Assert.InRange(whole, 3000, 6200);

        h.Call("inspect", """{"entity":105}""");
        h.Call("view", """{"index":1,"frameSelection":true}""");
        ViewState v = h.Shell.Views[1];
        JsonElement props = h.Call("inspect", """{"entity":105}""");
        JsonElement pos = props.GetProperty("properties").EnumerateArray().First(p => p.GetProperty("name").GetString() == "position").GetProperty("value");
        Assert.Equal(pos[0].GetDouble(), v.Target.X, 1);
        Assert.Equal(pos[1].GetDouble(), v.Target.Y, 1);
        Assert.True(v.HalfHeight < whole / 5, $"{v.HalfHeight} vs {whole}");

        // Nothing selected in the source: framing the selection falls back to the whole frame.
        h.Call("view", """{"index":1,"frame":true}""");
        Assert.Equal(whole, h.Shell.Views[1].HalfHeight, 1);
    }

    [Fact]
    public async Task AttachReturnsAtOnceWhileAHostThatStepsRarelyTakesItsSnapshot()
    {
        string dir = LiveAndSessionTests.NewDir();
        using var host = new MiniHost(dir, stepMicros: 900_000);
        using Harness h = new();
        host.Start();
        LiveAndSessionTests.WaitFor(() => host.Steps >= 1, "the host to run");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        JsonElement pending = await Task.Run(() => h.Call("attach", $$"""{"pid":{{Environment.ProcessId}},"dir":{{JsonSerializer.Serialize(dir)}},"wait":0}"""), TestContext.Current.CancellationToken);
        long returnedInMs = timer.ElapsedMilliseconds;
        Assert.Equal("connecting", pending.GetProperty("state").GetString());
        Assert.True(returnedInMs < 400, $"attach took {returnedInMs} ms against a host that steps every 900 ms");
        Assert.Empty(h.Shell.Sources);

        // The window keeps presenting frames while the host works toward its next step; each frame also pumps the shell as the realtime loop does.
        long startFrame = h.Game.Frame;
        int frames = 0;
        bool sawAttaching = false;
        while (h.Shell.Sources.Count == 0 && frames < 2000)
        {
            h.Game.Step(1);
            h.Shell.PumpLive();
            frames++;
            if (!sawAttaching)
            {
                JsonElement sources = h.Call("sources");
                JsonElement attaching = sources.GetProperty("attaching");
                if (attaching.GetArrayLength() == 1)
                {
                    sawAttaching = true;
                    Assert.Equal("connecting", attaching[0].GetProperty("state").GetString());
                    Assert.True(attaching[0].GetProperty("frame").GetInt64() >= attaching[0].GetProperty("startFrame").GetInt64());
                }
            }

            Thread.Sleep(4);
        }

        long framesWhileWaiting = h.Game.Frame - startFrame;
        Assert.True(sawAttaching);
        Assert.Single(h.Shell.Sources);
        Assert.True(framesWhileWaiting >= 20, $"only {framesWhileWaiting} frames while waiting for the snapshot");
        Assert.Empty(h.Call("sources").GetProperty("attaching").EnumerateArray());
        Harness.Report($"attach: returned in {returnedInMs} ms (host steps every 900 ms); {framesWhileWaiting} frames presented (frame {startFrame} to {h.Game.Frame}) before the source appeared");

        // Then streaming matches the host's hashes.
        SourceSession live = h.Shell.Sources[0];
        LiveAndSessionTests.WaitFor(() => { h.Shell.PumpLive(); return live.History.EndStep >= live.History.StartStep + 3; }, "records");
        host.Stop();
        h.Shell.PumpLive();
        for (long s = live.History.StartStep; s <= live.History.EndStep; s++)
        {
            h.Call("seek", $$"""{"step":{{s}}}""");
            Assert.Equal(host.Hashes[s], live.History.StateHash);
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task AttachWithAWaitStillReturnsTheSourceAndAFailedConnectionKeepsItsTypedError()
    {
        string dir = LiveAndSessionTests.NewDir();
        using var host = new MiniHost(dir);
        using Harness h = new();
        host.Start();
        LiveAndSessionTests.WaitFor(() => host.Steps >= 50, "the host to run");
        JsonElement attached = await Task.Run(() => h.Call("attach", $$"""{"pid":{{Environment.ProcessId}},"dir":{{JsonSerializer.Serialize(dir)}},"wait":10000}"""), TestContext.Current.CancellationToken);
        Assert.Equal("streaming", attached.GetProperty("live").GetProperty("state").GetString());

        // A failure that outlasts the wait is reported under attaching, and in the log.
        string missing = Path.Combine(dir, "missing.sock");
        JsonElement started = h.Call("attach", JsonSerializer.Serialize(new { socket = missing, wait = 0 }));
        Assert.Contains(started.GetProperty("state").GetString(), new[] { "connecting", "failed" });
        LiveAndSessionTests.WaitFor(() => { h.Shell.PumpLive(); return h.Shell.Attaches.Any(a => a.State == "failed"); }, "the failure");
        JsonElement failed = h.Call("sources").GetProperty("attaching")[0];
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Equal("not_found", failed.GetProperty("errorCode").GetString());
        Assert.Contains("missing.sock", failed.GetProperty("error").GetString());
        Assert.Contains(h.Shell.Log(), r => r.Kind == "error" && r.Text.Contains("missing.sock", StringComparison.Ordinal));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task AViewerThatReachesItsCapStopsWithAClearStateAndTheHostIsUnaffected()
    {
        // The same host run twice: once with no viewer, once with a viewer that hits its cap. Its hashes and its pace are the same.
        ulong[] baseline;
        string dir = LiveAndSessionTests.NewDir();
        using (var alone = new MiniHost(dir))
        {
            alone.Start();
            LiveAndSessionTests.WaitFor(() => alone.Steps >= 2500, "the unwatched host");
            alone.Stop();
            baseline = alone.Hashes[..(alone.Steps + 1)];
        }

        using var host = new MiniHost(dir);
        using Harness h = new();
        host.Start();
        LiveAndSessionTests.WaitFor(() => host.Steps >= 100, "the host to run");
        const long Cap = 8192;
        JsonElement attached = await Task.Run(() => h.Call("attach", $$"""{"pid":{{Environment.ProcessId}},"dir":{{JsonSerializer.Serialize(dir)}},"wait":10000,"capBytes":{{Cap}}}"""), TestContext.Current.CancellationToken);
        Assert.Equal(Cap, attached.GetProperty("live").GetProperty("capBytes").GetInt64());
        SourceSession live = h.Shell.Sources[0];
        LiveAndSessionTests.WaitFor(() => { h.Shell.PumpLive(); return live.LiveCapped; }, "the cap");

        JsonElement state = h.Call("sources").GetProperty("sources")[0].GetProperty("live");
        Assert.Equal("capped", state.GetProperty("state").GetString());
        Assert.Contains("live cap", state.GetProperty("endedBecause").GetString());
        Assert.InRange(state.GetProperty("recordBytes").GetInt64(), 1, Cap);
        Assert.Contains(h.Shell.Log(), r => r.Kind == "error" && r.Text.Contains("live cap", StringComparison.Ordinal));

        // What was received stays usable: seek and fork inside it.
        long end = live.History.EndStep;
        h.Call("seek", $$"""{"step":{{end}}}""");
        h.Call("fork", $$"""{"step":{{end}},"view":false}""");

        // The host kept stepping after the viewer left, and its hashes are the unwatched run's.
        int atCap = host.Steps;
        LiveAndSessionTests.WaitFor(() => host.Steps >= Math.Min(2500, atCap + 400), "the host to carry on");
        host.Stop();
        int common = Math.Min(baseline.Length, host.Steps + 1);
        Assert.True(common > atCap);
        Assert.Equal(baseline[..common], host.Hashes[..common]);
        Harness.Report($"live cap {Cap} bytes: capped at viewer step {end}; host carried on from step {atCap} to {host.Steps}; {common} hashes equal the unwatched run's");
        Directory.Delete(dir, true);
    }
}
