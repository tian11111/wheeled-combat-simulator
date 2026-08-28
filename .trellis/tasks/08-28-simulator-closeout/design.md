# 仿真器交付收尾技术设计

## Boundary

This is an integration task over four independently verifiable child tasks. Existing `Sim.Core` rules, replay semantics, official scenarios, and the MBri raw data directory remain authoritative. The closeout work adds evidence, validation, and narrowly scoped fixes; it does not replace the simulation kernel.

## Workstreams

1. `sensor-calibration-drift-resolution` uses the existing sensor evidence importer and replay evaluator to separate batch/semantic drift from runtime configuration. Its default output is evidence-only.
2. `real-telemetry-fidelity-promotion` uses the existing `telemetry-v1` contract and `Sim.Calibration` pipeline. A promotion is allowed only for real source data with a passing holdout gate; otherwise the result is an explicit no-promotion report.
3. `godot-render-smoke-closeout` keeps `Sim.Core` authoritative and separates logic/headless evidence from renderer-backed screenshot evidence. Any smoke-mode code change must preserve the existing parity contract.
4. `github-sync-closeout` runs last, after local checks. It synchronizes the reviewed `main` history without force-pushing or hiding a remote divergence.

## Data Flow

```text
MBri files + selection manifest
        → sensor evidence/drift report → optional user-approved profile

telemetry-v1 input
        → fit/holdout report → optional fidelity/profile promotion

Godot editor actions
        → Scenario/layout snapshot → MatchEngine parity + renderer screenshot

validated local HEAD
        → remote ref comparison → origin/main
```

## Compatibility

- Preserve `sim-replay-v1`, `sensor-calibration-v1`, `telemetry-v1`, official scenarios, and seed-42 event fingerprints.
- Never let a visual model, screenshot, or Godot physics result enter scoring, collision, replay, or sensor authority.
- Keep `fidelity.json` statuses truthful. Evidence reports may be complete while runtime promotion remains blocked.

## Rollback

- Sensor and calibration changes must be isolated from runtime defaults until their reports and regression tests pass.
- Godot smoke fixes must be revertible without touching `Sim.Core` behavior.
- GitHub synchronization stops on a remote divergence and never uses force push.
