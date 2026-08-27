# Implementation Plan

## Ordered Checklist

1. Reproduce and fix the scenario duration bug. Replace 120-second runtime initialization constants with `Scenario.Field.MatchDuration`; add core and CLI regression tests.
2. Run the existing `.NET` gate and seed/replay smoke tests before changing Godot.
3. Locate or install Godot 4 .NET, then run the current project in headless editor mode to collect actual C#/scene errors.
4. Correct the Godot project SDK, namespaces, scene tree, exported references, input actions, and project settings until headless load/build succeeds.
5. Build the arena and robot/block visuals from primitive meshes; add stable materials, lighting, camera modes, and responsive HUD layout.
6. Refactor the shell into match session, visualizer, HUD, camera, and replay responsibilities. Route all state changes through typed `Sim.Core` commands.
7. Implement replay load/reconstruct/cache plus playback, pause, single-step, and seek controls using the existing replay schema or optional additive fields.
8. Add Godot headless parity output and an automated cross-end test comparing score, done reason, event fingerprints, and final tick against `Sim.Cli`.
9. Run desktop smoke tests at common window sizes and verify controls, text fit, scene framing, and no overlapping UI.
10. Update README, `godot/README.md`, architecture/CLI docs, porting notes, and task evidence. Remove all stale “unverified scaffold” claims only after the checks pass.

## Validation Commands

```powershell
dotnet build src/Sim.Tests/Sim.Tests.csproj --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test src/Sim.Tests/Sim.Tests.csproj --no-build --no-restore -m:1
dotnet run --project src/Sim.Cli --no-build -- match --seed 42 --duration 3
dotnet run --project src/Sim.Cli --no-build -- match --seed 42 --duration 120
dotnet run --project src/Sim.Cli --no-build -- replay-record --seed 42 --out replays/godot-parity-seed42.json
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
godot --headless --path godot --editor --quit
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json
git diff --check
```

Use the actual Godot 4 .NET executable path if it is not on `PATH`.

## Review Gates

- After step 1: duration tests pass and default 120-second baseline remains stable.
- After step 4: Godot project loads and C# scripts compile before visual work expands.
- After step 7: replay navigation works without introducing rule logic in Godot.
- Before completion: full `.NET` tests, Godot headless validation, cross-end parity, and desktop visual QA all pass.

## Risky Files

- `src/Sim.Core/MatchEngine.cs` and `RuntimeState.cs`: timer initialization affects every match; protect with default and custom-duration tests.
- `src/Sim.Protocol/ReplayHeader.cs`: protocol is additive-only; avoid repurposing `Ticks` or changing JSON names.
- `godot/project.godot`, `GodotSim.csproj`, and `godot/scenes/Main.tscn`: editor-generated changes can be noisy; keep only required, reviewable updates.
- `godot/src/Main.cs`: do not leave simulation, rendering, replay, and input ownership in one script.

## Handoff Notes

The architecture implementation is in commit `28e516e`. Review evidence and the duration reproduction are recorded in this task's `research/task-one-review.md`.
