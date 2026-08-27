# Technical Design

## Architecture

Keep the existing authority boundary:

```text
Godot UI / Replay View
        |
        v
SnapshotView + typed commands
        |
        v
Sim.Core -> Sim.Protocol

Sim.Cli -> the same Sim.Core and replay schema
```

`Sim.Core` remains the only source of motion, sensors, referee decisions, scores, and events. Godot owns rendering, input, camera behavior, HUD state, and replay navigation.

## Match Duration Fix

Initialize `_matchTimer`, `RobotRuntime.Fsm.Timer`, and the initial snapshot timer from `Scenario.Field.MatchDuration`. Avoid another copied duration constant by passing the configured value through robot creation/reset paths. Add tests for 3 seconds, 120 seconds, invalid durations, and replay behavior.

## Godot Runtime Adapter

Split the current `Main` responsibilities into:

- `MatchSession`: creates/resets `MatchEngine`, runs the fixed-step accumulator, and exposes typed referee commands.
- `ArenaVisualizer`: applies `RenderFrame` transforms and materials only.
- `HudController`: projects scores, timer, phase, robot state/action, and recent events.
- `CameraController`: top, orbit, and follow views; camera changes never touch simulation state.
- `ReplayController`: loads a replay document, exposes play/pause/step/seek, and publishes recorded or deterministically reconstructed snapshots.

Prefer small Godot scripts with exported node references over absolute scene paths. Scene node names and script expectations must be validated at startup with clear errors.

## Scene Composition

Use primitive meshes for the MVP:

- 3.8 m square base and black 0.7 m aisle;
- 2.4 m square platform with a real 0.06 m visual height;
- colored start zones and restrained central marking;
- distinct yellow/blue robot bodies with visible heading and shovel;
- green/neutral buff blocks and red debuff block;
- directional light, world environment, top/orbit/follow cameras.

Godot collision bodies are optional diagnostics and must not drive core state.

## Replay Contract

Extend replay files only additively if snapshot navigation needs sampled frames. Do not reinterpret existing `ReplayHeader.Ticks`: it stores accepted external actions and commands, not every simulation tick. `ReplayController` may reconstruct snapshots from the scenario and recorded inputs once, cache them in memory, and seek through that immutable list.

Cross-end verification uses the same replay file in `Sim.Cli` and Godot headless mode and compares final score, done reason, event fingerprints (`seq|tick|type|cls|msg`), and final snapshot tick/time.

## Error Handling

- Invalid scenario/replay: show a concise in-app error and return a non-zero headless exit code.
- Missing expected scene node: fail startup with the exact node path.
- Godot unavailable: document the prerequisite; do not weaken or skip `.NET` checks.
- Rendering errors must never mutate, reset, or silently advance the core.

## Compatibility

Do not change current protocol field names, enum wire values, Python bridge behavior, legacy fixtures, or `fidelity.json` meanings. Any new replay field must be optional and use the existing serializer conventions.

## Rollback

Land the duration fix independently before Godot changes. Keep Godot edits confined to `godot/` except for additive replay/test support. If graphical integration fails, revert the Godot commit without reverting the validated core duration correction.
