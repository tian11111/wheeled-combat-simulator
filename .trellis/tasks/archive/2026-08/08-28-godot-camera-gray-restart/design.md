# Technical Design

## Architecture and boundaries

The authoritative restart operation belongs in `Sim.Core`; the Godot shell only routes a referee command and renders snapshots. Camera input stays in `godot/src/MatchCamera.cs`. Field-gray display stays in `godot/src/ArenaVisualizer.cs`, but its numeric source remains `Sim.Core.FieldModel`.

No Godot type, wall-clock API, or file IO may enter `Sim.Core`. No rendering logic may write positions, scores, FSM state, or calibration data back into the core except through the explicit referee command path.

## Restart command and state flow

Add an explicit `RestartRobot(role)` operation instead of overloading the legacy penalty-only `RestartPenalty` behavior used by old replay commands.

1. Validate the target role and active phase. Accept only live `Running` or `Paused` state; reject `Prep`, `Ready`, and `Finished` without score, event, or replay changes.
2. Preserve match timer, both scores, the other robot, and blocks.
3. Convert the target's field-local scenario start pose through `FieldModel.Transform`, then reset world pose, ZG, velocities, command queue, stall/wedge/drop flags, and sensor state.
4. Replace the target `FsmRuntime` with clean sub-state objects while keeping current match elapsed time. Set the robot armed and in `MountRing` so it resumes the mount/recovery flow without extending the match clock. A finished target is revivable while the match is active.
5. Add exactly 4 points to the opponent and increment the restarted role penalty total. Emit one `EventKind.Restart` event and record an additive command `restart_robot:<role>`.

Keep `RestartPenalty` and the old `restart:<role>:<kind>` parser behavior for existing files. Update the new command parser in `Sim.Cli` replay checking, `godot/src/ParityCheck.cs`, and `godot/src/MatchSession.cs`. Do not silently reinterpret old replay bytes.

## Session and scene restoration

Make the session reset scenario represent the current live scenario. Loading a replay should update the reset scenario to `file.Scenario` or provide an explicit reset-to-current-scenario path. Main's F5 handler must call `ApplyScenarioToShell` after `ResetToLive` so the visualizer root and camera are rebuilt from the same scenario as the new engine.

## Camera interaction

`MatchCamera` maintains presentation-only camera state. Overview and Top use a ground-plane focus and clamped distance/height. Left-button drag ray-casts the pointer to the ground plane and translates the focus by the world-space delta. Wheel changes the clamped distance/height. Follow retains automatic midpoint focus and allows zoom without fighting `SetFocus`. Top points downward at `-90` degrees around X.

Expose a narrow ownership hook so Main disables camera pointer handling while `LayoutEditor.Active`. Consumed camera events must be marked handled, and editor selection/drag behavior must remain unchanged. Do not add a second scene transform system or field-size constants.

## Gray rendering

Keep `FieldGrayLocal(x, y)` as the single sample function and keep the current field-local-to-world transform. Extract a small pure mapping helper if needed so tests can assert image-axis orientation and representative values. Use a controlled material/shading path so directional lighting cannot manufacture a diagonal gray band. The center red zone and white `武` remain visual conventions; measured gray-map integration is deferred.

## Compatibility and rollback

The highest-risk change is core state mutation and replay decoding. Land it behind focused tests first, preserving the old penalty method and command parser. If parity fails, disable only the new command route while keeping camera and gray fixes independently revertible. Never alter old fixture JSON or baseline scores to hide a divergence.
