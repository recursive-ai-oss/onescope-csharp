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

Hashes are 16 lowercase hex digits. Entity ids are the module's unsigned 64-bit integers; an argument that takes one accepts the JSON integer or the same digits as a string (some JSON clients read numbers as doubles, which cannot hold every id). Every operation that names a `source` or `view` defaults to the active one.

| Operation | Arguments | Result |
|---|---|---|
| `open` | `path`, `module`?, `moduleAssembly`?, `keyframeInterval`?, `scan`? (a module named by `moduleAssembly` opens this file even when a bundled one would also accept it; the source then reports `moduleAssembly`) | the new source: `source`, `name`, `path`, `module`, `dimensions`, `unit {name, metres}`, `steps`, `axes`, `overlays`, `tracks`, `pokes`, `step`, `hash`, `keyframeInterval`, `scannedThrough`, `views` |
| `sources` | | `step`, `start`, `steps`, `speed`, `sources[]` (the `open` fields plus `keyframes`, `keyframeBytes`, `findings`, and for a live source `live {state: streaming|ended|disconnected|failed|capped|closed, endedBecause?, follow, hostPid, library, format, firstStep, newestStep, recordBytes, capBytes}`, and when the file recorded hashes `recordedHashes {through, matches: all|so far|no, firstMismatch?}`), and `attaching[]`: attaches still connecting or failed (`attach`, `state`, `target`, `startFrame`, `frame`, and for a failure `errorCode`, `error`) |
| `attach` | none: list. `pid` or `socket` to connect; `dir`?, `keyframeInterval`?, `moduleAssembly`?, `wait`? (ms the call may block, default 100), `timeout`? (ms the connection may take, default 30000), `capBytes`? (received record bytes kept, default 268435456) | list: `probes[] {pid, socket, module, library, format, moduleLoaded}`, `removedStale[]`, `unreadable[]`. Connect: the new source as for `open`, plus `live`, when it is ready within `wait`; otherwise `{attach, state: "connecting", target, startFrame, frame}` and the source appears when the host has taken its snapshot. Errors within `wait`: `not_found`, `timeout`, `bad_stream`, `no_module`, `bad_format`; later ones are listed under `sources.attaching` |
| `follow` | `source`? (default the first live one), `on`? | `source`, `live`, `step`; on moves the cursor to the newest step |
| `session_save` | `path` (inside the output folder), `embed`? (embed opened files too) | `path`, `bytes`, `sha256`, `cursor`, `sources[] {source, name, kind: referenced\|embedded, live, parent?, forkStep?, bytes}`, `layoutBytes` |
| `session_open` | `path`, `moduleAssembly`? | `path`, `step`, `views`, `sources[] {source, label, module, startStep, steps, step, hash, parent?, forkStep?, divergence?}`. Replaces everything open; nothing changes on failure (`not_found`, `bad_session`) |
| `seek` | `step`, or `axis` + `position` (+ `source`) | `step`, `steps`, `sources[] {source, step, hash, viewAxis?, viewPosition?, viewHash?}` |
| `step` | `delta`? (default 1, negative goes back), `axis`?, `source`? | as `seek`; the step is clamped to the recording |
| `play` | `speed`? (steps per second, 0 pauses, negative reverses), `frames`? (run this many presentation frames now), `loop`? | `speed`, `playing`, `loop`, `frames`, `moved`, `step`, `hashes[]` |
| `offset` | `source`?, `offset`? (integer; omit to report) | `source`, `offset`, `step` (shared), `sourceStep`, `start`, `steps`, `hash`. At shared step s the source shows its own step s + offset |
| `close` | `source`? (default the active one) | `closed[] {source, label, fork}` (the source and every fork made from it, by their old numbers), `renumbered[] {from, to}`, `sources`, `active` (-1 when none are left), `views`, `step` |
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
| `layout` | `entities`?, `inspector`?, `timeline`?, `log`?, `compare`? (booleans) | the panel states |
| `compare` | `source`?, `other`?, `kind`? (`fork`, `versions`, `shared`), `map`? (`identity`, `links`), `offset`?, `step`?, `goto`? (`first`, `next`, `previous`, `last`), `limit`? (1 to 1000, default 50) | the pair, the agreement along the shared steps, and the differing fields at a step: see Compare |
| `capture_frame` | `path`? (inside the output folder), `size`? (`720p`, `1080p`, `WxH`), `scale`?, `view`? (that view alone, full size), `presented`? (the window's own frame; error `presented_unavailable` with the reason when the window cannot give one: minimized, covered, no swapchain image, surface lost; `rejected` when the shell is headless) | `path`, `width`, `height`, `scale`, `sha256`, `renderer`, `target`, `step`, `hashes[]`; over MCP the PNG travels as image content |
| `capture_sequence` | `dir`, `count` (1 to 3600), `stride`? (default 1), `axis`?, `source`?, `size`?, `scale`?, `view`? | `dir`, `frames`, `ended`, `manifest`, `renderer`, `step`; `manifest.json` lists each frame's `step`, `position`, `hash` and `sha256` |

onegame's own `info`, `ui_tree`, `ui_query`, `ui_click`, `ui_hit`, `ui_scale`, `input_pointer`, `input_key`, `input_text`, `window`, `render_graph`
and `quit` are served unchanged (`docs/interfaces/control.md` in onegame).

## Sources that number their steps differently

Two runs of one replay can count differently (one library version starts at step 0, another at step 1). `offset` says how far a source's own step numbers run
ahead of the shared cursor, so both views show the same moment; `compare` sets it for its pair. The shared axis then spans every source in shared numbers (it can start below 0).
`sources` and `seek` report a non-zero offset next to the source's own step, and a fork inherits its parent's.

```
$ onescope offset '{"source":1,"offset":5}' --socket /run/user/1000/os.sock
{"source":1,"offset":5,"step":105,"sourceStep":110,"start":-5,"steps":10000,"hash":"dad4cab43acbca19"}
$ onescope seek '{"step":100}' --socket /run/user/1000/os.sock
{"step":100,"start":-5,"steps":10000,"sources":[{"source":0,"step":100,"hash":"42065a69e337bef3"},{"source":1,"step":105,"offset":5,"hash":"dea099ed4b87dfcc"}]}
```

`close` removes a source and the forks made from it; the sources that remain keep their order and are numbered again from 0. Views of the closed sources go with them,
and a compare pair that named one is forgotten. A live source's connection is dropped and the program is not told anything.

```
$ onescope close '{"source":1}' --socket /run/user/1000/os.sock
{"closed":[{"source":1,"label":"run","fork":false},{"source":3,"label":"run fork 1 @50","fork":true}],"renumbered":[{"from":2,"to":1}],"sources":2,"active":0,"views":4,"step":50}
```

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

The shell is laid out for windows from 960x540: the scene views take what the timeline and the log leave, and those two give up height first (down to a floor). The toolbar buttons are short,
the **Pokes** panel can be collapsed to its title (**[-]**) and scrolls when the column is short, and a 2D view shows at least its framed area in both directions, so a narrow view is not cut.

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
grid is labelled in the source's length unit. A 3D view is rendered by the 3D renderer into an offscreen image that the 2D renderer samples directly (the picture never leaves the GPU), and and the labels and
the timeline are painted over everything. Forks hang below the time axis as branches: a line from the step they were forked at to the end of their recording,
joined to the time axis, with a marker at the divergence step. Captures of the same state are byte-identical run to run on the same renderer; the committed goldens
in `tests/OneScope.App.Tests/goldens` come from lavapipe (`VK_DRIVER_FILES=/usr/share/vulkan/icd.d/lvp_icd.json`). Which graphics device a process uses is onegame's pick, in a fixed order, and `ONEGAME_GPU_ADAPTER` (`software`, `discrete`, `integrated` or part of a device name) pins it; the tests pin `software` on Linux and Windows unless it is set. The goldens match exactly on the llvmpipe build that drew them and within one step per channel on another. Triangles and meshes in a 2D view
are filled by one-pixel strips because the canvas has rectangles and rounded rectangles only.

## Compare

`compare` looks at two sources at once and makes them the **compare pair**: the **Compare** panel, the *agree* row under the timeline's time axis and the outline of the
selected entity in both sources' views follow it. Three kinds of pair, chosen from the sources (or `kind`):

- `fork`: a fork and its original (the default for a fork), or any two sources on one module copy. The first difference is the module's own finder's.
- `versions`: two copies of one module id, each in its own load context. Open one file twice, each time with `moduleAssembly` naming a folder's module dll; the shell pairs
  the two sources by itself. The worlds are compared through their inspector rows, since a module can't be handed a world made by another copy of itself. If the two copies
  number the replay differently (one starts at step 0, the other at 1) and play the same number of steps, the pair aligns their starts: `offset` is how far the second source's
  step numbers run ahead of the first's, steps are reported in the first source's numbering, and a row that only shows each world's own step number is not a difference.
  Pass `offset` to set it. The shared cursor still puts both sources at one step number.
- `shared` (`kind:"shared"`): only the entities both sides hold (a partial world against a whole one), paired by `map` (`identity`, or the declared `links`); roots are left out.
  Entities held by one side are counted (`onlySource`, `onlyOther`), not listed.

The result: `kind`, `step` and `otherStep`, `offset`, `equal` and the two world hashes at the step, `divergence` (the first step whose input differs; forks), and
`agreement {basis, scannedThrough, complete, divergentSteps, firstDivergent, lastDivergent, nextDivergent, previousDivergent, divergentHere, runs, runsTruncated}`: the pair is played
privately from the start of the shared steps (up to 100,000; 10,000 for `shared`) and a step *agrees* when the world hashes are equal (for `shared`, when the shared entities are).
`difference` is the first differing entity and field; `rows[] {entity, otherEntity?, field, source, other}` lists every differing inspector row at the step, at most `limit`,
with the full number in `rowCount` (`truncated` says when the list is cut). With `goto`, the shared cursor first moves to the first, last, next or previous divergent step
(`moved` says whether there was one). `diff` between two copies of one module id works as well, by the same inspector rows.

In the window, the **Compare** button shows a panel with the pair, whether the worlds agree at the cursor, **|< First / < Prev / Next > / Last >|**, and the differing fields;
choosing a row selects that entity. The panel shows by itself for a fork, and when `compare` is called or one file is opened through two module copies.

## Every operation

`open`, `attach`, `follow`, `session_save`, `session_open`, `sources`, `seek`, `step`, `play`, `offset`, `close`, `timeline`, `entities`, `inspect`, `overlay`, `axes`,
`findings`, `fork`, `poke`, `diff`, `compare`, `replay_save`, `pick`, `project`, `view`, `layout`, `capture_frame`, `capture_sequence` are in the table above.
`field`, `links`, `selection`, `track`, `ghost`, `open_real` and `diff_shared` are in `fields-links-ghosts.md`. `info`, `ui_tree`, `ui_query`, `ui_click`, `ui_hit`, `ui_scale`, `input_pointer`,
`input_key`, `input_text`, `window`, `render_graph` and `quit` are onegame's control operations, forwarded unchanged. `onescope --help` prints the same list, and a test keeps it equal to what the shell registers.

## Bundled modules

The app carries the OnePhysics module (`RecursiveAI.OnePhysics.Scope`) and offers it first to every file, so `onescope open run.oppr` works with no
`--module`. It opens OnePhysics replays, world snapshots and scene text, 2D and 3D (units: metres). Entities are the world, each body and each joint;
overlays are shapes (coloured by state: static, kinematic, awake, asleep, sensor), bounds, contacts, joints and centres of mass; the pokes are `impulse`,
`set_velocity`, `set_position` and `wake` on a body. A replay played here gives the library's own world hash after every step. `testdata/physics-*` holds
small inputs written by `tools/GenPhysicsData` with the library's recorder, each replay with its per-step hashes (`.hashes`).

The app also carries the OnePath module (`RecursiveAI.OnePath.Scope`), second in line. It opens crowd replays (`.oprp`, 2D when the mesh is flat, otherwise 3D),
navigation-mesh build inputs (`.opbi`) and grid runs (`.opgr`). A build input has one empty time step and a `stage` axis of seven positions (voxels, filtered spans,
compact spans, regions, contours, polygons, detail); a grid run has an `iteration` axis inside each step, the pops of the next slice of an A\*, JPS or flow-field
search. Entities are the crowd, its agents and the mesh polygons; the stages; the grid's cells and its start and goal. The pokes are `set_target` (the drag
poke: drag an agent), `add_agent`, `remove_agent`, `stop_agent` and `teleport_agent` on a crowd; `move_goal` (the drag poke), `move_start`, `set_wall` and
`set_area` on a grid. `testdata/path-*` and `testdata/combo-*` come from `tools/GenPathData`, each crowd replay with the crowd's per-step hashes (`.hashes`);
`samples/CrowdHost` is a program with a probe whose crowd the shell attaches to. The `combo-physics` and `combo-crowd` pair is one scenario run by both libraries,
for a session that holds a physics world and a crowd on one time axis.

The module restores from nuget.org; to try an unpublished build of it, set `ONESCOPE_FEEDS` to the folder that holds it (several are separated by `;`)
before `dotnet restore`. `DrawStyle` keeps its four-argument constructor so modules built against early previews of the SDK still load.
