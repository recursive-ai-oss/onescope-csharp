using System.Buffers;
using System.Numerics;
using OneScope.Attach;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Attach.Tests;

public class SessionTests
{
    private static byte[] Write(SessionFile file)
    {
        var w = new ArrayBufferWriter<byte>();
        file.Write(w);
        return w.WrittenSpan.ToArray();
    }

    private static (byte[] Session, ulong[] ForkHashes, long Divergence) MakeSessionWithFork()
    {
        TestSource source = Support.OpenLong();
        var history = new History(source, new HistoryOptions { KeyframeInterval = 32 });
        history.Seek(1000);
        Branch branch = history.Fork(1000);
        var forkHashes = new List<ulong>();
        for (int i = 0; i < 300; i++)
        {
            if (i % 50 == 0)
            {
                branch.Poke(new PokeCall("impulse", [ScopeValue.OfEntity(new EntityId(TestModule.TestModule.FirstBodyId + (ulong)(i / 50))), ScopeValue.OfVector(new Vector3(300, 2500, -100))]));
            }

            branch.Step();
        }

        for (long k = 1000; k <= 1300; k += 10)
        {
            branch.History.Seek(k);
            forkHashes.Add(branch.History.StateHash);
        }

        var file = new SessionFile { CursorStep = 1200, Layout = new byte[] { 1, 2, 3, 4, 5 } };
        file.Sources.Add(SessionSource.Embed(source, 32));
        file.Sources.Add(SessionSource.EmbedFork(branch.History.Source, 0, 1000, 16));
        return (Write(file), [.. forkHashes], branch.DivergenceStep!.Value);
    }

    [Fact]
    public void SaveLoadSaveIsByteExact()
    {
        (byte[] bytes, _, _) = MakeSessionWithFork();
        SessionFile loaded = SessionFile.Parse(bytes, "a.oscope");
        Assert.Equal(bytes, Write(loaded));
        Assert.Equal(2, loaded.Sources.Count);
        Assert.Equal(1200, loaded.CursorStep);
        Assert.Equal(0, loaded.Sources[1].ParentIndex);
        Assert.Equal(1000, loaded.Sources[1].ForkStep);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, loaded.Layout.ToArray());

        // Through the modules as well: reopen, capture again, same bytes.
        RestoredSession restored = SessionRestore.Open(loaded, [Support.Module], _ => null);
        var again = new SessionFile { CursorStep = restored.CursorStep, Layout = restored.Layout };
        for (int i = 0; i < restored.Sources.Count; i++)
        {
            RestoredSource r = restored.Sources[i];
            again.Sources.Add(r.ParentIndex < 0 ? SessionSource.Embed(r.Source, 32) : SessionSource.EmbedFork(r.Source, r.ParentIndex, r.ForkStep, 16));
        }

