# Component Guidelines

Godot scenes/nodes are the UI components; the data contract is `RenderFrame`.
`ArenaVisualizer` owns Godot nodes and input. It consumes the immutable output
of `SnapshotView.From`/`Lerp` rather than recomputing physics, scoring, or sensor
rules locally.

Prefer typed records (`RobotVisual`, `BlockVisual`, `HudState`) over dictionaries
for data passed between shell helpers. Keep node lookup and visual updates in
the visualizer. Inputs map to referee/session commands and remain separate from
rendering; parity behavior must be testable headlessly through `ParityCheck`.

`MatchCamera` state is presentation-only: Overview and Follow orbit a ground
focus via left-drag (yaw +0.3°/px, pitch clamped 10°–85°; Top spins in-plane
at fixed −90° pitch), right-drag pans with grab semantics — the grabbed ground
point follows the pointer so the focus moves opposite the cursor's ground
delta (Follow focus stays frame-driven) — and wheel zooms within clamped
distance/height (×0.3–3 overview, ×0.5–3 top, ×0.5–2.5 follow). `F5`/arena
rebuild resets the orbit angles to the default framing. Camera pointer
handling is gated behind a narrow ownership hook (`PointerInputEnabled`);
Main turns it off while `LayoutEditor.Active` so editor selection/drag owns
the mouse, and consumed events are marked handled. Never add a second scene
transform or duplicated field-size constants here.

Common mistake: adding local score or collision logic to a visual node. The
authoritative implementation remains in `Sim.Core`.
