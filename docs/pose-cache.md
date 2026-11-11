# Pose cache (version 1)

A pose cache holds the positions and orientations of a fixed set of things over consecutive steps, with no simulation behind them: a reference engine's pose dump, a baked clip. The shell opens one as a **ghost source** (see `fields-links-ghosts.md`). The reader is `OneScope.Sdk.PoseCache`; a module or a tool can write one with `PoseCache.Create` and `Write`.

All integers and floats are little endian. Frame k is the world at step `start step` + k.

```
magic            4 bytes   "OSPC"
version          u16       1
flags            u16       0 (any other value is an error)
dimensions       u8        2 or 3
reserved         u8 + u16  zero (any other value is an error, so a parsed cache always writes back to the same bytes)
entity count     u32       1 to 65 536
frame count      u32       1 to 4 194 304
start step       i64       0 or more
metres per unit  f64       0 (a unit with no physical size) or a positive finite number
text x 6         u16 length (at most 1024) + UTF-8 bytes, in this order:
                 unit name, module id, library version, scene, replay, label
entities         entity count x:
                   u64 id          not 0, unique
                   u8 shape        0 point, 1 sphere, 2 box, 3 capsule
                   3 bytes         zero
                   f32 x 3         size: sphere radius in x; box half extents; capsule radius in x and half length in y (axis is local Y); finite
                   u16 + bytes     name (at most 1024 bytes of UTF-8)
poses            frame count x entity count x 7 x f32: position x y z, rotation x y z w
```

The poses are the rest of the file: its length must equal frame count x entity count x 28 exactly, so a count cannot claim more than the file holds. A NaN position x means the entity is absent in that frame (the rotation is then ignored); every other value must be finite. A zero rotation reads as the identity.

The origin fields (module id, library version, scene, replay) are free text, each possibly empty. When `replay` is a plain file name (no directory part), the shell looks for it next to the cache and offers to open it with the named module; any other value offers nothing, so a cache can never point the shell at another folder.

Limits: the file is at most 256 MiB. Every length is checked against the bytes that remain before anything is allocated, sizes are computed with checked arithmetic, and every error is a `ScopeFormatException` that names the input, what was expected and the offset. Reading the bytes and writing them again gives the same bytes.
