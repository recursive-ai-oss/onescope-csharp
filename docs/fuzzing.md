# Fuzzing the file formats

`tools/Fuzz` is a coverage-guided harness for the three formats onescope parses from untrusted bytes: the probe handshake and stream frame headers (`handshake`), the `.oscope` session file (`session`) and the pose cache (`posecache`). It uses SharpFuzz with AFL++, and it is a test tool that never ships.

A typed format error (`ScopeFormatException`, `ProbeFormatException`) is the right answer to bad input. Anything else is a finding: another exception, a hang (10 s per input), or an input that makes the parser allocate far more than the input's size. A session or pose cache that parses must also write back to the same bytes.

## Run

```
TARGETS="handshake session posecache" tools/Fuzz/run-afl.sh <work-dir> <seconds-per-target>
```

Needs the .NET SDK, `curl`, `make` and `gcc`. On first use it builds AFL++ and installs the SharpFuzz tool into `<work-dir>`, then publishes the harness, writes seed corpora (`Fuzz seeds <dir>`, plus the pose caches in `testdata/`), instruments the assemblies and fuzzes each target. It prints executions, bitmap coverage, crashes and hangs from AFL's stats. Crashing inputs are in `<work-dir>/out-<target>/default/crashes`.

Replay one input without the fuzzer:

```
dotnet <work-dir>/app/Fuzz.dll replay <handshake|session|posecache> <file>
```

Every crash found becomes a minimized file in `testdata/` and a test that parses it and expects the typed error (see the `fuzz-posecache-*` files).
