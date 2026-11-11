# Time, history and forks

Time is a step index. Nothing in the SDK reads a clock; playback speed belongs to the viewer.

## Recording and worlds

A `Recording` is the module's setup bytes plus one opaque input per step. It has a `StartStep`: 0 for a replay from the beginning, or the host's step for a live attach, whose setup is the host's snapshot. The world at step k is the setup with the inputs for steps `StartStep` to k-1 applied in order, so every world is a pure function of the recording. `InputAt`, `Fork` and `FirstDifference` take step numbers, not positions, so a history that starts at step 1502 shows step 1502 first and its hashes line up with the host's by step number. A module that writes a replay must write its start step (the test module takes it from the start snapshot).

One input holds at most `Recording.MaxInputBytes` (16 MiB, the probe's largest record). A module reading a file must still bound the step count and total size it accepts before allocating.

A recording may carry `ExpectedHashes`, the per-step world hashes its file recorded: entry i is the hash at step `StartStep` + i. The first time `History` plays a step with an expected hash it compares; on a difference it raises one `replay.hash-mismatch` error finding at that step and sets `FirstHashMismatch`. A replay that does not reproduce is then visible on the timeline and in the event log. Forks carry no expected hashes. A step's input is the concatenation of self-delimiting encoded pokes (empty on most steps). Only the module interprets the bytes.

## Keyframes and seeking

`History` keeps a snapshot of its start step and of every N-th step it plays through (`KeyframeInterval`, default 64). `Seek(k)` restores the nearest keyframe at or before k (or just steps on when the cursor is already between that keyframe and k) and re-simulates. The result has the same `StateHash` as stepping straight to k, whether the cursor moved forward, backward, randomly, or after keyframes were evicted. Keyframes are created as steps are played, so the first visit to a stretch costs its length; later seeks cost one restore plus fewer than N steps.

Memory is bounded by `MaxKeyframes` (default 1024). Beyond it, the interior keyframe whose neighbours are closest together is dropped. The start step and the newest keyframe stay. `CurrentStep` runs from `StartStep` to `EndStep` (`StepCount` is `EndStep` - `StartStep`).

Timeline tracks and findings are recorded the first time a step is run and ignored when it is re-simulated. `Scan()` plays through the recording once so the whole timeline is filled.

## Axes

A source declares the axes it can be stepped along. `time` is always one. Another axis has positions 0 to P inside one step (a build stage, a solver iteration). `Seek(axis, position)` leaves the time cursor and `StateHash` alone: the view is a scratch world, restored from the cursor's snapshot and run partway through the step that follows. The same (step, position) gives the same hash by any path.

## Forks

`History.Fork(step)` creates a `Branch` with its own recording (the original's inputs before `step`), its own source and history, seeded with the original's keyframes and timeline data up to `step`. `Branch.Poke` queues pokes; `Branch.Step` runs a step with them as its input and appends that input to the branch's recording. Writing the branch's source gives a replay that reproduces the branch. The original recording, history and world are never touched.

`Branch.DivergenceStep` is the first step whose recorded input differs from the original's. The two worlds are equal through that step and may differ from the next one.

## Diff

`IScopeWorld.FirstDifference` returns the first differing entity and field, in the module's fixed order, or null. `History.Diff(other, step)` moves both histories to the step and compares.

## What a fork's pokes can carry

A module declares each poke with its parameters (`PokeInfo`). A poke with `PokeRole.DragToPoint` takes an entity and a vector and is the one the app calls when an entity is dragged with the pointer. Draw items carry the entity they show (`DrawStyle.Entity`), which is how the app finds the entity under the pointer and outlines the selected one.
