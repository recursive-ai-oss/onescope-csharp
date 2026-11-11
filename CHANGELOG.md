# Changelog

What changed in each release, newest first. The libraries (`RecursiveAI.OneScope.Sdk`, `.Probe` and `.Attach`) are released with a `v<version>` tag, under a `## [<version>]` heading; the tool (`RecursiveAI.OneScope.Tool`) with a `tool-v<version>` tag, under `## [tool <version>]`.

## [Unreleased]

## [1.0.0] - 2026-11-11

The first stable release of `RecursiveAI.OneScope.Sdk`, `RecursiveAI.OneScope.Probe` and `RecursiveAI.OneScope.Attach`, all at 1.0.0. They have no package dependencies beyond .NET, and are NativeAOT- and trim-clean, so any program can reference them.

- **Sdk: the module contract.** A library plugs into the viewer by implementing `IScopeModule`, `IScopeSource` and `IScopeWorld`. A world advances by one input per step, snapshots and restores itself, and hashes its state. It lists its entities and inspector rows, and draws into a neutral `DrawList` (2D or 3D lines, points, triangles, meshes, shapes, arrows and labels, with overlays and entity tags). It also declares pokes, diffs two worlds, and reports timeline events, series and findings.
- **History.** Seeking to any step goes through keyframes plus deterministic re-simulation, and lands on the same hash as playing straight to that step. Forks are writable recordings that never touch their source. Sources can also step along axes other than time (build stages, solver iterations).
- **Fields, links, units, ghost sources and shared-entity diffs** for modules that need them, as default interface members.
- **Probe.** A host program streams a handshake, one snapshot and then its replay records over a local socket, only while a viewer is connected. With nobody connected, each recorded call costs one branch. The wire format is in `docs/probe-wire.md`.
- **Attach.** Lists running probes, connects, and turns the stream into a live source that re-simulates locally. It also reads and writes session files (`.oscope`, `docs/session-file.md`).
- **Formats owned by onescope:** the probe handshake v1, the session file v1 and the pose cache v1, each fuzzed.

Changes since 0.3.0-preview.1: `HistoryOptions` and its `History` constructor are removed (use `ScopeHistoryOptions`), `DrawStyle.Deconstruct` with four values is removed, and `ScopeHistoryOptions` is sealed. The public surface is listed in `docs/api.md`.
