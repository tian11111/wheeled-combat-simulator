# Task One Review Evidence

## Verified

- Clean `main` worktree before review.
- Project-level build succeeded with one MSBuild node and shared compilation disabled.
- `Sim.Tests`: 89 passed, 0 failed, 0 skipped.
- Seed 42 default match completed at 2400 ticks.
- Python controller process completed with zero bridge faults.
- Replay record/check reproduced 752 event fingerprints and score 4:49.
- `Sim.Core` contains no Godot dependency; `SnapshotView` is covered by pure tests.

## Findings

### P1: configured match duration is ignored

- `src/Sim.Cli/Program.cs` writes `--duration` into `Scenario.Field.MatchDuration`.
- `src/Sim.Core/MatchEngine.cs:54` initializes `_matchTimer = 120`.
- `src/Sim.Core/RuntimeState.cs:82` initializes each FSM timer to 120.
- Running `match --seed 42 --duration 3` still produced 2400 ticks and a 120-second completion.
- No test references `MatchDuration` or exercises CLI duration behavior.

### P1: desktop acceptance is not complete

- `godot/README.md` labels the client an uncompiled scaffold.
- `godot/src/Main.cs` and `ArenaVisualizer.cs` are explicitly unverified.
- The local machine has `.NET` but no detected Godot executable.
- The scene contains empty robot nodes and no arena geometry or replay controls.
- Archived task completion table leaves Godot items 6, 7, and part of 9 blocked.

## Recommended Next Task

Complete the Godot desktop client and cross-end verification, with the duration fix as the first prerequisite. This closes the only unmet product-facing acceptance criteria from the architecture milestone without broadening into calibrated 3D physics or tournament management.
