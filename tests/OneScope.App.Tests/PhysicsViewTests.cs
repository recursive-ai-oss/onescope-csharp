using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace OneScope.App.Tests;

/// <summary>
/// The bundled OnePhysics module end to end through the operations: recorded hashes, picking, dragging a fork, and diff naming.
/// </summary>
[Collection("shell")]
public sealed class PhysicsViewTests
{
    private const long First = 1;

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    private static string P(string action, float x, float y) => JsonSerializer.Serialize(new { action, x, y });

    /// <summary>A copy of a version 2 replay whose recorded hash after the step that reaches <paramref name="stepCount"/> has one bit flipped.</summary>
    private static byte[] Tamper(byte[] replay, long stepCount)
    {
        byte[] copy = (byte[])replay.Clone();
        int offset = 16;
        while (offset + 5 <= copy.Length)
        {
            byte kind = copy[offset];
            int length = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(offset + 1, 4));
            if (kind == 5 && length == 16 && BinaryPrimitives.ReadInt64LittleEndian(copy.AsSpan(offset + 5, 8)) == stepCount)
            {
                copy[offset + 5 + 8] ^= 0x01;
                return copy;
            }

            if (kind == 0)
            {
                break;
            }

            offset += 5 + length;
        }

        throw new InvalidOperationException($"no EndStep record for step {stepCount}");
    }

    [Theory]
    [InlineData("physics-2d", 2)]
    [InlineData("physics-3d", 3)]
    public void TamperedRecordedHashesRaiseAFindingAtTheRightStepAndTheUntamperedFileRaisesNone(string name, int dimension)
    {
        byte[] original = File.ReadAllBytes(Harness.Data(name + ".oppr"));
        string tampered = Path.Combine(Path.GetTempPath(), "onescope-tamper-" + Guid.NewGuid().ToString("N") + ".oppr");
        try
        {
            const long Bad = 30;
            File.WriteAllBytes(tampered, Tamper(original, Bad));

            using Harness clean = new Harness(bundled: true);
            clean.Call("open", $$"""{"path":{{Quote(Harness.Data(name + ".oppr"))}},"scan":true}""");
            JsonElement none = clean.Call("findings", """{"source":0}""");
            Assert.Equal(0, none.GetProperty("count").GetInt32());
            JsonElement cleanInfo = clean.Call("sources").GetProperty("sources")[0].GetProperty("recordedHashes");
            Assert.Equal("all", cleanInfo.GetProperty("matches").GetString());

            using Harness h = new Harness(bundled: true);
            JsonElement open = h.Call("open", $$"""{"path":{{Quote(tampered)}},"scan":true}""");
            Assert.Equal(dimension, open.GetProperty("dimensions").GetInt32());
            JsonElement findings = h.Call("findings", """{"source":0}""");
            Assert.Equal(1, findings.GetProperty("count").GetInt32());
            JsonElement f = findings.GetProperty("findings")[0];
            Assert.Equal("replay.hash-mismatch", f.GetProperty("code").GetString());
            Assert.Equal(Bad, f.GetProperty("step").GetInt64());
            JsonElement info = h.Call("sources").GetProperty("sources")[0].GetProperty("recordedHashes");
            Assert.Equal("no", info.GetProperty("matches").GetString());
            Assert.Equal(Bad, info.GetProperty("firstMismatch").GetInt64());
            Harness.Report($"{name}: tampered hash at step {Bad} -> {f}; untampered: count 0, matches all");
        }
        finally
        {
            File.Delete(tampered);
        }
    }

    /// <summary>The window pixel where <paramref name="entity"/> is, and whether a pick there returns it.</summary>
    private static (float X, float Y)? Locate(Harness h, int view, long entity)
    {
        JsonElement p = h.Call("project", $$"""{"entity":{{entity}},"view":{{view}}}""");
        if (!p.GetProperty("onScreen").GetBoolean())
        {
            return null;
        }

        float x = p.GetProperty("x").GetSingle();
        float y = p.GetProperty("y").GetSingle();
        JsonElement hit = h.Call("pick", $$"""{"x":{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"y":{{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"view":{{view}}}""");
        return hit.GetProperty("entity").ValueKind == JsonValueKind.Number && hit.GetProperty("entity").GetInt64() == entity ? (x, y) : null;
    }

    private static List<long> Bodies(Harness h)
    {
        var ids = new List<long>();
        foreach (JsonElement e in h.Call("entities").GetProperty("entities").EnumerateArray())
        {
            if (e.GetProperty("kind").GetString() == "body")
            {
                ids.Add(e.GetProperty("id").GetInt64());
            }
        }

        return ids;
    }

    private static string HashAt(Harness h, long step, int source)
    {
        h.Call("seek", $$"""{"step":{{step}}}""");
        return h.Call("sources").GetProperty("sources")[source].GetProperty("hash").GetString()!;
    }

    [Theory]
    [InlineData("physics-2d")]
    [InlineData("physics-3d")]
    public void PickAndProjectFindBodiesAndAPointerDragOnAForkIsReplayedExactly(string name)
    {
        string file = Harness.Data(name + ".oppr");
        string shaBefore = Harness.Sha(File.ReadAllBytes(file));
        string[] recorded = File.ReadAllLines(Harness.Data(name + ".hashes"));
        using Harness h = new Harness(960, 540, bundled: true);
        h.Call("open", $$"""{"path":{{Quote(file)}},"keyframeInterval":16}""");
        h.Call("seek", """{"step":21}""");
        h.Call("capture_frame", """{"path":"layout.png"}""");

        // Every view of the original can find at least one body by projecting it and picking where it lands.
        for (int view = 0; view < h.Shell.Views.Count; view++)
        {
            int found = Bodies(h).Count(b => Locate(h, view, b) is not null);
            Assert.True(found >= (view == 0 ? 3 : 1), $"view {view}: only {found} bodies pickable");
            Harness.Report($"{name} view {view} ({h.Shell.Views[view].Kind}): {found} of {Bodies(h).Count} bodies found by project then pick");
        }

        // Fork, then drag a body in the fork's own view (the last one).
        JsonElement fork = h.Call("fork");
        int branch = fork.GetProperty("branch").GetInt32();
        int viewIndex = h.Shell.Views.Count - 1;
        Assert.Equal(branch, h.Shell.Views[viewIndex].Source);
        h.Call("capture_frame", """{"path":"layout2.png"}""");
        (float X, float Y, long Body)? grab = null;
        foreach (long b in Bodies(h))
        {
            if (Locate(h, viewIndex, b) is (float gx, float gy))
            {
                // Skip the floor: a dynamic body has a fork poke that moves it.
                JsonElement inspected = h.Call("inspect", $$"""{"entity":{{b}},"source":{{branch}}}""");
                if (!inspected.ToString().Contains("Static", StringComparison.OrdinalIgnoreCase))
                {
                    grab = (gx, gy, b);
                    break;
                }
            }
        }

        Assert.NotNull(grab);
        (float x, float y, long body) = grab.Value;
        const int Moves = 6;
        h.Call("input_pointer", P("down", x, y));
        for (int i = 1; i <= Moves; i++)
        {
            h.Call("input_pointer", P("move", x + (3 * i), y - (2 * i)));
        }

        h.Call("input_pointer", P("up", x + (3 * Moves), y - (2 * Moves)));
        JsonElement info = h.Call("sources").GetProperty("sources")[branch];
        Assert.Equal(21 + Moves, info.GetProperty("steps").GetInt64());
        Assert.Equal(Moves, info.GetProperty("fork").GetProperty("pokedSteps").GetInt64());
        Assert.Equal(21, info.GetProperty("fork").GetProperty("divergence").GetInt64());

        // The saved fork reopens to the fork's hash at every step; the original is byte for byte what it was.
        var forkHashes = new List<string>();
        for (long s = First; s <= 21 + Moves; s++)
        {
            forkHashes.Add(HashAt(h, s, branch));
        }

        string path = h.Call("replay_save", $$"""{"source":{{branch}},"path":"drag.oppr"}""").GetProperty("path").GetString()!;
        int again = h.Call("open", $$"""{"path":{{Quote(path)}},"keyframeInterval":16}""").GetProperty("source").GetInt32();
        for (long s = First; s <= 21 + Moves; s++)
        {
            Assert.Equal(forkHashes[(int)(s - First)], HashAt(h, s, again));
        }

        Assert.Equal(shaBefore, Harness.Sha(File.ReadAllBytes(file)));
        for (long s = First; s <= 21; s++)
        {
            Assert.Equal(recorded[s - First], HashAt(h, s, 0));
        }

        Assert.NotEqual(recorded[22 - First], forkHashes[(int)(22 - First)]);
        Harness.Report($"{name}: dragged body {body} with {Moves} moves from fork step 21; saved replay reproduces {forkHashes.Count} hashes; original sha256 {shaBefore[..16]} unchanged");
    }

    [Theory]
    [InlineData("physics-2d")]
    [InlineData("physics-3d")]
    public void AForkDiffNamesABodyAndItsFieldFirst(string name)
    {
        using Harness h = new Harness(bundled: true);
        h.Call("open", $$"""{"path":{{Quote(Harness.Data(name + ".oppr"))}}}""");
        h.Call("seek", """{"step":21}""");
        long box = Bodies(h).First(b => h.Call("inspect", $$"""{"entity":{{b}}}""").ToString().Contains("Body 1 ", StringComparison.Ordinal));
        h.Call("fork");
        h.Call("poke", $$"""{"source":1,"poke":"impulse","args":{"target":{{box}},"impulse":[0,6,0]},"step":true}""");
        JsonElement diff = h.Call("diff", """{"source":1,"step":22}""");
        JsonElement d = diff.GetProperty("difference");
        Assert.Equal(box, d.GetProperty("entity").GetInt64());
        Assert.False(string.IsNullOrEmpty(d.GetProperty("field").GetString()));
        Harness.Report($"{name} diff JSON: {d}");
    }

    [Fact]
    public void ProjectBeforeTheFirstFrameIsATypedErrorNotAnInfiniteNumber()
    {
        using Harness h = new Harness(bundled: true);
        h.Call("open", $$"""{"path":{{Quote(Harness.Data("physics-3d.oppr"))}}}""");
        long body = Bodies(h)[1];
        Assert.Equal("rejected", Assert.Throws<OneGame.Control.ControlException>(() => h.Call("project", $$"""{"entity":{{body}},"view":0}""")).Code);
        h.Call("capture_frame", """{"path":"layout.png"}""");
        Assert.True(h.Call("project", $$"""{"entity":{{body}},"view":0}""").GetProperty("x").GetDouble() > 0);
    }
}
