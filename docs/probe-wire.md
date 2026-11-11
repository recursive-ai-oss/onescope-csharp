# Probe wire format (version 1)

What a host program's probe and a viewer say to each other over a local socket. The probe carries the library's own replay bytes without reading them; this file covers only the framing around them. All integers are little endian.

## Where the socket is

A Unix-domain socket named `p<pid>-<n>.sock` in a per-user directory, on every platform (Windows 10 and later support Unix-domain sockets; a named-pipe transport is not used). The directory is, in order: `$ONESCOPE_SOCKET_DIR`; on Unix `$XDG_RUNTIME_DIR/onescope`; otherwise `onescope/probes` under the user's local application data. On Unix the directory is created mode 0700 and tightened if it already exists wider, and the socket is mode 0600, so only the user can connect. Listing the directory and connecting to each socket is how `onescope attach` finds running programs. A socket nobody listens on (the host was killed) is deleted by the listing and reported as removed, never shown as running.

A probe never starts on its own: the host creates it with `ScopeProbe.Attach`. Where sockets are unavailable, or the path is too long, the probe stays inert and the host is not told.

## Handshake (probe to viewer, sent on connect)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `OSPB` |
| 4 | 2 | protocol version, 1 |
| 6 | 2 | flags, 0 |
| 8 | 4 | replay-format version the records use (i32) |
| 12 | 4 | host process id |
| 16 | 2 | module id length, 1 to 256 |
| 18 | 2 | library version length, 0 to 256 |
| 20 | n | module id, UTF-8 |
| 20+n | m | library version, UTF-8 |

The longest handshake is 532 bytes. A reader checks the magic, version, flags and both lengths from the first 20 bytes before reading the strings, rejects invalid UTF-8, and rejects trailing bytes.

## Asking for the stream (viewer to probe)

The viewer sends one byte, `1`. Any other value, or closing without sending, ends the connection and changes nothing in the host, which is what a listing does. This byte is the only thing the host ever reads from a viewer.

## Stream (probe to viewer)

Frames, each `u8 kind, u32 length, length bytes`:

| Kind | Meaning | Limit |
|---|---|---|
| 1 snapshot | the library's snapshot of the whole world; always the first frame | 64 MiB by default |
| 2 record | exactly the bytes the host passed to `Record` | 16 MiB by default |
| 3 end | the host closed its probe; length 0 | |

A reader checks the kind (order included) and the length against its limits before allocating. A closed socket between frames means the host dropped the viewer or died. A closed socket inside a frame is an error.

## What the host guarantees

- `Record` after the host applied a record: the snapshot is taken on the host's thread at the first `Record` or `Poll` after a viewer asks, so it already contains that call's record, which is then not sent. Records after it follow in order. `Poll` lets a viewer attach at step 0, before any record exists.
- One streaming viewer at a time; a second that asks while one is attached is closed.
- With no viewer attached `Record` is one branch. With one, the bytes are copied into a buffer that the viewer's own thread drains. The buffer holds at most 16 MiB by default; a viewer that falls behind, stops reading (a socket write times out after 5 s), disconnects, or sends a record the viewer's limit would refuse is closed. Attaching again starts from a fresh snapshot.
- The probe never throws into the host and never changes the host's world.
