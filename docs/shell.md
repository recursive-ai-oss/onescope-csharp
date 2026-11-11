# The shell and its operations

`onescope` is a onegame program. One process holds the opened sources, the scene views and the UI. Everything the shell does is an
operation, and the operations are the same from the window, the command line and MCP. Results are JSON.

## Running it

```
onescope open <file> [--module <dll>]... [--headless] [--live [path]] [--run "<op> [json]"]... [--size WxH] [--artifacts <dir>]
onescope open <session.oscope>                                     # reopen a saved session
onescope attach                                                    # list running programs that have a probe, as JSON
onescope attach <pid|socket> [--dir <probe dir>] [same options as open]   # open the shell on a running program's live stream
onescope <operation> [json | --name value ...] --socket <path>     # drive a running shell, prints the operation's JSON
onescope mcp --socket <path>                                       # stdio MCP bridge for an agent
```

`open` loads the file with the first loaded module that accepts it. A module is an assembly built against `RecursiveAI.OneScope.Sdk`;
`--module` loads one from a path into its own load context (the framework, onegame and the SDK are shared with the host).
`--headless` renders offscreen; without it the shell opens a window. `--live` serves the operations on a Unix socket, one JSON-RPC (MCP)
message per line; `onegame mcp --socket <path>` bridges to it as well. Errors are `{"ok":false,"error":{"code","message"}}` with exit code 1.
Exit codes: 0 ok, 1 an operation failed, 2 usage, 4 could not run.

## Time

Time is the step index. `seek` moves every source to the same step (a source that ends or starts elsewhere stops at its end or start) and lands on exactly the
state that stepping straight there gives, so the `hash` in a result is the module's world hash at that step. Playback speed (`play`) is
presentation only: it counts presentation frames at 60 per second, never a clock, and only decides which step the next frame shows.
A source's other axes (`axes`) are positions inside one step; `seek` with `axis` and `position` moves a scratch view and leaves the time
cursor and `hash` alone, and reports `viewAxis`, `viewPosition` and `viewHash`.

## Operations

Hashes are 16 lowercase hex digits. Entity ids are the module's integers. Every operation that names a `source` or `view` defaults to the active one.

