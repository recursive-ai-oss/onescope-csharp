# Test module replay format (version 1)

The test module is a small integer simulation: up to 64 bodies bouncing in a 10 m box (lengths in millimetres), with a stage axis (apply input, gravity, move, bookkeeping). All integers are little endian.

```
magic          4 bytes   "OSTM"
version        u16       1
flags          u16       bit 0: hash rows present; bit 1: a start snapshot present; bit 2: a link block follows the setup; other bits must be 0
setup length   u32       6, or 10 when flag bit 2 is set
setup          6 bytes   u8 dimensions (2 or 3), u8 body count (1..64), u32 seed
link block     4 bytes   (only with flag bit 2) u8 role: 1 sends a body away, 2 receives it, 3 neither (declares the density and occupancy fields only); u8 body index (roles 1 and 2); u16 transfer step
step count     u32       at most 16 777 216
start snapshot u32 length, then the world snapshot, only if flag bit 1: the state a live attach began from; its first 8 bytes are the host's step at that moment, which is the recording's start step
hash rows      u64 x (step count + 1), only if flag bit 0; row i is the world hash at the start step + i
per step       u16 input length, then that many bytes
```

A step input is a sequence of operations: `1 body dx dy dz` (impulse), `2 body x y z` (teleport), `3 body` (remove), `4 gravity`. `body` is a u8 index, the rest are i32.

Every length is checked against the bytes that remain before anything is allocated; trailing bytes are an error. Errors are `ScopeFormatException`, naming the input and what was expected at which offset.

A world with a link block also declares the two scalar fields (`density`, per grid vertex, and `occupancy`, per triangle, both over an 8 by 8 floor grid) and their overlays. Role 1 removes the body at the transfer step and role 2 holds it from that step on (it is absent before, and appears at the middle of the box with a fixed velocity); the sender declares the link as `ContinuedBy` and the receiver as `Continues`, with link keys `tm-left` and `tm-right`. `TestGhost` writes a pose cache from a run, with an offset, as a stand-in for a reference engine's dump.

`testdata/test-10000.ostm` (3D, 24 bodies, 10 000 steps) and `testdata/test-2d-600.ostm` are produced by `tools/GenTestData`, as are `test-field-2d.ostm`, `test-field-3d.ostm`, the linked pair `test-link-left.ostm` and `test-link-right.ostm` (body 3 moves at step 150) and the pose cache `test-2d-ghost.ospc` (the 2D replay's bodies for 40 frames from step 200, drawn 400 mm to the right).

## Live stream

The test module is also a live module. Its probe snapshot is the 6-byte setup followed by the world snapshot (a 64-bit host step, gravity, then per body alive, still count, position and velocity as 32-bit integers). Each record is one step's input. A viewer's world starts at the snapshot at the host's step, so the viewer's step numbers are the host's and hashes compare by step number. A live source written as a replay sets flag bit 1 and holds the snapshot, so reopening it reproduces the same hashes.
