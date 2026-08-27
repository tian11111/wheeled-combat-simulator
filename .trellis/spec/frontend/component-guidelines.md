# Component Guidelines

Godot scenes/nodes are the UI components; the data contract is `RenderFrame`.
`ArenaVisualizer` owns Godot nodes and input. It consumes the immutable output
of `SnapshotView.From`/`Lerp` rather than recomputing physics, scoring, or sensor
rules locally.

Prefer typed records (`RobotVisual`, `BlockVisual`, `HudState`) over dictionaries
for data passed between shell helpers. Keep node lookup and visual updates in
the visualizer. Inputs map to referee/session commands and remain separate from
rendering; parity behavior must be testable headlessly through `ParityCheck`.

Common mistake: adding local score or collision logic to a visual node. The
authoritative implementation remains in `Sim.Core`.