| Operation | Arguments | Result |
|---|---|---|
| `open` | `path`, `module`?, `moduleAssembly`?, `keyframeInterval`?, `scan`? | the new source: `source`, `name`, `path`, `module`, `dimensions`, `unit {name, metres}`, `steps`, `axes`, `overlays`, `tracks`, `pokes`, `step`, `hash`, `keyframeInterval`, `scannedThrough`, `views` |
| `sources` | | `step`, `start`, `steps`, `speed`, `sources[]` (the `open` fields plus `keyframes`, `keyframeBytes`, `findings`, and for a live source `live {state: streaming|ended|disconnected|failed|capped|closed, endedBecause?, follow, hostPid, library, format, firstStep, newestStep, recordBytes, capBytes}`, and when the file recorded hashes `recordedHashes {through, matches: all|so far|no, firstMismatch?}`), and `attaching[]`: attaches still connecting or failed (`attach`, `state`, `target`, `startFrame`, `frame`, and for a failure `errorCode`, `error`) |
| `attach` | none: list. `pid` or `socket` to connect; `dir`?, `keyframeInterval`?, `moduleAssembly`?, `wait`? (ms the call may block, default 100), `timeout`? (ms the connection may take, default 30000), `capBytes`? (received record bytes kept, default 268435456) | list: `probes[] {pid, socket, module, library, format, moduleLoaded}`, `removedStale[]`, `unreadable[]`. Connect: the new source as for `open`, plus `live`, when it is ready within `wait`; otherwise `{attach, state: "connecting", target, startFrame, frame}` and the source appears when the host has taken its snapshot. Errors within `wait`: `not_found`, `timeout`, `bad_stream`, `no_module`, `bad_format`; later ones are listed under `sources.attaching` |
| `follow` | `source`? (default the first live one), `on`? | `source`, `live`, `step`; on moves the cursor to the newest step |
| `session_save` | `path` (inside the output folder), `embed`? (embed opened files too) | `path`, `bytes`, `sha256`, `cursor`, `sources[] {source, name, kind: referenced\|embedded, live, parent?, forkStep?, bytes}`, `layoutBytes` |
| `session_open` | `path`, `moduleAssembly`? | `path`, `step`, `views`, `sources[] {source, label, module, startStep, steps, step, hash, parent?, forkStep?, divergence?}`. Replaces everything open; nothing changes on failure (`not_found`, `bad_session`) |
| `seek` | `step`, or `axis` + `position` (+ `source`) | `step`, `steps`, `sources[] {source, step, hash, viewAxis?, viewPosition?, viewHash?}` |
| `step` | `delta`? (default 1, negative goes back), `axis`?, `source`? | as `seek`; the step is clamped to the recording |
| `play` | `speed`? (steps per second, 0 pauses, negative reverses), `frames`? (run this many presentation frames now), `loop`? | `speed`, `playing`, `loop`, `frames`, `moved`, `step`, `hashes[]` |
| `entities` | `source`?, `kind`?, `limit`? | `entities[] {id, parent, kind, name}`, `count`, `truncated` |
| `inspect` | `entity`, `source`? | `entity`, `kind`, `name`, `properties[] {group, name, kind, value}`; also selects the entity in the shell |
| `overlay` | `view`?, `overlay`? (name or id), `on`? (toggles when omitted), `wireframe`?, `grid`? | `view`, `kind`, `wireframe`, `grid`, `overlays[] {id, name, on}` |
| `axes` | `source`? | `step`, `axes[] {id, name, length, position}` |
| `findings` | `source`?, `from`?, `to`?, `severity`?, `limit`? | `scannedThrough`, `findings[] {step, code, severity, entities, text, suggestedChange}`, `count`, `truncated` |
| `fork` | `source`? (default the active one), `step`? (default the shared cursor), `view`? (default true: add a view of the fork) | the new source (every `open` field plus `fork {parent, forkStep, divergence, pokedSteps, pending}`) and `branch`, its number |
| `poke` | `source`, `poke` (id or name), `args` (object by parameter name, or array in order), `step`? (run one step now) | `source`, `poke`, `appliesAtStep`, `pending`, `step`, `hash`. Without `poke`: the source's declarations with each poke's parameters |
| `diff` | `source` (a fork), `other`? (default its parent), `step`? (default the cursor, at most the shorter recording) | `source`, `other`, `step`, `equal`, `hash`, `otherHash` (at that step), `divergence`, `difference {entity, field, value, otherValue}` or null |
| `replay_save` | `source`, `path` (a new file in the output folder) | `path`, `bytes`, `sha256`, `steps`, `divergence` |
| `pick` | `x`, `y` (window pixels), `view`? | `view`, `source`, `entity` or null, `point` (world) |
| `project` | `entity`, `view`? or `source`? | `view`, `entity`, `x`, `y` (window pixels), `onScreen`, `world` |
| `view` | nothing to list; `add` (`2d`/`3d`), or `index` with `remove`, `source`, `target`, `halfHeight` (2D), `distance`, `yaw`, `pitch` (3D), `frame` (the moving bodies; see Framing), `frameSelection` (the selected entity), `wireframe`, `grid`, `active` | `active`, `views[]` |
| `timeline` | `fit`? (`all` or `fork`, with `source`?), `from`? and `to`?, `zoom`? (factor above 1 shows fewer steps) with `center`?, `pan`? (steps); nothing reports | `from`, `to`, `zoomed`, `axisFrom`, `axisTo`, `step`. View only: no world moves |
| `layout` | `entities`?, `inspector`?, `timeline`?, `log`? (booleans) | the panel states |
| `capture_frame` | `path`? (inside the output folder), `size`? (`720p`, `1080p`, `WxH`), `scale`?, `view`? (that view alone, full size), `presented`? | `path`, `width`, `height`, `scale`, `sha256`, `renderer`, `target`, `step`, `hashes[]`; over MCP the PNG travels as image content |
| `capture_sequence` | `dir`, `count` (1 to 3600), `stride`? (default 1), `axis`?, `source`?, `size`?, `scale`?, `view`? | `dir`, `frames`, `ended`, `manifest`, `renderer`, `step`; `manifest.json` lists each frame's `step`, `position`, `hash` and `sha256` |

onegame's own `info`, `ui_tree`, `ui_query`, `ui_click`, `ui_hit`, `ui_scale`, `input_pointer`, `input_key`, `input_text`, `window`, `render_graph`
and `quit` are served unchanged (`docs/interfaces/control.md` in onegame).

## Forks and pokes

`fork` makes a new source whose recording starts as a copy of its parent's up to a step. Opened files are read-only: `poke` on one fails with `read_only`.
A poke is queued on a fork and takes effect on its next step; `step` with the fork as `source` (or active) at the end of its recording runs new steps
in it, the first one carrying the queued pokes, and the shared cursor follows. Seeking a fork back and forth replays its recording, so it gives the
hashes the straight run gave. `diff` compares a fork with its parent at a step and names the first differing entity and field in the module's order.
`divergence` is the first step whose recorded input differs, so the two worlds are equal at it and differ from the next step. `replay_save` writes the
fork's replay; opened again, it gives the fork's hashes at every step. A fork of a fork is allowed; `Parent` chains.

A press in a scene view selects the entity under it, which is outlined in every view of the same file. On a fork, and when the module declares a drag poke
(`PokeRole.DragToPoint`), the press also grabs the entity: each pointer move is one step of the fork with that poke aimed at the pointer (on the view plane in 2D,
on the plane through the grabbed point facing the camera in 3D), and releasing ends the drag. Elsewhere a drag moves the camera. `pick` and `project` give an
agent the same hit test and the pixel to press, so it can script a drag with `input_pointer`.

