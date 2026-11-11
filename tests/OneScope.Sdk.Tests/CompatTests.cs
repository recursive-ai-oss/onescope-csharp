using OneScope.Sdk;
using OneScope.TestModule;
using Xunit;

namespace OneScope.Sdk.Tests;

/// <summary>The convenience shape that stays: a recording that starts at step 0 and has no recorded hashes.</summary>
public class CompatTests
{
    [Fact]
    public void TheShortRecordingFactoryStartsAtStepZeroWithoutHashes()
    {
        TestSource full = Fixtures.OpenLong();
        List<ReadOnlyMemory<byte>> inputs = [];
        for (long s = 0; s < 300; s++)
        {
            inputs.Add(full.Recording.InputAt(s));
        }

        Recording shortForm = Recording.Create(full.Recording.Setup, inputs);
        Assert.Equal(0, shortForm.StartStep);
        Assert.Equal(300, shortForm.EndStep);
        Assert.True(shortForm.ExpectedHashes.IsEmpty);

        var history = new History(TestModule.TestModule.CreateSource("short", full.Recording.Setup.ToArray(), shortForm), new ScopeHistoryOptions { KeyframeInterval = 16 });
        foreach (long step in new long[] { 300, 17, 299, 0, 150 })
        {
            history.Seek(step);
            Assert.Equal(Fixtures.StraightHashes.Value[step], history.StateHash);
        }
    }
}
