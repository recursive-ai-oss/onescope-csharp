# onescope

A viewer for deterministic libraries. Open a scene, snapshot or replay, or attach to a running program, and watch the world: scrub its history, inspect any entity, compare runs, and fork from any frame to poke at it. One shell, one module per library; physics and navigation modules are bundled. Everything the window does is also a command-line operation and an MCP tool, so an agent can drive it the same way.

Time is a step index. Seeking to step *k* lands on exactly the state that playing straight to *k* gives, however you got there, and the world hash it reports is the library's own. A fork re-simulates from the original's bits, records each poke into its own replay, and shows where it diverges. The original file and the running program are never changed.

## Install

```
dotnet tool install --global RecursiveAI.OneScope.Tool --prerelease
onescope --help
```

Requires the .NET 10 SDK or runtime. The shell is a [onegame](https://github.com/recursive-ai-oss/onegame) program: it opens a window on Linux, macOS and Windows, or runs headless and renders offscreen.

## Use it

```
# Open a replay (physics and navigation files open with no extra options)
onescope open run.oppr

# Scrub, inspect, capture: run operations after opening, print each result as JSON, and exit
onescope open run.oppr --headless --run 'seek {"step":120}' --run 'inspect {"entity":1}' --run 'capture_frame {"path":"frame.png"}'

# Watch a running program that called the probe
onescope attach                      # lists programs with a probe
onescope attach 48213                # opens the shell on its live stream

# Fork from the current frame, poke the fork, step it, and find where it diverges
onescope open run.oppr --live /tmp/onescope.sock &
onescope fork --socket /tmp/onescope.sock
onescope entities '{"source":1,"limit":5}' --socket /tmp/onescope.sock   # entity ids
onescope poke '{"source":1,"poke":"impulse","args":{"target":<id>,"impulse":[0,5,0]},"step":true}' --socket /tmp/onescope.sock
onescope diff '{"source":1}' --socket /tmp/onescope.sock

# Compare two versions of a library on one replay, each module in its own load context
onescope open run.oppr --run 'open {"path":"run.oppr","moduleAssembly":"/path/to/other/module.dll"}' --run 'compare'

# Save everything (sources, forks with their pokes, views) and reopen it later
onescope session_save '{"path":"work.oscope"}' --socket /tmp/onescope.sock
onescope open work.oscope
```

In the window: the entity tree, inspector, one or more 2D and 3D scene views, a timeline with a track per source (forks hang below it as branches from the step they diverge at), an event log and a compare panel. Drag a body in a fork to poke it.

## MCP

`onescope open <file> --live <socket>` serves the operations over MCP on a Unix socket, and `onescope mcp --socket <socket>` bridges it to stdio. In an MCP client's configuration:

```json
{ "mcpServers": { "onescope": { "command": "onescope", "args": ["mcp", "--socket", "/tmp/onescope.sock"] } } }
```

Every operation (`open`, `attach`, `sources`, `seek`, `step`, `play`, `entities`, `inspect`, `overlay`, `fork`, `poke`, `diff`, `compare`, `close`, `capture_frame`, `capture_sequence`, `session_save`, and the rest) takes and returns JSON. They are listed with their arguments and results in [docs/shell.md](docs/shell.md).

## Watch your own program

A host program references `RecursiveAI.OneScope.Probe` and calls it once. The probe listens on a local socket in a per-user directory, sends the library's own replay bytes, and costs one branch per call when nothing is connected. It never blocks or throws into the host, and a viewer that falls behind is dropped. See [docs/live-attach.md](docs/live-attach.md) and `samples/`.

## Add a library

A module is a package built against `RecursiveAI.OneScope.Sdk` (BCL only) that opens the library's files into a deterministic world and describes it as data: entities and properties, a neutral draw list in world units, timeline tracks, overlays, pokes and findings. The library never depends on onescope; the module lives in the library's repository. Start with [docs/module-authoring.md](docs/module-authoring.md); the full public surface is in [docs/api.md](docs/api.md). Modules load from a path with `--module` into their own load context.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneScope.Sdk` | The module contract, the draw list and the time model. BCL only; AOT and trim safe. |
| `RecursiveAI.OneScope.Probe` | What a host calls to stream a replay. BCL only; AOT, trim and WebAssembly safe. |
| `RecursiveAI.OneScope.Attach` | The viewer side of a probe, and the `.oscope` session file. BCL only. |
| `RecursiveAI.OneScope.Tool` | The `onescope` command and shell. |

## Build and test

```
dotnet build OneScope.slnx -c Release
dotnet test --project tests/OneScope.Sdk.Tests -c Release
dotnet test --project tests/OneScope.Attach.Tests -c Release
dotnet test --project tests/OneScope.App.Tests -c Release
```

Build output goes to `artifacts/` (ignored), or to the directory named by `ONESCOPE_ARTIFACTS`. The bundled physics and navigation modules, and `RecursiveAI.OnePath` 1.2.0, are restored from nuget.org; until they are published there, point `ONESCOPE_FEEDS` at a folder that holds them (several folders are separated by `;`). Captures are compared with goldens that come from the lavapipe software renderer; on another renderer the tests check that three runs agree with each other.

Documentation index: [docs/README.md](docs/README.md).

Licensed under `MIT OR Apache-2.0`, © Recursive AI, LLC.
