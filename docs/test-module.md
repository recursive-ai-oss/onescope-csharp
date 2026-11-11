# Test module replay format (version 1)

The test module is a small integer simulation: up to 64 bodies bouncing in a 10 m box (lengths in millimetres), with a stage axis (apply input, gravity, move, bookkeeping). All integers are little endian.

```
magic          4 bytes   "OSTM"
version        u16       1
flags          u16       bit 0: hash rows present; other bits must be 0
setup length   u32       6
setup          6 bytes   u8 dimensions (2 or 3), u8 body count (1..64), u32 seed
step count     u32       at most 16 777 216
hash rows      u64 x (step count + 1), only if flag bit 0; row i is the world hash at step i
per step       u16 input length, then that many bytes
```

A step input is a sequence of operations: `1 body dx dy dz` (impulse), `2 body x y z` (teleport), `3 body` (remove), `4 gravity`. `body` is a u8 index, the rest are i32.

Every length is checked against the bytes that remain before anything is allocated; trailing bytes are an error. Errors are `ScopeFormatException`, naming the input and what was expected at which offset.

`testdata/test-10000.ostm` (3D, 24 bodies, 10 000 steps) and `testdata/test-2d-600.ostm` are produced by `tools/GenTestData`.
