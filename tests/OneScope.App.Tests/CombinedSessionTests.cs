using System.Text.Json;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// A physics world and a navigation crowd of the same scenario, open together on one time axis, saved as one session and reopened.
/// The inputs (testdata/combo-*) come from tools/GenPathData: one arena, ten discs, sixty steps at sixty a second.
/// </summary>
[Collection("shell")]
public sealed class CombinedSessionTests
{
    private static string Q(string s) => JsonSerializer.Serialize(s);

    private static string[] Hashes(Harness h, long step)
    {
        h.Call("seek", $$"""{"step":{{step}}}""");
        return [.. h.Call("sources").GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("hash").GetString()!)];
    }

    private static Harness OpenBoth(int width = 960, int height = 540)
    {
        var h = new Harness(width, height, bundled: true);
        h.Call("open", $$"""{"path":{{Q(Harness.Data("combo-physics.oppr"))}},"keyframeInterval":16}""");
        h.Call("open", $$"""{"path":{{Q(Harness.Data("combo-crowd.oprp"))}},"keyframeInterval":16}""");
        return h;
    }

    [Fact]
    public void BothSourcesLandOnTheirOwnLibraryHashesAtTheSameStepAndASavedSessionReopensToTheSameHashesAndFrame()
    {
        string[] physics = File.ReadAllLines(Harness.Data("combo-physics.hashes"));
        string[] crowd = File.ReadAllLines(Harness.Data("combo-crowd.hashes"));
        using Harness h = OpenBoth();
        JsonElement sources = h.Call("sources");
        Assert.Equal(["onephysics", "onepath"], sources.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("module").GetString()));
        Assert.Equal(60, sources.GetProperty("steps").GetInt64());

        long[] walk = [0, 1, 17, 33, 60, 47, 16, 15, 2, 0, 60, 31];
        foreach (long step in walk)
        {
            string[] got = Hashes(h, step);
            Assert.Equal(physics[step], got[0]);
            Assert.Equal(crowd[step], got[1]);
        }

        // A fork of the crowd with a poke rides along in the session.
        h.Call("seek", """{"step":20}""");
        int branch = h.Call("fork", """{"source":1}""").GetProperty("branch").GetInt32();
        ulong agent = h.Call("entities", """{"source":1,"kind":"agent"}""").GetProperty("entities").EnumerateArray().First().GetProperty("id").GetUInt64();
        h.Call("poke", $$"""{"source":{{branch}},"poke":"set_target","args":{"agent":{{agent}},"target":[3,3,0]},"step":true}""");
        h.Call("step", $$"""{"source":{{branch}},"delta":10}""");

        h.Call("seek", """{"step":33}""");
        string frame = h.Capture("combined.png", """{"path":"combined.png","size":"960x540"}""");
        string[] before = [.. Hashes(h, 33), .. Hashes(h, 25), .. Hashes(h, 33)];
        string saved = h.Call("session_save", """{"path":"combined.oscope"}""").GetProperty("path").GetString()!;

        using Harness again = new Harness(960, 540, bundled: true);
        JsonElement opened = again.Call("session_open", $$"""{"path":{{Q(saved)}}}""");
        Assert.Equal(3, opened.GetProperty("sources").GetArrayLength());
        Assert.Equal(33, opened.GetProperty("step").GetInt64());
        string[] after = [.. Hashes(again, 33), .. Hashes(again, 25), .. Hashes(again, 33)];
        Assert.Equal(before, after);
        Assert.Equal(physics[33], after[0]);
        Assert.Equal(crowd[33], after[1]);
        string frameAgain = again.Capture("combined.png", """{"path":"combined.png","size":"960x540"}""");
        Assert.Equal(frame, frameAgain);
        Harness.Report($"combined session: 12 landings of both sources equal their library hashes; saved with a crowd fork and reopened fresh: {before.Length} hashes equal; shell capture at step 33 sha256 {frame[..16]} before and after");
    }

    [Fact]
    public void TheCombinedShellCaptureIsByteIdenticalAcrossThreeRunsAndMatchesTheGolden()
    {
        var runs = new List<(string Sha, string Renderer, string File)>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                using Harness h = OpenBoth(1280, 720);
                h.Call("seek", """{"step":33}""");
                // Two 2D views side by side: the physics world and the crowd, each framed on the arena.
                h.Call("view", """{"index":1,"remove":true}""");
                h.Call("view", """{"index":0,"target":[20,12,0],"halfHeight":14}""");
                h.Call("view", """{"index":1,"target":[20,12,0],"halfHeight":14}""");
                JsonElement r = h.Call("capture_frame", """{"path":"combined.png","size":"1280x720"}""");
                string copy = Path.Combine(Path.GetTempPath(), "onescope-golden-" + Guid.NewGuid().ToString("N") + ".png");
                File.Copy(r.GetProperty("path").GetString()!, copy);
                runs.Add((r.GetProperty("sha256").GetString()!, r.GetProperty("renderer").GetString()!, copy));
            }

            GoldenTests.Check("combined-shell", runs);
        }
        finally
        {
            foreach (var r in runs)
            {
                File.Delete(r.File);
            }
        }
    }
}
