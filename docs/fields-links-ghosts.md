# Fields, links, shared-entity diffs and ghost sources

Four parts of the SDK contract that a module declares as data. Each is optional: a module built before they existed still loads and runs (every addition is a new member with a default).

## Scalar fields

A module declares fields in `SourceInfo.Fields` (`FieldInfo`: id, name, `ColorMap`, min, max, unit) and paints them with `DrawList.FieldMesh` (one value per vertex or per triangle) or `DrawList.FieldPoints` (one value per point). The draw item carries `DrawItem.Field` (1 plus the index into `DrawList.FieldBindings`); a renderer that ignores fields draws the item in its own colour.

- Colour maps are `Viridis`, `Heat`, `CoolWarm` and `Grayscale`; `ColorMaps.Evaluate` is the one function the app, the legend and a module's own tests share. Values outside min..max clamp; NaN is "no data" and draws grey.
- The shell colours a per-vertex mesh by blending across each triangle, a per-triangle mesh flat, and shows a legend (name, unit, colour bar, min and max) in the bottom-right corner of every view that draws a field.
- `IScopeWorld.TryReadField(field, entity, out value)` and `TryReadFieldAt(field, point, out value)` give the same values the module paints. They default to "no value".
- Operation `field`: no `field` lists the source's declared fields; `field` with `entity` or `point` returns `{source, step, field, entity|point, value, unit, min, max}`. Errors: `bad_args` (unknown field, neither or both of entity and point), `no_value`.
- Limits: 1024 field draws and 8M values per list; a wrong value count drops the mesh and counts it in `DrawList.Dropped`.

## Links

A world lists transfers with `IScopeWorld.CollectLinks`: an `EntityLink` says one of its entities continues an entity of another source (`Continues`) or is continued by one (`ContinuedBy`), from a step on. Sources name each other by `SourceInfo.LinkKey` (the name when unset).

- The shell resolves every source's links to (source, entity) pairs and a step. When the shared cursor is at or past the step, a selection of the departing entity moves to the arriving one in the other source; when it is before the step, back again. The active source, the inspector and the entity highlight follow. A link whose other source is not open is ignored; among several sources with a key, an opened file wins over its forks.
- A view with `track` on (operation `track`) re-centres on the selected entity at every seek and switches to the source the selection moved to (the same dimensions only).
- Operations: `links` (the resolved pairs, whether the cursor has crossed each), `selection` (source, entity, name and properties of the selected entity), `track {view, on}`.

## Shared-entity diff

`IScopeWorld.CompareShared(other, map)` compares only the entities both worlds hold. `EntityMap.Identity` pairs equal ids; `EntityMap.FromPairs` pairs explicitly (for example from links). The default implementation (`SharedDiff.Compare`) compares inspector rows (group and name) of each shared entity; root entities (no parent) are skipped because a root usually aggregates the whole world. The result `SharedDifference` has the first difference (in the first world's entity order) and counts: shared, only in the first, only in the second.

Operation `diff_shared {source, other?, step?, map?}` (`other` defaults to a fork's parent; `map` is `identity` or `links`) returns `{source, other, step, map, equal, shared, onlySource, onlyOther, hashSource, hashOther, difference {entity, otherEntity?, field, source, other} | null}`. Entities held by only one side are not compared; they are counted.

## Ghost sources

A pose cache (`pose-cache.md`) opens as a source of the module `oscope.posecache` with `SourceInfo.Ghost` set. It plays through the same history as any source, so it scrubs on the shared time axis; frames before and after its range hold the first and last pose. It has one axis (time), no pokes and no tracks. `fork` on it fails with `ghost_source`; `poke` fails with `read_only`; `diff` and `diff_shared` refuse it.

- Opening a ghost over an existing source of the module the cache names (any module when it names none, same dimensions) draws it over every view of that source, as translucent outlines, and gives it no view of its own. With no such source it gets views like any source.
- `sources` shows `ghost {module, libraryVersion, scene, replay, offer {op: "open_real", args, text, available, path?}}`. `available` is true when the named replay is a plain file name that exists next to the cache.
- Operation `open_real {source}` opens that replay with the named module, moves it to the ghost's cursor, draws the ghost over it and returns the new source (`{source, path, module, ghost, step, hash}`).
- Operation `ghost` lists ghosts and the views they are over; with `source` and `on` it attaches (`view`? names one view) or detaches.
- Ghost positions are drawn as stored; a cache in a different length unit than the source it is drawn over is not rescaled.
