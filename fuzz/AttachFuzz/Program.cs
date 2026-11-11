// SharpFuzz harness for the two parsers of untrusted bytes that onescope owns outside the modules:
//   AttachFuzz handshake   the probe handshake and stream frame headers
//   AttachFuzz session     the .oscope session file
//   AttachFuzz posecache   the pose cache that opens as a ghost source
//   AttachFuzz seeds <dir>     write a small seed corpus for both targets
//   AttachFuzz replay <handshake|session|posecache> <file>   run one input (a crash repro) through the chosen target without the fuzzer
// Typed errors are the expected answer to bad input; any other exception is a finding.
using System.Buffers;
using OneScope.Attach;
using OneScope.Probe;
using OneScope.Sdk;
using OneScope.TestModule;
using SharpFuzz;

if (args.Length == 2 && args[0] == "seeds")
{
    WriteSeeds(args[1]);
    return 0;
}

if (args.Length >= 3 && args[0] == "replay")
{
    using FileStream f = File.OpenRead(args[2]);
    Run(args[1], f);
    Console.WriteLine("ok");
    return 0;
}

if (args.Length != 1 || args[0] is not ("handshake" or "session" or "posecache"))
{
    Console.Error.WriteLine("usage: AttachFuzz handshake|session|posecache   (run under afl-fuzz)");
    return 2;
}

string target = args[0];
Fuzzer.OutOfProcess.Run(stream => Run(target, stream));
return 0;

static void Run(string target, Stream stream)
{
    // Inputs are capped like a real read would be; the parsers must enforce their own limits.
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    byte[] bytes = ms.ToArray();
    try
    {
        if (target == "handshake")
        {
            try
            {
                _ = ProbeWire.ParseHandshake(bytes, "fuzz");
            }
            catch (ProbeFormatException)
            {
            }

            for (int first = 0; first < 2; first++)
            {
                try
                {
                    _ = ProbeWire.ReadFrameHeader(bytes, new ProbeLimits(1 << 20, 1 << 16), first == 0, "fuzz");
                }
                catch (ProbeFormatException)
                {
                }
            }
        }
        else if (target == "posecache")
        {
            PoseCache cache = PoseCache.Parse(bytes, "fuzz.ospc");

            // A cache that parses must write back to the same bytes.
            var w = new ArrayBufferWriter<byte>();
            cache.Write(w);
            if (!w.WrittenSpan.SequenceEqual(bytes))
            {
                throw new InvalidOperationException("a parsed pose cache did not write back to the same bytes");
            }
        }
        else
        {
            SessionFile file = SessionFile.Parse(bytes, "fuzz.oscope", new SessionLimits(1 << 24, 256, 1 << 20));

            // A session that parses must write back to the same bytes.
            var w = new ArrayBufferWriter<byte>();
            file.Write(w);
            if (!w.WrittenSpan.SequenceEqual(bytes))
            {
                throw new InvalidOperationException("a parsed session did not write back to the same bytes");
            }
        }
    }
    catch (ScopeFormatException)
    {
    }
}

static void WriteSeeds(string dir)
{
    Directory.CreateDirectory(Path.Combine(dir, "handshake"));
    Directory.CreateDirectory(Path.Combine(dir, "session"));
    Directory.CreateDirectory(Path.Combine(dir, "posecache"));
    var source2d = (TestSource)new TestModule().Open(TestReplayBuilder.Build(2, 4, 1, 60), "seed");
    File.WriteAllBytes(Path.Combine(dir, "posecache", "ghost"), TestGhost.ToBytes(TestGhost.Record(source2d, 5, 12, new System.Numerics.Vector3(100, 0, 0), "seed.ostm")));
    var info = new ProbeInfo("onephysics", "1.0.0", 3);
    byte[] hello = new byte[ProbeWire.HandshakeLength(info)];
    ProbeWire.WriteHandshake(hello, info, 1234);
    File.WriteAllBytes(Path.Combine(dir, "handshake", "hello"), hello);
    byte[] frame = new byte[5];
    ProbeWire.WriteFrameHeader(frame, ProbeFrameKind.Record, 100);
    File.WriteAllBytes(Path.Combine(dir, "handshake", "frame"), frame);

    var module = new TestModule();
    var small = (TestSource)module.Open(TestReplayBuilder.Build(2, 4, 1, 40), "seed");
    var fork = small.WithRecording(small.Recording.Fork(10));
    var session = new SessionFile { CursorStep = 7, Layout = new byte[] { 9, 8, 7 } };
    session.Sources.Add(SessionSource.Embed(small, 8));
    session.Sources.Add(SessionSource.EmbedFork(fork, 0, 10, 4));
    session.Sources.Add(SessionSource.Reference("oscope.test", "ref", "data/ref.ostm", new byte[] { 1, 2, 3 }));
    var w = new ArrayBufferWriter<byte>();
    session.Write(w);
    File.WriteAllBytes(Path.Combine(dir, "session", "three-sources"), w.WrittenSpan.ToArray());
    w.ResetWrittenCount();
    new SessionFile().Write(w);
    File.WriteAllBytes(Path.Combine(dir, "session", "empty"), w.WrittenSpan.ToArray());
}