        Assert.Equal(bytes, Write(again));
    }

    [Fact]
    public void ForkAndItsPokeReplayReopenToTheSameHashes()
    {
        (byte[] bytes, ulong[] forkHashes, long divergence) = MakeSessionWithFork();
        string path = Path.Combine(Support.NewSocketDir(), "fork.oscope");
        File.WriteAllBytes(path, bytes);
        RestoredSession restored = SessionRestore.Open(SessionFile.Load(path), [Support.Module], _ => null);
        RestoredSource fork = restored.Sources[1];
        Assert.Equal(0, fork.ParentIndex);
        Assert.Equal(1000, fork.ForkStep);
        Assert.Equal(divergence, fork.DivergenceStep);
        for (int i = 0; i < forkHashes.Length; i++)
        {
            fork.History.Seek(1000 + i * 10);
            Assert.Equal(forkHashes[i], fork.History.StateHash);
        }

        Assert.Equal(1200, restored.Sources[0].History.CurrentStep);

        // Before the divergence the fork's world is the original's.
        fork.History.Seek(divergence);
        restored.Sources[0].History.Seek(divergence);
        Assert.Equal(restored.Sources[0].History.StateHash, fork.History.StateHash);
        Support.Report("session with a fork reopened: fork hash at steps 1000..1300 step 10, saved vs reopened");
        for (int i = 0; i < forkHashes.Length; i += 5)
        {
            fork.History.Seek(1000 + i * 10);
            Support.Report($"  step {1000 + i * 10}  saved {forkHashes[i]:x16}  reopened {fork.History.StateHash:x16}");
        }
    }

    [Fact]
    public void ReferencedFilesAreCheckedAgainstTheirHash()
    {
        byte[] file = Support.LongBytes;
        var session = new SessionFile();
        session.Sources.Add(SessionSource.Reference("oscope.test", "test-10000.ostm", "data/test-10000.ostm", file));
        byte[] bytes = Write(session);
        SessionFile loaded = SessionFile.Parse(bytes, "r.oscope");
        Assert.Equal(bytes, Write(loaded));
        RestoredSession ok = SessionRestore.Open(loaded, [Support.Module], p => p == "data/test-10000.ostm" ? file : null);
        Assert.Equal(10_000, ok.Sources[0].History.StepCount);

        byte[] changed = (byte[])file.Clone();
        changed[^1] ^= 1;
        Assert.Contains("SHA-256", Assert.Throws<ScopeFormatException>(() => SessionRestore.Open(loaded, [Support.Module], _ => changed)).Message);
        Assert.Contains("to exist", Assert.Throws<ScopeFormatException>(() => SessionRestore.Open(loaded, [Support.Module], _ => null)).Message);
        Assert.Contains("module with id", Assert.Throws<ScopeFormatException>(() => SessionRestore.Open(loaded, [], _ => file)).Message);
    }

    [Fact]
    public void ReaderRejectsEveryTruncationAndNamesWhatWasExpected()
    {
        (byte[] bytes, _, _) = MakeSessionWithFork();
        for (int n = 0; n < bytes.Length; n += n < 400 ? 1 : 997)
        {
            ScopeFormatException e = Assert.Throws<ScopeFormatException>(() => SessionFile.Parse(bytes.AsSpan(0, n).ToArray(), "cut.oscope"));
            Assert.StartsWith("cut.oscope: expected", e.Message);
        }

        Assert.Throws<ScopeFormatException>(() => SessionFile.Parse(bytes.Append((byte)0).ToArray(), "trail.oscope"));
    }

    [Fact]
    public void ReaderRejectsBadMagicVersionHugeCountsAndInconsistentSources()
    {
        (byte[] good, _, _) = MakeSessionWithFork();
        byte[] Mutate(int offset, params byte[] value)
        {
            byte[] c = (byte[])good.Clone();
            value.CopyTo(c, offset);
            return c;
        }

        void Fails(byte[] b, string contains) =>
            Assert.Contains(contains, Assert.Throws<ScopeFormatException>(() => SessionFile.Parse(b, "bad.oscope")).Message);

        Fails(Mutate(0, (byte)'Z'), "magic");
        Fails(Mutate(4, 9), "version 1");
        Fails(Mutate(6, 1), "flags");
        Fails(Mutate(15, 0x80), "cursor");
        Fails(Mutate(16, 0xFF, 0xFF, 0xFF, 0xFF), "at most 4096 sources");
        Fails(Mutate(16, 0xFF, 0x0F, 0, 0), "sources of at least");
        Fails(Mutate(20, 0xFF, 0xFF, 0xFF, 0x7F), "layout bytes");
        Fails(Mutate(24, 7), "kind");
        Fails(Mutate(24 + 5, 5), "fork step");

        // Source 1 is at an offset past source 0's strings and payload; find it by parsing source 0's lengths.
        SessionFile parsed = SessionFile.Parse(good, "good");
        int off1 = 24 + 27 + "oscope.test".Length + "test-10000.ostm".Length + parsed.Sources[0].Replay.Length;
        Fails(Mutate(off1 + 1, 1, 0, 0, 0), "parent");
        Fails(Mutate(off1 + 1, 0xFE, 0xFF, 0xFF, 0xFF), "parent");
        Fails(Mutate(off1 + 13, 0, 0, 0, 0), "keyframe interval");
        Fails(Mutate(off1 + 23, 0xFF, 0xFF, 0xFF, 0xFF), "bytes of strings and payload");
        Fails(Mutate(off1, 1), "to be embedded");

        var forkOfNothing = new SessionFile();
        forkOfNothing.Sources.Add(SessionSource.EmbedFork(Support.OpenLong(), 0, 0));
        Assert.Throws<ScopeFormatException>(() => Write(forkOfNothing));
    }

    [Fact]
    public void LimitsAreEnforcedBeforeAllocating()
    {
        (byte[] good, _, _) = MakeSessionWithFork();
        var tight = new SessionLimits(good.Length - 1, 4096, 1024);
        Assert.Contains("at most", Assert.Throws<ScopeFormatException>(() => SessionFile.Parse(good, "big.oscope", tight)).Message);
        Assert.Contains("at most 1 sources", Assert.Throws<ScopeFormatException>(() => SessionFile.Parse(good, "many.oscope", new SessionLimits(1 << 30, 1, 1024))).Message);
        string dir = Support.NewSocketDir();
        string path = Path.Combine(dir, "big.oscope");
        File.WriteAllBytes(path, good);
        Assert.Contains("the file has", Assert.Throws<ScopeFormatException>(() => SessionFile.Load(path, tight)).Message);
    }

    [Fact]
    public void RandomMutationsOnlyEverGiveTypedErrors()
    {
        (byte[] good, _, _) = MakeSessionWithFork();
        var rng = new Random(777);
        for (int i = 0; i < 3000; i++)
        {
            byte[] b = (byte[])good.Clone();
            int edits = rng.Next(1, 4);
            for (int e = 0; e < edits; e++)
            {
                // Aim at the structural bytes most of the time: the headers and each source's fixed part.
                int at = rng.Next(3) == 0 ? rng.Next(b.Length) : rng.Next(80);
                b[at] = (byte)rng.Next(256);
            }

            try
            {
                SessionFile f = SessionFile.Parse(b, "mut.oscope");
                Assert.Equal(b, Write(f));
            }
            catch (ScopeFormatException)
            {
                // Expected.
            }
        }
    }
}
