# The shell and its operations

`onescope` is a onegame program. One process holds the opened sources, the scene views and the UI. Everything the shell does is an
operation, and the operations are the same from the window, the command line and MCP. Results are JSON.

## Running it

```
onescope open <file> [--module <dll>]... [--headless] [--live [path]] [--run "<op> [json]"]... [--size WxH] [--artifacts <dir>]
onescope <operation> [json | --name value ...] --socket <path>     # drive a running shell, prints the operation's JSON
onescope mcp --socket <path>                                       # stdio MCP bridge for an agent
```

`open` loads the file with the first loaded module that accepts it. A module is an assembly built against `RecursiveAI.OneScope.Sdk`;
`--module` loads one from a path into its own load context (the framework, onegame and the SDK are shared with the host).
`--headless` renders offscreen; without it the shell opens a window. `--live` serves the operations on a Unix socket, one JSON-RPC (MCP)
message per line; `onegame mcp --socket <path>` bridges to it as well. Errors are `{"ok":false,"error":{"code","message"}}` with exit code 1.
Exit codes: 0 ok, 1 an operation failed, 2 usage, 4 could not run.

## Time

Time is the step index. `seek` moves every source to the same step (a source shorter than the step stops at its end) and lands on exactly the
state that stepping straight there gives, so the `hash` in a result is the module's world hash at that step. Playback speed (`play`) is
presentation only: it counts presentation frames at 60 per second, never a clock, and only decides which step the next frame shows.
A source's other axes (`axes`) are positions inside one step; `seek` with `axis` and `position` moves a scratch view and leaves the time
cursor and `hash` alone, and reports `viewAxis`, `viewPosition` and `viewHash`.

## Operations

Hashes are 16 lowercase hex digits. Entity ids are the module's integers. Every operation that names a `source` or `view` defaults to the active one.

| Operation | Arguments | Result |
|---|---|---|
| `open` | `path`, `module`?, `moduleAssembly`?, `keyframeInterval`?, `scan`? | the new source: `source`, `name`, `path`, `module`, `dimensions`, `unit {name, metres}`, `steps`, `axes`, `overlays`, `tracks`, `pokes`, `step`, `hash`, `keyframeInterval`, `scannedThrough`, `views` |
| `sources` | | `step`, `steps`, `speed`, `sources[]` (the `open` fields plus `keyframes`, `keyframeBytes`, `findings`) |
| `seek` | `step`, or `axis` + `position` (+ `source`) | `step`, `steps`, `sources[] {source, step, hash, viewAxis?, viewPosition?, viewHash?}` |
| `step` | `delta`? (default 1, negative goes back), `axis`?, `source`? | as `seek`; the step is clamped to the recording |
| `play` | `speed`? (steps per second, 0 pauses, negative reverses), `frames`? (run this many presentation frames now), `loop`? | `speed`, `playing`, `loop`, `frames`, `moved`, `step`, `hashes[]` |
| `entities` | `source`?, `kind`?, `limit`? | `entities[] {id, parent, kind, name}`, `count`, `truncated` |
| `inspect` | `entity`, `source`? | `entity`, `kind`, `name`, `properties[] {group, name, kind, value}`; also selects the entity in the shell |
| `overlay` | `view`?, `overlay`? (name or id), `on`? (toggles when omitted), `wireframe`?, `grid`? | `view`, `kind`, `wireframe`, `grid`, `overlays[] {id, name, on}` |
| `axes` | `source`? | `step`, `axes[] {id, name, length, position}` |
| `findings` | `source`?, `from`?, `to`?, `severity`?, `limit`? | `scannedThrough`, `findings[] {step, code, severity, entities, text, suggestedChange}`, `count`, `truncated` |
| `view` | nothing to list; `add` (`2d`/`3d`), or `index` with `remove`, `source`, `target`, `halfHeight` (2D), `distance`, `yaw`, `pitch` (3D), `frame`, `wireframe`, `grid`, `active` | `active`, `views[]` |
| `layout` | `entities`?, `inspector`?, `timeline`?, `log`? (booleans) | the panel states |
| `capture_frame` | `path`? (inside the output folder), `size`? (`720p`, `1080p`, `WxH`), `scale`?, `view`? (that view alone, full size), `presented`? | `path`, `width`, `height`, `scale`, `sha256`, `renderer`, `target`, `step`, `hashes[]`; over MCP the PNG travels as image content |
| `capture_sequence` | `dir`, `count` (1 to 3600), `stride`? (default 1), `axis`?, `source`?, `size`?, `scale`?, `view`? | `dir`, `frames`, `ended`, `manifest`, `renderer`, `step`; `manifest.json` lists each frame's `step`, `position`, `hash` and `sha256` |

onegame's own `info`, `ui_tree`, `ui_query`, `ui_click`, `ui_hit`, `ui_scale`, `input_pointer`, `input_key`, `input_text`, `window`, `render_graph`
and `quit` are served unchanged (`docs/interfaces/control.md` in onegame).

Error codes: `bad_args` (the message names the argument and what it must be), `not_found` (a file, source, view, entity or overlay),
`no_source`, `no_module`, `bad_format` (the file is not one any loaded module opens; the message names the file and each module's complaint),
`bad_module`, `too_large`, `path_outside` (an output path leaving the output folder), `rejected`, `unknown_op`.

## Pictures

The shell draws the panels with onegame's UI, the 2D views with the 2D renderer's canvas (rotated, rounded rectangles clipped to the view) and
the 3D views with the 3D renderer: solids become one lit mesh, lines, points, arrows and wireframes are the renderer's debug lines, the ground
grid is labelled in the source's length unit. A 3D view is rendered offscreen and placed in the layout by the 2D renderer, and the labels and
the timeline are painted over everything. Captures of the same state are byte-identical run to run on the same renderer; the committed goldens
in `tests/OneScope.App.Tests/goldens` come from lavapipe (`VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json`). Triangles and meshes in a 2D view
are filled by one-pixel strips because the canvas has rectangles and rounded rectangles only.
