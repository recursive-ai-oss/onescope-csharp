using System.Text.Json;
using OneGame.Control;
using OneScope.Sdk;
using Xunit;

namespace OneScope.App.Tests;

[Collection("shell")]
public sealed class ShellTests
{
    private static readonly Lazy<ulong[]> Straight = new(() => Harness.StraightHashes("test-10000.ostm"));

    private static string Hex(ulong h) => h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void SeekThroughTheShellGivesTheStraightPlaybackHash()
    {
        using Harness h = new Harness().Open();
        // Forward over several keyframes, backward across them, and back to the start: every landing is the straight-playback hash.
        long[] path = [0, 1, 63, 64, 65, 500, 1000, 4096, 4097, 3000, 640, 63, 9999, 10000, 5000, 0, 7777];
        foreach (long step in path)
        {
            JsonElement r = h.Call("seek", $$"""{"step":{{step}}}""");
            Assert.Equal(step, r.GetProperty("step").GetInt64());
            Assert.Equal(Hex(Straight.Value[step]), r.GetProperty("sources")[0].GetProperty("hash").GetString());
        }

        Harness.Report($"seek-determinism: {path.Length} landings equal straight playback (keyframe interval 64, 10000 steps)");
    }

    [Fact]
    public void StepMovesByDeltaAndClampsAtTheEnds()
    {
        using Harness h = new Harness().Open();
        h.Call("seek", """{"step":100}""");
        Assert.Equal(101, h.Call("step").GetProperty("step").GetInt64());
        Assert.Equal(91, h.Call("step", """{"delta":-10}""").GetProperty("step").GetInt64());
        Assert.Equal(0, h.Call("step", """{"delta":-1000}""").GetProperty("step").GetInt64());
        Assert.Equal(10000, h.Call("step", """{"delta":99999}""").GetProperty("step").GetInt64());
    }

    [Fact]
    public void PlaySpeedIsPresentationOnlyAndLandsOnTheSameHash()
    {
        using Harness h = new Harness().Open();
        JsonElement r = h.Call("play", """{"speed":120,"frames":30}""");
        // 120 steps per second at 60 presentation frames per second is two steps a frame.
        Assert.Equal(60, r.GetProperty("step").GetInt64());
        Assert.Equal(Hex(Straight.Value[60]), r.GetProperty("hashes")[0].GetString());
        r = h.Call("play", """{"speed":-30,"frames":20}""");
        Assert.Equal(50, r.GetProperty("step").GetInt64());
        Assert.Equal(Hex(Straight.Value[50]), r.GetProperty("hashes")[0].GetString());
        // Playing to the end stops at the end.
        r = h.Call("play", """{"speed":6000,"frames":1000}""");
        Assert.Equal(10000, r.GetProperty("step").GetInt64());
        Assert.False(r.GetProperty("playing").GetBoolean());
    }

    [Fact]
    public void OpenSourcesEntitiesInspectAxesFindingsDescribeTheWorld()
    {
        using Harness h = new Harness().Open();
        JsonElement sources = h.Call("sources");
        JsonElement s0 = sources.GetProperty("sources")[0];
        Assert.Equal("test-10000.ostm", s0.GetProperty("name").GetString());
        Assert.Equal(3, s0.GetProperty("dimensions").GetInt32());
        Assert.Equal("mm", s0.GetProperty("unit").GetProperty("name").GetString());
        Assert.Equal(10000, s0.GetProperty("steps").GetInt64());

        h.Call("seek", """{"step":300}""");
        JsonElement ents = h.Call("entities", """{"kind":"body"}""");
        Assert.True(ents.GetProperty("count").GetInt32() > 0);
        Assert.All(ents.GetProperty("entities").EnumerateArray(), e => Assert.Equal("body", e.GetProperty("kind").GetString()));

        long id = ents.GetProperty("entities")[0].GetProperty("id").GetInt64();
        JsonElement inspect = h.Call("inspect", $$"""{"entity":{{id}}}""");
        Assert.Equal(id, inspect.GetProperty("entity").GetInt64());
        Assert.Contains(inspect.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "position" && p.GetProperty("kind").GetString() == "vector");
        Assert.Equal((ulong)id, h.Shell.Selected.Value);

        JsonElement axes = h.Call("axes");
        Assert.Equal(["time", "stage"], axes.GetProperty("axes").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray());

        JsonElement findings = h.Call("findings");
        Assert.Equal(10000, findings.GetProperty("scannedThrough").GetInt64());
        Assert.True(findings.GetProperty("count").GetInt32() >= 0);
    }

