# onescope

A viewer for deterministic libraries. Open a scene, snapshot or replay, or attach to a running program, and watch the world: scrub its history, inspect any entity, compare runs, and fork from any frame to poke at it. One shell, one module per library.

This repository holds the foundation:

- `src/OneScope.Sdk` (`RecursiveAI.OneScope.Sdk`): the module contract. A module opens a library's files into a deterministic world and describes it as data: entities and properties, a neutral draw list in world units, timeline tracks, overlays, pokes, findings, a length unit and the axes the world can be stepped along. The SDK also holds the time model (`History`): keyframe snapshots plus deterministic re-simulation, exact seeking and forks. BCL only, 0.x.
- `src/OneScope.Probe` (`RecursiveAI.OneScope.Probe`): what a host program calls to stream its library's replay records to a viewer. BCL only. This version has no transport yet; a recorded call with nothing connected is one branch.
- `tests/OneScope.TestModule`: a small integer simulation with its own replay format that exercises every SDK feature. Not packed.

Semantics are in `docs/`. Build and test:

```
dotnet build OneScope.slnx -c Release
dotnet test --project tests/OneScope.Sdk.Tests -c Release
```

Build output goes to `artifacts/` (ignored), or to the directory named by `ONESCOPE_ARTIFACTS`.

Licensed under `MIT OR Apache-2.0`, © Recursive AI, LLC.
