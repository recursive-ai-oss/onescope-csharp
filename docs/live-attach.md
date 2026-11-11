# Live attach

A viewer connects to a probe (see `probe-wire.md`) and turns its stream into a source that the history re-simulates locally.

1. `ProbeFinder.List` lists the running probes: socket path, module id, library version, format version and process id.
2. `LiveSession.Connect` connects, reads the handshake, asks for the stream and reads the snapshot. It finds the loaded module whose id the handshake names; that module must implement `ILiveModule`.
3. `ILiveModule.OpenLive` gets the library version, the format version and the snapshot, and returns an `ILiveSource`: a source whose world starts at the snapshot and whose recording is writable, plus `BaseStep`, the host's step at the snapshot.
4. A reader thread receives frames into a bounded queue. `LiveSession.Pump`, on the thread that owns the history, hands each record to `ILiveSource.AppendRecord`, which appends to the recording. The history sees new steps as they arrive. If the viewer stops pumping the queue fills, the reader waits, and the host's probe drops the viewer.

Time in the viewer starts at the attach: the viewer's step `j` is the host's step `BaseStep + j`. The world reports step 0 at the snapshot but hashes with the host's own step, so the viewer's hash at `j` equals the host's hash at `BaseStep + j` bit for bit. Seeking, forking and diffing work as for a file. Saving a live source writes a replay that starts from the snapshot, so a session reopens to the same hashes.

When the host disconnects the viewer, closes its probe, or dies, `LiveSession.State` says which (`Disconnected`, `Ended`) and everything already received stays usable. Attaching again makes a new session from a fresh snapshot.
