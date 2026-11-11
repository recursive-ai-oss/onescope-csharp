# Changelog

What changed in each release, newest first. The libraries (`RecursiveAI.OneScope.Sdk`, `.Probe` and `.Attach`) are released with a `v<version>` tag, under a `## [<version>]` heading; the tool (`RecursiveAI.OneScope.Tool`) with a `tool-v<version>` tag, under `## [tool <version>]`.

## [Unreleased]

## [tool 1.0.0] - 2026-11-11

The first stable release of the `onescope` tool (`RecursiveAI.OneScope.Tool`): a viewer for the deterministic libraries, built on onegame and bundling the physics and navigation modules. It runs in a window or headless, on the desktop platforms onegame supports.

- **Open and attach.** Open a scene, snapshot or replay file, or attach to a running game, test or benchmark that hosts the probe. A live source is re-simulated locally, so the host is never changed or slowed by the viewer.
- **Scrub.** One timeline across every open source, with tracks for events, markers, series and findings. Seeking to any step goes through keyframes and lands on the same hash as playing straight to it.
- **Inspect.** Entity tree, inspector, selection that follows entities across sources, fields drawn as colour maps, and overlays declared by the module.
- **Fork and poke.** Fork any source at any step, poke the fork (drag, spawn, delete, set a parameter), and step it. Every poke is recorded into the fork's own replay, and the fork is drawn on the timeline as a branch from its divergence step. Originals and live processes are never changed.
- **Compare.** A fork against its original, the same replay through two versions of a library side by side, a shared-entity diff that names the first differing entity and field, and ghost tracks from recorded pose dumps.
- **Sessions.** `.oscope` files keep the open sources, forks, layout and cameras.
- **MCP and CLI.** Every action is one operation with JSON results, reachable from the UI, the command line and MCP, with a window or headless. Headless captures (`capture_frame`, `capture_sequence`) are byte-identical from run to run on one renderer.
- **Bundled modules.** `RecursiveAI.OnePhysics.Scope` 1.1.0 (2D and 3D worlds, contacts, joints, islands and sleep; contact-normal arrows scale with body size) and `RecursiveAI.OnePath.Scope` 1.0.0 (navmesh build stages stepped one at a time, paths and search frontiers, flow fields, crowds), on `RecursiveAI.OnePhysics` 1.0.0 and `RecursiveAI.OnePath` 1.2.0.
- **Other modules** load from a path into their own collectible load context.
- Built on `RecursiveAI.OneScope.Sdk`, `.Probe` and `.Attach` 1.0.0 and onegame 1.25.1.

## [1.0.0] - 2026-11-11

The first stable release of `RecursiveAI.OneScope.Sdk`, `RecursiveAI.OneScope.Probe` and `RecursiveAI.OneScope.Attach`, all at 1.0.0. They have no package dependencies beyond .NET, and are NativeAOT- and trim-clean, so any program can reference them.

- **Sdk: the module contract.** A library plugs into the viewer by implementing `IScopeModule`, `IScopeSource` and `IScopeWorld`. A world advances by one input per step, snapshots and restores itself, and hashes its state. It lists its entities and inspector rows, and draws into a neutral `DrawList` (2D or 3D lines, points, triangles, meshes, shapes, arrows and labels, with overlays and entity tags). It also declares pokes, diffs two worlds, and reports timeline events, series and findings.
- **History.** Seeking to any step goes through keyframes plus deterministic re-simulation, and lands on the same hash as playing straight to that step. Forks are writable recordings that never touch their source. Sources can also step along axes other than time (build stages, solver iterations).
- **Fields, links, units, ghost sources and shared-entity diffs** for modules that need them, as default interface members.
- **Probe.** A host program streams a handshake, one snapshot and then its replay records over a local socket, only while a viewer is connected. With nobody connected, each recorded call costs one branch. The wire format is in `docs/probe-wire.md`.
- **Attach.** Lists running probes, connects, and turns the stream into a live source that re-simulates locally. It also reads and writes session files (`.oscope`, `docs/session-file.md`).
- **Formats owned by onescope:** the probe handshake v1, the session file v1 and the pose cache v1, each fuzzed.

Changes since 0.3.0-preview.1: `HistoryOptions` and its `History` constructor are removed (use `ScopeHistoryOptions`), `DrawStyle.Deconstruct` with four values is removed, and `ScopeHistoryOptions` is sealed. The public surface is listed in `docs/api.md`.
