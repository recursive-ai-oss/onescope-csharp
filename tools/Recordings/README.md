# Recordings

The clips in `docs/media` are frames of the released `onescope` tool, captured headless on a discrete GPU and encoded afterwards. Nothing
here is part of the product; it needs Python 3, Pillow (`cli_frames.py` only) and an ffmpeg with libx264 and freetype for encoding.

1. Install the tool: `dotnet tool install RecursiveAI.OneScope.Tool --version 1.0.0 --tool-path <dir>` and point `ONESCOPE` at it.
2. Build `samples/PhysicsHost`, `tests/OneScope.TestModule` and `tests/OneScope.TestModule.RuleB` (the test modules with `-p:Version=1.0.0`, so
   they match the tool's SDK), and run `GenShowcase` to write `showcase.oppr`. Set `OSREC_BIN` to the build output folder that holds
   `<Project>/Release/net10.0` and `OSREC_DATA` to the folder with `showcase.oppr`.
3. Pin the device with `ONEGAME_GPU_ADAPTER=discrete` and run `python3 tools/Recordings/record.py <physics|live|compare|nav|cli>` from the repo root.
   Frames land in `frames/<clip>`; the last frame's JSON names the renderer.
4. `python3 tools/Recordings/encode.py frames <clip> <name>` writes `docs/media/<name>.mp4` and `.gif`; `cli_frames.py frames cli` lays out the
   command-line clip.

`oscope.py` is a small MCP client for the shell's live socket. The live clip paces itself on the host's steps, so its frames differ a little
from run to run; the others are deterministic.