Error codes: `bad_args` (the message names the argument and what it must be), `not_found` (a file, source, view, entity or overlay),
`no_source`, `no_module`, `bad_stream`, `timeout`, `bad_session`, `bad_format` (the file is not one any loaded module opens; the message names the file and each module's complaint),
`bad_module`, `too_large`, `path_outside` (an output path leaving the output folder), `rejected`, `read_only` (poking an opened file), `unknown_op`.

## Live sources and sessions

`attach` makes a running program's probe stream a source whose steps are the host's step numbers (`startStep` is the host's step at the attach); it plays,
scrubs and forks like a file, and its history keeps growing while you scrub (`follow` decides whether the cursor goes with it). `session_save` writes
the open sources, the cursor and the layout to a `.oscope` file (format in `session-file.md`): opened files by path and SHA-256, forks and live sources embedded
with the history received so far. `session_open` (or `onescope open x.oscope`) replaces the shell's state with it: forks come back as working forks that can be poked and
stepped again, and views, cameras, panels, selection and the event log come back as they were, so a capture taken after reopening is byte-identical to one taken before saving.
A live source also has a viewer-side cap: the viewer keeps at most `capBytes` of received records (each counted with 48 bytes of bookkeeping). When the next record would
pass it the viewer stops and disconnects: the source's state is `capped`, `endedBecause` says where, the event log gets an error row, and what was received stays usable
(seek, fork, save). The program never notices beyond a viewer that left.

The layout is a small JSON object (`version 1`: panels, active source and view, selection, speed, source labels, views with cameras, log rows); a layout that is not
valid, or that points at a source or view that is not there, is refused as `bad_session` and leaves the shell as it was. Other errors: `bad_session` also covers a
malformed or hostile session file, a missing module, or a referenced file that changed.

## The window

The toolbar has the transport, Fork, **Frame** and **Frame sel.**, and the overlay toggles. Beside the inspector, a **Pokes** panel appears when the active source
is a fork: one button per poke the module declares, then a field for each of its typed parameters (an entity takes an id or, when empty, the selected entity; a
vector takes `x,y,z` and starts at the centre of the active view; integers, numbers, text and booleans as typed), **Apply** and **Step**. With *Step after poke* on, Apply
queues the poke and runs one step of the fork (as `poke` with `step:true`); with it off the poke waits for **Step**. A refused entry goes to the event log and changes nothing.
The panel is built from the module's declarations, so a module's new poke shows up without an app change. Pressing and dragging a body on a fork is the drag poke, as before.

Above the timeline, **Fit all**, **Fit fork**, **Zoom in/out** and **<< >>** change which steps it spans; the wheel zooms around the pointer, and `+`, `-`, `0` (fit all) and
`f` (fit fork) do the same with the timeline focused. A zoomed timeline shows its range at the bottom right; clicking it seeks to the step under the pointer. `Fit fork`
spans from a little before the fork step to a little after the end of the fork's recording, so a five-step fork in a ten-thousand-step history takes the whole width.
Zoom is a view setting: it is not saved in a session and never touches a world.

### Framing

A new view, `view … frame` and the **Frame** button place the camera on what the view draws with its own overlays, leaving out static and sensor entities and drawings
that belong to no entity (a floor, a trigger zone, bounds) while anything else is drawn. A module that tags nothing (draw items with no entity) is framed without
items much larger than the typical one (eight times the median extent), which drops a ground plane. **Frame sel.** (`frameSelection`) frames the selected entity and
falls back to the same default when the view draws nothing for it.

## Pictures

The shell draws the panels with onegame's UI, the 2D views with the 2D renderer's canvas (rotated, rounded rectangles clipped to the view) and
the 3D views with the 3D renderer: solids become one lit mesh, lines, points, arrows and wireframes are the renderer's debug lines, the ground
grid is labelled in the source's length unit. A 3D view is rendered offscreen and placed in the layout by the 2D renderer, and the labels and
the timeline are painted over everything. Forks hang below the time axis as branches: a line from the step they were forked at to the end of their recording,
joined to the time axis, with a marker at the divergence step. Captures of the same state are byte-identical run to run on the same renderer; the committed goldens
in `tests/OneScope.App.Tests/goldens` come from lavapipe (`VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json`). Triangles and meshes in a 2D view
are filled by one-pixel strips because the canvas has rectangles and rounded rectangles only.

## Bundled modules

The app carries the OnePhysics module (`RecursiveAI.OnePhysics.Scope`) and offers it first to every file, so `onescope open run.oppr` works with no
`--module`. It opens OnePhysics replays, world snapshots and scene text, 2D and 3D (units: metres). Entities are the world, each body and each joint;
overlays are shapes (coloured by state: static, kinematic, awake, asleep, sensor), bounds, contacts, joints and centres of mass; the pokes are `impulse`,
`set_velocity`, `set_position` and `wake` on a body. A replay played here gives the library's own world hash after every step. `testdata/physics-*` holds
small inputs written by `tools/GenPhysicsData` with the library's recorder, each replay with its per-step hashes (`.hashes`).

Until the module package is on nuget.org, restore needs the folder that holds it: set `ONESCOPE_FEEDS` to that folder (several are separated by `;`)
before `dotnet restore`. The module was built against the first preview of the SDK; `DrawStyle` keeps its old four-argument constructor so such a build still loads.
