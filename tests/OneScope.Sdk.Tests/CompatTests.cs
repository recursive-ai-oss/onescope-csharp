using System.Reflection;
using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

/// <summary>
/// The shapes of SDK 0.1 that stay so a module built against it still loads. The pack is also checked against the 0.1 package at pack
/// time (package validation); these tests pin the exact binary signatures and that the old shapes behave like the new ones.
/// </summary>
public class CompatTests
{
#pragma warning disable CS0618 // The obsolete 0.1 names are the point of these tests.
    [Fact]
    public void TheVersionOneBinarySignaturesExist()
    {
        MethodInfo? create = typeof(Recording).GetMethod("Create", [typeof(ReadOnlyMemory<byte>), typeof(IEnumerable<ReadOnlyMemory<byte>>)]);
        Assert.NotNull(create);
        Assert.NotNull(typeof(History).GetConstructor([typeof(IScopeSource), typeof(HistoryOptions)]));
        Assert.NotNull(typeof(DrawStyle).GetConstructor([typeof(Color32), typeof(int), typeof(int), typeof(DrawFlags)]));
        Assert.NotNull(typeof(DrawStyle).GetMethod("Deconstruct", [typeof(Color32).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(DrawFlags).MakeByRefType()]));
        Assert.Equal(ScopeHistoryOptions.DefaultKeyframeInterval, HistoryOptions.DefaultKeyframeInterval);
    }

    [Fact]
    public void TheOldRecordingHistoryAndStyleShapesWorkLikeTheNewOnes()
    {
        TestSource full = Fixtures.OpenLong();
        List<ReadOnlyMemory<byte>> inputs = [];
        for (long s = 0; s < 300; s++)
        {
            inputs.Add(full.Recording.InputAt(s));
        }

        Recording old = Recording.Create(full.Recording.Setup, inputs);
        Assert.Equal(0, old.StartStep);
        Assert.Equal(300, old.EndStep);
        Assert.True(old.ExpectedHashes.IsEmpty);

        var viaOld = new History(TestModule.TestModule.CreateSource("old", full.Recording.Setup.ToArray(), old), new HistoryOptions { KeyframeInterval = 16 });
        var viaNew = new History(TestModule.TestModule.CreateSource("new", full.Recording.Setup.ToArray(), old), new ScopeHistoryOptions { KeyframeInterval = 16 });
        var viaNull = new History(TestModule.TestModule.CreateSource("null", full.Recording.Setup.ToArray(), old), (HistoryOptions?)null);
        foreach (long step in new long[] { 300, 17, 299, 0, 150 })
        {
            viaOld.Seek(step);
            viaNew.Seek(step);
            viaNull.Seek(step);
            Assert.Equal(Fixtures.StraightHashes.Value[step], viaOld.StateHash);
            Assert.Equal(viaNew.StateHash, viaOld.StateHash);
            Assert.Equal(viaNew.StateHash, viaNull.StateHash);
        }

        Assert.Equal(16, viaOld.KeyframeSteps.Skip(1).First());

        var style = new DrawStyle(new Color32(1, 2, 3, 4), 5, 6, DrawFlags.None);
        Assert.Equal(EntityId.None, style.Entity);
        (Color32 color, int overlay, int layer, DrawFlags flags) = style;
        Assert.Equal((new Color32(1, 2, 3, 4), 5, 6, DrawFlags.None), (color, overlay, layer, flags));
    }
#pragma warning restore CS0618
}
