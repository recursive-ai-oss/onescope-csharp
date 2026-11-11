using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using OneScope.Sdk;

namespace OneScope.TestModule;

/// <summary>Generates test replays: seeded pokes at pseudo-random steps, with the module's own per-step hashes.</summary>
public static class TestReplayBuilder
{
    /// <summary>
    /// Builds a replay of <paramref name="steps"/> steps. Pokes come from <paramref name="seed"/> alone, so the same arguments
    /// give the same bytes. Hash rows are computed by running a world straight through, without the history.
    /// </summary>
    public static byte[] Build(int dimensions, int bodies, uint seed, int steps)
    {
        byte[] setup = TestFormat.MakeSetup(dimensions, bodies, seed);
        var inputs = new List<ReadOnlyMemory<byte>>(steps);
        var world = new TestWorld(setup);
        var hashes = new ulong[steps + 1];
        hashes[0] = world.Hash();
        uint state = seed * 2654435761u + 12345u;
        var buffer = new ArrayBufferWriter<byte>();
        for (int s = 0; s < steps; s++)
        {
            buffer.ResetWrittenCount();
            state = unchecked(state * 1664525u + 1013904223u);
            uint r = state >> 8;
            if (r % 37 == 0)
            {
                int i = (int)(r / 37 % (uint)bodies);
                var id = new EntityId(TestModule.FirstBodyId + (ulong)i);
                uint kind = r / 1369 % 40;
                try
                {
                    if (kind < 30)
                    {
                        world.EncodePoke(new PokeCall("impulse", [ScopeValue.OfEntity(id), ScopeValue.OfVector(new Vector3((int)(r % 401) - 200, (int)(r / 7 % 601), (int)(r / 11 % 401) - 200))]), buffer);
                    }
                    else if (kind < 36)
                    {
                        world.EncodePoke(new PokeCall("teleport", [ScopeValue.OfEntity(id), ScopeValue.OfVector(new Vector3(r % 10000, 5000 + r / 3 % 5000, r / 5 % 10000))]), buffer);
                    }
                    else if (kind < 38)
                    {
                        world.EncodePoke(new PokeCall("set_gravity", [ScopeValue.OfInteger(-1 - (long)(r % 6))]), buffer);
                    }
                    else if (bodies > 8 && i >= bodies - 2)
                    {
                        world.EncodePoke(new PokeCall("remove", [ScopeValue.OfEntity(id)]), buffer);
                    }
                }
                catch (ArgumentException)
                {
                    // The body was already removed; this step has no poke.
                    buffer.ResetWrittenCount();
                }
            }

            byte[] input = buffer.WrittenSpan.ToArray();
            inputs.Add(input);
            world.Advance(input, TimelineSink.None);
            hashes[s + 1] = world.Hash();
        }

        var writer = new ArrayBufferWriter<byte>();
        TestFormat.Write(writer, setup, Recording.Create(setup, inputs), hashes);
        return writer.WrittenSpan.ToArray();
    }
}
