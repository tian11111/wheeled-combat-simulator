# Implementation Plan

## Ordered Checklist

1. Kernel: promote mount gate to `SimParameters.MountVMin` (0.3) / `MountAngleMax` (0.26),
   name-mapped `MOUNT_V_MIN` / `MOUNT_ANGLE_MAX`, range-validated; `PhysicsWorld` reads
   `_params` at the exact use sites. Add parameter-validation + defaults tests.
   **Gate: existing replay baselines stay bit-for-bit (full `dotnet test` + CLI
   `replay-check` + Godot `--parity-check`).** Rollback: revert two reads.
2. Protocol: `telemetry-v1` DTOs (`TelemetryFile`/`TelemetryTrial`/per-kind payload records) +
   strict one-shot `Validate()` collecting indexed errors; `ProtocolVersion.TelemetryFormat`.
   Round-trip + invalid-fixture tests.
3. `Sim.Calibration` project (pure): shared constants (`Gravity 9.81`,
   `BlockLinearDamping 2.2` — asserted equal to kernel usage, minimum counts,
   `CalibrationTargets`). Port fitters with numeric-equivalence unit tests using hand-built
   sequences (exponential, block, restitution, stall incl. tie-break and insufficient-sample
   paths).
4. Telemetry decomposition layer: frame-interval velocity derivation, idle-command filtering,
   collision normal/impact extraction (explicit normal | wall tag | robot-opponent geometry),
   mount trial approach extraction — each with invalid-data tests (AC1 cases).
5. Mount evaluator: predict `vn > VMin && |vt| ≤ vn·tan(AngleMax)` per trial, bucketed
   confusion matrix (4 speed × 4 angle bins, coverage rule), insufficiency reporting (no fit).
6. Report model + canonical serialization: fit/holdout columns, per-subsystem
   `eligible`/`reason`, `contentSha256` excluding `generatedAt`, input SHA-256, tool version,
   trial counts, limitations list. Determinism tests (two runs byte-equal).
7. Patch application: `recommendedPatch` builder (eligible-only), `--emit-scenario` composing
   onto `--base-scenario` (default official), `--apply-roles`, `Scenario.Validate()` before
   atomic write; tests: emitted scenario loads on engine, old fixtures untouched.
8. CLI `calibrate` command wiring (flags per design, exit codes, overwrite guard); in-process
   `Program.Main` tests. `--update-fidelity` promotion logic incl. synthetic-source refusal.
9. Fixtures: port legacy selftest data → `src/Sim.Tests/fixtures/telemetry-synthetic-v1.json`
   (+ holdout sets + mount trials, both outcomes); `telemetry-invalid-*` set; a
   `source:"real"` copy for promotion + end-to-end. AC2 numbers asserted at test level.
10. End-to-end test: synthetic-real fixture → `calibrate --emit-scenario` (temp) →
    `match --scenario` runs → `replay-record`/`replay-check` PASS → shell-side
    `ParityCheck.Verify` PASS → shipped `fidelity.json` byte-unchanged in tests (promotion
    asserted only against temp copy).
11. Templates + docs: `telemetry/README.md` + `telemetry/template.telemetry-v1.json`
    (export columns + minimum counts per kind); `docs/CLI.md` `calibrate` section; README
    quick-start line; `docs/ARCHITECTURE.md` calibration block; `godot/README.md` unchanged
    (loads emitted scenarios via existing `--scenario-path`); fidelity note in docs stays
    `uncalibrated` unless real data was actually processed by the user.

## Validation Commands

```powershell
dotnet build src/Sim.Calibration src/Sim.Cli src/Sim.Tests -m:1 -p:UseSharedCompilation=false
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 -p:UseSharedCompilation=false
# step-1 identity gate (must stay PASS after mount-parameter promotion):
dotnet run --project src/Sim.Cli --no-build -- replay-check src/Sim.Tests/fixtures/godot-parity-seed42.json
godot --headless --path godot -- --parity-check ../src/Sim.Tests/fixtures/godot-parity-seed42.json
# calibrator end-to-end (test fixture path):
dotnet run --project src/Sim.Cli --no-build -- calibrate --input src/Sim.Tests/fixtures/telemetry-synthetic-v1.json --out $env:TEMP\cal.json
# emitted-scenario roundtrip:
dotnet run --project src/Sim.Cli --no-build -- match --scenario $env:TEMP\calibrated.json --seed 42 --duration 3
godot --path godot --quit-after 60 -- --scenario-path <abs path to calibrated.json>
git diff --check
```

## Review Gates

- After step 1: identity replay gate green (bit-for-bit) before any calibration code exists.
- After step 6: report determinism + AC2 legacy-number equivalence demonstrated in tests.
- After step 8: invalid input never yields a patch; synthetic data never yields promotion —
  both proven by tests, not just asserted in code.
- Before completion: full .NET gate, both parity-check commands, end-to-end calibration run,
  fidelity.json untouched, docs honest about "no real telemetry yet" when that's the case.

## Risky Files And Rollback Points

- `src/Sim.Core/Physics.cs` (StageWall hot path): only two constant→parameter reads;
  rollback = revert, parameters additive in SimParameters.
- `src/Sim.Protocol/`: additive telemetry-v1 types only; no existing DTO touched.
- `src/Sim.Cli/Program.cs`: new subcommand branch; existing dispatch untouched.
- `fidelity.json`: must NOT change in this task unless the user supplies real telemetry and
  runs promotion explicitly; tests assert the shipped file is untouched.

## Handoff Notes

Read `research/current-calibration-audit.md`, then `design.md`. Note the audit's
"24-degree gate" wording is imprecise: the code constant is `MountAngle = 0.26` rad (≈14.9°)
— the design follows the code. Do not start with fidelity/CLI plumbing; the kernel parameter
promotion with its identity gate comes first, then protocol, then math.
