# Writing a module

A module teaches the shell one library. It is a package in the library's own repository, built against `RecursiveAI.OneScope.Sdk` (BCL only, no other
dependency). The library itself never references onescope. This page is the contract; every public member has an XML doc comment with its units, limits and
what it throws, and [api.md](api.md) lists all of them. `tests/OneScope.TestModule` is a small complete module that uses every feature.

## The three interfaces

```csharp
public sealed class MyModule : IScopeModule              // also ILiveModule, to accept a live attach
{
    public string Id => "mylib";                         // stable; the probe handshake names it
    public string Name => "My library";
    public IScopeSource Open(ReadOnlyMemory<byte> data, string name);   // throws ScopeFormatException for bytes it does not own
}
```

`Open` recognises its library's files (replay, snapshot, scene) by their own magic bytes and returns an `IScopeSource`; the shell offers a file to each loaded module
in turn and takes the first that opens it. Parse the bytes as untrusted: check lengths before allocating, keep arithmetic `checked`, and throw `ScopeFormatException`
with a message that says what was expected and where.

An `IScopeSource` holds the declarations (`SourceInfo`) and the `Recording`, and makes worlds:

- `Info`: id, name, dimensions (2 or 3), `LengthUnit` (grids and rulers use it), the axes, entity kinds, overlays (ids 0 to 63), pokes, timeline tracks and fields.
- `Recording`: the setup bytes plus one opaque input per step, starting at `StartStep` (0 for a replay; the host's step for a live attach). The shell never reads an
  input; only your world does. A step's input is the concatenation of self-delimiting encoded pokes (empty on most steps).
- `CreateWorld()`: a fresh world at the recording's start. `WithRecording(r)`: the same source over another recording (a fork's). `WriteReplay(writer)`: the source's
  recording in the library's own replay format, so a fork can be saved and replayed by the library itself.

An `IScopeWorld` is one deterministic world:

| Member | What it must do |
|---|---|
| `Step`, `Hash()` | The step reached and a 64-bit hash of the whole state. Equal states hash equal on every platform; the shell compares it with the file's recorded hashes. |
| `Advance(input, sink)` | Apply the step's input, then run one step. Report events, markers, series samples and findings through `sink` (it only records the first time a step is reached). |
| `AdvanceAxis(axis, position, input)` | Run part of the step along another axis (a build stage, a solver iteration) on a scratch world; the time state must not change. |
| `WriteSnapshot`, `Restore` | The exact state, so the shell can keyframe and seek. Restore then step must equal stepping straight through. |
| `CollectEntities`, `Inspect` | The entity tree (`EntityId` is an unsigned 64-bit id, 0 is none) and the property rows for the inspector. |
| `Draw(list, request)` | Write this frame's draw list; `request.IsOn(overlay)` says which overlays are on. |
| `EncodePoke(call, writer)` | Turn a declared poke into the bytes your `Advance` will apply. |
| `FirstDifference(other)` | The first differing entity and field in a fixed order, or null. The desync finder your library has is the natural source. |

Optional, with defaults: `TryReadField`/`TryReadFieldAt` (values of a declared field), `CollectLinks` (an entity that continues one in another source) and
`CompareShared` (diff over only the entities two worlds both hold).

## What the shell does with them

[history.md](history.md) has the exact semantics. In short: `History` keeps keyframe snapshots every N steps (default 64) and re-simulates between them, so `Seek(k)`
gives the same hash as stepping to `k`; memory is bounded; a fork copies the recording up to its step, appends every poke to its own recording, and reports the first step
whose input differs from the original's. Nothing in the SDK reads a clock.

## The draw list

`DrawList` takes lines, points, triangles, meshes, instanced boxes, spheres, capsules and cylinders, arrows and labels, in the source's length unit (Z is 0 for 2D).
Each item carries a `DrawStyle`: colour, overlay id, layer, flags (`Wireframe`, `OnTop`) and the `EntityId` it shows, which is how the shell picks the entity under the
pointer and outlines the selection. Colour by state (static, kinematic, awake, asleep, sensor) is the module's choice. The list is cleared and refilled each frame and
allocates nothing once warm; reuse your own scratch buffers the same way. Field meshes and points carry a per-vertex or per-triangle scalar with a named colour map
([fields-links-ghosts.md](fields-links-ghosts.md)).

## Pokes, tracks, findings, overlays

- **Pokes** are declared as data (`PokeInfo`: id, name, typed parameters). Mark one `PokeRole.DragToPoint` (an entity and a vector) to make dragging a body in the window use it.
  A poke only ever applies to a fork.
- **Tracks** (`TrackInfo`): `Event` (contact begin, joint break), `Marker` and `Series` (energy, step time). Write them from `Advance` through the `TimelineSink`.
- **Findings**: code, severity, entities, text and a suggested change, at a step; the shell shows them on the timeline and in the event log.
- **Overlays**: named toggles; draw an item under an overlay id and the shell hides it when that overlay is off.

## Live attach

Implement `ILiveModule` as well: `OpenLive` gets the host's snapshot and returns an `ILiveSource` that takes each replay record as it arrives. The host feeds the probe the library's replay
bytes ([live-attach.md](live-attach.md), [probe-wire.md](probe-wire.md)); the probe never interprets them. A module that can already read the library's replay can usually
read the stream with a small adapter.

## Packaging and loading

Reference the SDK with a version range that starts at the release you built against. The shell loads the module either as a bundled package or from a path
(`onescope open file --module path/to/Module.dll`) into its own collectible `AssemblyLoadContext`; the framework, onegame and the SDK assembly are shared with the host,
and everything else the module needs loads from its own folder. Two versions of one module can therefore play one replay side by side (`compare`). Keep the module's public
types to the contract; do not reflect over the host.

## Checking your module

Drive it through the shell, the way a user or agent will, and check the three things the viewer promises: the shell's hash at every seek equals the library's own hash for that
step; a fork's saved replay reproduces the fork's hashes; a planted difference is named by the first differing entity and field. The test module's tests in this repository do each against a
small simulation, and `tests/OneScope.App.Tests/BundledModuleBindingTests.cs` shows a cheap check that a module built on an older SDK still binds to the current one.

## Changes before 1.0

The first stable release removed the 0.1 names `HistoryOptions` (use `ScopeHistoryOptions`) and the `History` constructor that took it, and `DrawStyle`'s four-value `Deconstruct`.
`ScopeHistoryOptions` is sealed. `DrawStyle`'s four-argument constructor and `Recording.Create(setup, inputs)` stay. The bundled modules were built on earlier previews and bind unchanged: a test compiles every method of each against the current SDK.