    [Fact]
    public void StageAxisSeekLeavesTheTimeCursorAndStateHashAlone()
    {
        using Harness h = new Harness().Open();
        h.Call("seek", """{"step":200}""");
        string before = h.Call("sources").GetProperty("sources")[0].GetProperty("hash").GetString()!;
        JsonElement r = h.Call("seek", """{"axis":"stage","position":2}""");
        JsonElement src = r.GetProperty("sources")[0];
        Assert.Equal(200, src.GetProperty("step").GetInt64());
        Assert.Equal(before, src.GetProperty("hash").GetString());
        Assert.Equal("stage", src.GetProperty("viewAxis").GetString());
        Assert.Equal(2, src.GetProperty("viewPosition").GetInt32());
        // The same (step, position) gives the same view hash by any path.
        string view = src.GetProperty("viewHash").GetString()!;
        h.Call("seek", """{"step":50}""");
        h.Call("seek", """{"step":200}""");
        r = h.Call("seek", """{"axis":"stage","position":2}""");
        Assert.Equal(view, r.GetProperty("sources")[0].GetProperty("viewHash").GetString());
        Assert.Equal(2, h.Call("axes").GetProperty("axes")[1].GetProperty("position").GetInt32());
    }

    [Fact]
    public void OverlayTogglesPerViewAndByName()
    {
        using Harness h = new Harness().Open();
        JsonElement r = h.Call("overlay", """{"overlay":"velocities","on":true}""");
        Assert.Contains(r.GetProperty("overlays").EnumerateArray(), o => o.GetProperty("name").GetString() == "Velocities" && o.GetProperty("on").GetBoolean());
        r = h.Call("overlay", """{"view":1,"overlay":"1"}""");
        Assert.Contains(r.GetProperty("overlays").EnumerateArray(), o => o.GetProperty("id").GetInt32() == 1 && o.GetProperty("on").GetBoolean());
        r = h.Call("overlay", """{"view":1,"overlay":"Velocities"}""");
        Assert.Contains(r.GetProperty("overlays").EnumerateArray(), o => o.GetProperty("id").GetInt32() == 1 && !o.GetProperty("on").GetBoolean());
        r = h.Call("overlay", """{"wireframe":true,"grid":false}""");
        Assert.True(r.GetProperty("wireframe").GetBoolean());
        Assert.False(r.GetProperty("grid").GetBoolean());
        ControlException ex = Assert.Throws<ControlException>(() => h.Call("overlay", """{"overlay":"nope"}"""));
        Assert.Equal("not_found", ex.Code);
        Assert.Contains("Velocities", ex.Message);
    }

