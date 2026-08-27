# State Management

State is split between the authoritative core session and immutable render
projections. `MatchEngine` owns mutable simulation state. `MatchSession` owns
shell lifecycle and replay cursor state. `SnapshotView` creates render data;
visual nodes must not become a second source of truth.

Layout editing uses `LayoutDraft` snapshots plus bounded undo/redo. Scenario and
replay JSON are persisted only through explicit save/load operations. Do not
promote visual convenience state into the engine or alter a running match from
editor state; rebuild a `Scenario` and a new session instead.

There is no server state or cache library. File reads go through
`ProtocolJson.Deserialize` followed by validation. Snapshots are observations,
not commands.
