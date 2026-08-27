# Implementation Plan

## Ordered Checklist

1. Add protocol-level layout version and optional field pose with validation and round-trip tests. Preserve identity defaults and the existing JSON shape.
2. Introduce the pure `FieldTransform` abstraction and unit-test point, vector, heading, inverse, translation, and rotation behavior.
3. Refactor `FieldModel`, robot/block initialization, stage-wall/fence physics, sensors, and snapshot height calculations to consume shared transformed geometry. Verify the identity transform is bit-for-bit equal to existing replay fixtures.
4. Update `scenarios/wushu-ring-2026.json` only with additive canonical fields; add official dimension/start-zone assertions and translated/rotated scenario tests.
5. Refactor `ArenaVisualizer` to build all geometry from `Scenario`: correct black walkway, 0.20m fence, start zones, grayscale platform texture, red center and white `武` asset. Remove duplicated geometry constants.
6. Add a pure `LayoutDraft` plus command history, validation, canonical serialization, and atomic Save As/load behavior. Link Godot-free code into `Sim.Tests` for headless tests.
7. Add Godot edit mode, selection/picking, ground-plane drag, yaw control, snap toggle, numeric inspector, undo/redo, restore official, Open, Save As, and Apply. Disable edits during active matches and replay.
8. Recreate `MatchSession` on Apply and prove CLI/Godot load the saved layout with identical platform, starts and block locations.
9. Add `RobotModelLoader` for imported `res://` scenes and external `.glb/.gltf`, per-role render offsets, error reporting, limits, and primitive fallback.
10. Run desktop visual/interaction QA at 1280x720 and 1920x1080, including rotated field framing, imported model, fallback model, save/reload, and replay mode.
11. Update `README.md`, `godot/README.md`, `docs/ARCHITECTURE.md`, `docs/PORTING_NOTES.md`, scenario documentation, and `fidelity.json` without overstating physical realism.

## Validation Commands

```powershell
dotnet build src/Sim.Tests/Sim.Tests.csproj --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test src/Sim.Tests/Sim.Tests.csproj --no-build --no-restore -m:1
dotnet run --project src/Sim.Cli --no-build -- match --seed 42 --scenario scenarios/wushu-ring-2026.json --duration 3
dotnet run --project src/Sim.Cli --no-build -- replay-record --seed 42 --scenario scenarios/wushu-ring-2026.json --out replays/arena-layout-seed42.json
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/arena-layout-seed42.json
godot --headless --path godot --editor --quit
godot --headless --path godot -- --parity-check ../replays/arena-layout-seed42.json
git diff --check
```

Also run focused tests for identity and rotated transforms, scenario save/reload, editor history, official dimensions, external-model failure fallback, and unchanged legacy fixtures. Use the actual Godot 4.7.2 .NET executable path when `godot` is not on `PATH`.

## Review Gates

- After step 3: identity layout reproduces existing event fingerprints and rotated platform/fence tests pass before any Godot UI work.
- After step 5: screenshot and numeric assertions agree with the PDF dimensions.
- After step 7: save/reload/apply works and no edit command can mutate a running match or replay.
- After step 9: successful import and malformed/missing/oversized-file fallback are both demonstrated.
- Before completion: full .NET gate, Godot headless load, parity check, desktop QA, documentation, and `git diff --check` pass.

## Risky Files And Rollback Points

- `src/Sim.Protocol/Scenario.cs`: additive fields only; rollback if old JSON no longer round-trips.
- `src/Sim.Core/FieldModel.cs` and `Physics.cs`: preserve an identity-transform checkpoint and compare replay fingerprints before proceeding.
- `godot/src/ArenaVisualizer.cs`: geometry must be scenario-driven; do not retain a second set of official constants.
- `godot/src/Main.cs` / `MatchSession.cs`: draft edits must not mutate the active deterministic engine.
- `godot/project.godot` and scene files: keep editor-generated churn scoped and reviewable.

## Handoff Notes

Start by reading `research/rule-and-code-audit.md`, then `prd.md`, `design.md`, and this plan. Do not begin with Godot visuals: the shared coordinate contract and identity compatibility gate are prerequisites for a movable field that remains physically coherent.