    [Fact]
    public void ErrorsAreTypedAndNameTheInput()
    {
        using Harness h = new();
        Assert.Equal("no_source", Assert.Throws<ControlException>(() => h.Call("seek", """{"step":1}""")).Code);
        Assert.Equal("no_source", Assert.Throws<ControlException>(() => h.Call("overlay")).Code);

        ControlException missing = Assert.Throws<ControlException>(() => h.Call("open", """{"path":"/no/such/file.ostm"}"""));
        Assert.Equal("not_found", missing.Code);
        Assert.Contains("/no/such/file.ostm", missing.Message);
        Assert.Contains("expected", missing.Message);

        h.Open();
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("seek", """{"step":99999}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("seek", """{"step":"x"}""")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("seek", "[1]")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("seek", "{oops")).Code);
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => h.Call("inspect", """{"entity":424242}""")).Code);
        Assert.Equal("not_found", Assert.Throws<ControlException>(() => h.Call("entities", """{"source":3}""")).Code);
        Assert.Equal("unknown_op", Assert.Throws<ControlException>(() => h.Call("explode")).Code);
        Assert.Equal("bad_args", Assert.Throws<ControlException>(() => h.Call("capture_sequence", """{"dir":"x","count":0}""")).Code);
        Assert.Equal("path_outside", Assert.Throws<ControlException>(() => h.Call("capture_sequence", """{"dir":"../out","count":1}""")).Code);
    }

    [Theory]
    [InlineData("empty", new byte[0])]
    [InlineData("garbage", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    [InlineData("truncated", new byte[] { (byte)'O', (byte)'S', (byte)'T', (byte)'M', 1, 0 })]
    [InlineData("wrong-version", new byte[] { (byte)'O', (byte)'S', (byte)'T', (byte)'M', 9, 9, 0, 0, 6, 0, 0, 0, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void MalformedFilesAreATypedErrorNotACrash(string name, byte[] bytes)
    {
        using Harness h = new();
        string file = Path.Combine(h.Output, name + ".ostm");
        File.WriteAllBytes(file, bytes);
        ControlException ex = Assert.Throws<ControlException>(() => h.Call("open", System.Text.Json.JsonSerializer.Serialize(new { path = file })));
        Assert.Equal("bad_format", ex.Code);
        Assert.Contains(name + ".ostm", ex.Message);
        Assert.Contains("oscope.test", ex.Message);
        Assert.Empty(h.Shell.Sources);
        // The shell is still usable afterwards.
        h.Open("test-2d-600.ostm");
        Assert.Single(h.Shell.Sources);
    }

    [Fact]
    public void NoModuleLoadedSaysSoAndHowToLoadOne()
    {
        using var game = new Harness();
        var shell = new ShellModel();
        ControlException ex = Assert.Throws<ControlException>(() => shell.Open(Harness.Data("test-2d-600.ostm"), null, 0, null));
        Assert.Equal("no_module", ex.Code);
        Assert.Contains("--module", ex.Message);
    }

    [Fact]
    public void SeveralSourcesShareOneTimeAxis()
    {
        using Harness h = new Harness().Open("test-10000.ostm").Open("test-2d-600.ostm");
        JsonElement r = h.Call("seek", """{"step":300}""");
        Assert.Equal(2, r.GetProperty("sources").GetArrayLength());
        Assert.Equal(300, r.GetProperty("sources")[1].GetProperty("step").GetInt64());
        r = h.Call("seek", """{"step":9000}""");
        Assert.Equal(600, r.GetProperty("sources")[1].GetProperty("step").GetInt64());
        Assert.Equal(9000, r.GetProperty("sources")[0].GetProperty("step").GetInt64());
        Assert.Equal(Hex(Straight.Value[9000]), r.GetProperty("sources")[0].GetProperty("hash").GetString());
    }

    [Fact]
    public void CaptureSequenceStepsTheCursorAndRecordsExactHashes()
    {
        using Harness h = new Harness(480, 270).Open();
        h.Call("seek", """{"step":100}""");
        JsonElement r = h.Call("capture_sequence", """{"dir":"seq","count":4,"stride":50}""");
        Assert.Equal(4, r.GetProperty("frames").GetInt32());
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(r.GetProperty("manifest").GetString()!));
        JsonElement[] frames = manifest.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        for (int i = 0; i < frames.Length; i++)
        {
            long step = 100 + (50L * i);
            Assert.Equal(step, frames[i].GetProperty("step").GetInt64());
            Assert.Equal(Hex(Straight.Value[step]), frames[i].GetProperty("hash").GetString());
            Assert.True(File.Exists(Path.Combine(h.Output, "seq", frames[i].GetProperty("file").GetString()!)));
        }

        Assert.Equal(frames.Select(f => f.GetProperty("sha256").GetString()).Distinct().Count(), frames.Length);
    }

    [Fact]
    public void ViewsAreConfiguredThroughTheViewOperation()
    {
        using Harness h = new Harness().Open();
        JsonElement v = h.Call("view");
        Assert.Equal(["3d", "2d"], v.GetProperty("views").EnumerateArray().Select(x => x.GetProperty("kind").GetString()!).ToArray());
        v = h.Call("view", """{"add":"2d"}""");
        Assert.Equal(3, v.GetProperty("views").GetArrayLength());
        v = h.Call("view", """{"index":0,"yaw":10,"pitch":50,"distance":1234,"active":true}""");
        Assert.Equal(1234, v.GetProperty("views")[0].GetProperty("distance").GetDouble());
        v = h.Call("view", """{"index":2,"remove":true}""");
        Assert.Equal(2, v.GetProperty("views").GetArrayLength());
        Assert.Equal("rejected", Assert.Throws<ControlException>(() => { h.Call("view", """{"index":1,"remove":true}"""); h.Call("view", """{"index":0,"remove":true}"""); }).Code);
    }
}
