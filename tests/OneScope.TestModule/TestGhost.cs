using System.Buffers;
using System.Numerics;
using OneScope.Sdk;

namespace OneScope.TestModule;

/// <summary>Writes pose caches from test-module runs: the stand-in for a reference engine's pose dump.</summary>
public static class TestGhost
{
    /// <summary>
    /// Plays <paramref name="source"/> from step 0 and records every body's position from <paramref name="fromStep"/> for <paramref name="frames"/> frames
    /// as a pose cache whose origin names the test module, <paramref name="replayName"/> and <paramref name="libraryVersion"/>.
    /// Every position is moved by <paramref name="offset"/> (millimetres), so a ghost drawn over its own source is visibly apart from it.
    /// Absent (removed) bodies get a NaN position.
    /// </summary>
    public static PoseCache Record(TestSource source, long fromStep, int frames, Vector3 offset, string replayName, string libraryVersion = "1.0")
    {
        ArgumentNullException.ThrowIfNull(source);
        var world = (TestWorld)source.CreateWorld();
        Recording recording = source.Recording;
        while (world.Step < fromStep)
        {
            world.Advance(recording.InputAt(world.Step).Span, TimelineSink.None);
        }

        int count = world.BodyCount;
        var entities = new PoseEntity[count];
        for (int i = 0; i < count; i++)
        {
            entities[i] = new PoseEntity(new EntityId(TestModule.FirstBodyId + (ulong)i), "ref-body-" + i, PoseShape.Sphere, new Vector3(150, 0, 0));
        }

        var poses = new float[frames * count * 7];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < count; i++)
            {
                int at = ((f * count) + i) * 7;
                if (world.TryGetBodyPosition(i, out Vector3 p))
                {
                    Vector3 q = p + offset;
                    poses[at] = q.X;
                    poses[at + 1] = q.Y;
                    poses[at + 2] = q.Z;
                    poses[at + 6] = 1;
                }
                else
                {
                    poses[at] = float.NaN;
                }
            }

            if (f + 1 < frames)
            {
                world.Advance(recording.InputAt(world.Step).Span, TimelineSink.None);
            }
        }

        var info = new PoseCacheInfo(source.Info.Dimensions, LengthUnit.Millimetre, fromStep, TestFormat.ModuleId, libraryVersion, "test-box", replayName, "test reference dump");
        return PoseCache.Create(info, entities, poses);
    }

    /// <summary>The cache as file bytes.</summary>
    public static byte[] ToBytes(PoseCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var writer = new ArrayBufferWriter<byte>();
        cache.Write(writer);
        return writer.WrittenSpan.ToArray();
    }
}
