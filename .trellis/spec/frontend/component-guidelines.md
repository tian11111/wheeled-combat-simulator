# Component Guidelines

Godot scenes/nodes are the UI components; the data contract is `RenderFrame`.
`ArenaVisualizer` owns Godot nodes and input. It consumes the immutable output
of `SnapshotView.From`/`Lerp` rather than recomputing physics, scoring, or sensor
rules locally.

Prefer typed records (`RobotVisual`, `BlockVisual`, `HudState`) over dictionaries
for data passed between shell helpers. Keep node lookup and visual updates in
the visualizer. Inputs map to referee/session commands and remain separate from
rendering; parity behavior must be testable headlessly through `ParityCheck`.

`MatchCamera` state is presentation-only: Overview and Top share a ground-plane
focus with clamped distance/height, Top pitches to −90° (straight down, full
field visible), and left-drag pan uses grab semantics — the focus moves
opposite the cursor delta so the grabbed ground point follows the pointer.
Wheel zooms within the same clamps and must not fight `SetFocus` in Follow
mode. Camera pointer handling is gated behind a narrow ownership hook
(`PointerInputEnabled`); Main turns it off while `LayoutEditor.Active` so
editor selection/drag owns the mouse, and consumed events are marked handled.
Never add a second scene transform or duplicated field-size constants here.

Common mistake: adding local score or collision logic to a visual node. The
authoritative implementation remains in `Sim.Core`.
