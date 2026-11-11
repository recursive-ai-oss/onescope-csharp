# Session file `.oscope` (version 1)

The open sources of a session, the forks with their poke replays, the shared time cursor, and an opaque blob the shell uses for layout and cameras. All integers are little endian. Reading a session and writing it again gives the same bytes.

## Header (24 bytes)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `OSCP` |
| 4 | 2 | version, 1 |
| 6 | 2 | flags, 0 |
| 8 | 8 | time cursor, an absolute step number, 0 or more (i64) |
| 16 | 4 | source count, at most 4096 |
| 20 | 4 | layout length, at most 16 MiB |

## Sources

Source count records follow the header, parents before their forks. Each has a 27-byte fixed part, then three strings, then a payload:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | kind: 0 embedded, 1 referenced |
| 1 | 4 | parent source index, or -1; always an earlier source (i32) |
| 5 | 8 | fork step, an absolute step number of the parent; 0 when there is no parent (i64) |
| 13 | 4 | keyframe interval for the source's history, at least 1 |
| 17 | 2 | module id length, 1 to 4096 |
| 19 | 2 | name length, 0 to 4096 |
| 21 | 2 | path length, 0 to 4096; 0 for embedded, at least 1 for referenced |
| 23 | 4 | payload length |

then the module id, name and path as UTF-8, then the payload:

- **Embedded:** the source's replay in the module's own replay format. A fork is always embedded, because its replay holds the pokes; its parent index and fork step say what it was forked from.
- **Referenced:** a 32-byte SHA-256 of the file as it was when saved. Opening the session reads the file by its path and refuses one whose hash differs.

After the last source come exactly layout-length bytes of layout, then the end of the file. The SDK never interprets them.

## Limits and errors

The reader enforces a file size limit (1 GiB by default), the source and layout limits above, and checks every length against the bytes that remain before allocating. Errors are `ScopeFormatException` naming the input and what was expected at which offset or source.

## Opening

`SessionRestore.Open` opens each source with the module whose id it names, builds a history for each at the saved cursor, and for a fork computes its divergence from its parent's recording. A fork reopens from its own replay, so it reproduces the hashes it had when saved.
