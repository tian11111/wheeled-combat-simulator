# Technical Design

## Architecture

Offline calibration pipeline only; the online determinism contract is untouched:

```text
telemetry.json (telemetry-v1, strict SI)
        │  validate once (Sim.Protocol DTOs)
        ▼
Sim.Calibration (pure, deterministic)      ── fit on `set:"fit"` trials
  validator · fitters · mount evaluation    ── score on `set:"holdout"` trials
        │
        ▼
CalibrationReport (content-fingerprinted) ── recommendedPatch + eligibility flags
        │
   ┌────┴───────────────┐
   ▼                    ▼
--emit-scenario     --update-fidelity
(新场景/profile 文件)  (仅当 holdout 达标且 source=real)
   │
   ▼
Sim.Cli / Godot 共用该场景 (与既有加载路径完全相同)
```

Layers: `Sim.Protocol` owns the telemetry contract DTOs + one-shot validation (same role it
plays for Snapshot/Scenario). New pure class library `Sim.Calibration` owns all fitting and
report math — no IO, no clock in content. `Sim.Cli` gains one `calibrate` command that wires
files to the library and stays the only product entry. The kernel gains explicit mount-gate
parameters but **no calibration code**. Godot is untouched and consumes emitted scenarios
through the existing `--scenario-path`. No Node runtime remains in the loop.

## Telemetry Contract (telemetry-v1)

`ProtocolVersion.TelemetryFormat = "telemetry-v1"`. Root:

```json
{
  "protocolVersion": "v1",
  "schemaVersion": 1,
  "units": { "length": "m", "time": "s", "angle": "rad" },
  "vehicle": { "id": "…", "name": "…" },
  "capture": { "source": "synthetic|real", "date": "YYYY-MM-DD",
               "field": { "id": "…", "condition": "…" }, "notes": "…" },
  "trials": [ … ]
}
```

`units` must equal SI exactly — no inference, no conversion. Trial: `{id, kind, set:
"fit"|"holdout" (default "fit"), …}`. Per-kind payloads, all fields validated at entry with
trial/frame-indexed errors:

| kind | required data | model fitted / evaluated |
| --- | --- | --- |
| `lateral_coast` | frames `{t, robot{x,y,th}, command{v,w}}`, idle-command segments | `latFrictionK` (exponential decay) |
| `angular_coast` | same shape; omega from `th` deltas | `angDamping` |
| `block_push` | frames `{t, block{x,y}}` | `BLOCK_MU_K` (bounded 1-D LSQ) |
| `collision` | `normal{x,y}` (or wall tag / robot-opponent geometry) + `impact{pre,post}` velocities or ≥4 frames with `impactIndex` | `COLLISION_RESTITUTION` |
| `stall` | frames `{t, command.v, speed, stalled:bool}` (boolean label required) | `STALL_SPEED` (threshold classifier) |
| `mount` | per-trial `approach{vn,vt}` + `outcome:bool` (frames optional as evidence) | confusion matrix vs kernel gate formula |

Validation rejects (each as a distinct, collectable error): non-object root, empty `trials`,
non-finite/missing pose or velocity, non-strictly-increasing timestamps, unknown kind, missing
kind-required fields, non-boolean labels, unknown `units`, and — separately as *insufficient
sample* errors — below-minimum usable counts (fit: exponential≥4 pairs, block≥4 pairs,
restitution≥3 samples, stall≥6 with both labels, mount≥12 with both outcomes). On any invalid
input the CLI exits non-zero and produces **no** report and **no** patch (AC1).

## Fitters (port of the validated legacy algorithms)

Numerics are ported from `sim_calibrate.js` (read-only reference), keeping formulas and bounds
identical so legacy synthetic fixtures act as equivalence tests (AC2):

- `FitExponentialDecay(mode)`: consecutive idle pairs, `|speed|>0.02`, same sign, `dt≤0.5`;
  `k = -Σ dt·ln(|v_{i+1}|/|v_i|) / Σ dt²`; fit/holdout RMSE on log-ratio residuals.
- `FitBlockFriction`: model `v' = max(0, v·e^(−2.2·dt) − μ·9.81·dt)` — exactly the kernel's
  `IntegrateBlocks` step (BlockLinearDamping 2.2, gravity 9.81 are shared constants with the
  kernel and documented as such); ternary search 80 iters on `[0.01, 3]`.
- `FitRestitution`: `e = clamp(−Σ b·a / Σ b², 0, 0.9)` over samples with `before>0.05, after≤0`.
- `FitStallThreshold`: candidate thresholds {0, values, midpoints}; squared-error classifier
  loss, ties → lower threshold.
- `EvaluateMount` (new): predicts the kernel's deterministic gate —
  `accepted = vn > MOUNT_V_MIN && |vt| ≤ vn·tan(MOUNT_ANGLE_MAX)` — per trial against
  `outcome`, and emits a confusion matrix bucketed by speed
  (0.3–0.5, 0.5–0.75, 0.75–1.0, ≥1.0 m/s) × angle (≤10°, 10–15°, 15–20°, >20°). No parameter
  is "fitted" from mount data; a mismatch against the agreed target is reported as *model
  insufficiency*, keeping `mount` uncalibrated (AC6).

All values round to 6 decimals for report stability (AC3).

## Train/Holdout Split and Fidelity Eligibility (R3, R6)

Every subsystem is fit on `fit` trials and scored on `holdout` trials of the same kind. The
report always shows both metric columns. Eligibility per subsystem requires **all** of:

1. fit succeeded (minimum counts above);
2. `holdoutSamples ≥ per-kind minimum` (same minimum counts);
3. holdout RMSE ≤ target, or classification metric within target;
4. `capture.source == "real"` — synthetic or self-test data is *never* promotable (its fits
   may pass the numeric gates but `fidelityEligible` carries `reason: "synthetic source"`).

Agreed error targets (constants in `CalibrationTargets.cs`, mirrored in docs):
exponential log-ratio RMSE ≤ 0.05; block/collision speed RMSE ≤ 0.05 m/s; stall
misclassification ≤ 5%; mount overall misclassification ≤ 10% with both outcomes present and
≥2 holdout trials in ≥3 buckets. `--update-fidelity` refuses without eligibility, requires a
`fidelity.json` whose subsystem names match, and records evidence: telemetry SHA-256, report
path, capture date, vehicle/field conditions, limitations (AC5). In the repo's tests this runs
only against a **temp copy** of fidelity.json; the shipped `fidelity.json` keeps
`uncalibrated`/`hand_drawn` statuses unchanged (Open Decision fallback path).

## Determinism and Reporting

Report content = canonical JSON of `CalibrationReport` (protocol camelCase writer) with
`generatedAt` excluded from its own `contentSha256` (computed over the remaining fields, so
the same input always yields the same fingerprint — AC3). Input SHA-256 is taken over the
raw bytes of `--input`. Tool version string embeds `MatchEngine.CoreVersion` +
`calibration-v1`.

## Kernel Change: Explicit Mount Gate (R4)

`SimParameters` gains `MountVMin = 0.3` and `MountAngleMax = 0.26` (parameter names
`MOUNT_V_MIN`, `MOUNT_ANGLE_MAX`), range-validated (0<VMin≤2; 0<AngleMax<1.2). `PhysicsWorld`
replaces its two private constants with `_params` reads at the same use sites (including the
cmdN-elevation branch). Defaults reproduce current behavior; the identity gate for this change
is the existing replay baselines staying bit-for-bit identical (`dotnet test` +
`replay-check` + Godot `--parity-check`). The report's mount evaluator reads the same
parameter values so prediction and simulation can never drift apart.

## Safe Application (R5)

`recommendedPatch` carries only `vehicles` (per-role `latFrictionK`, `angDamping`) and
`parameters` (`BLOCK_MU_K`, `COLLISION_RESTITUTION`, `STALL_SPEED`,
`MOUNT_V_MIN`, `MOUNT_ANGLE_MAX` for eligible subsystems). `--emit-scenario out.json
[--base-scenario path] [--apply-roles us,them]` composes a **new** scenario file with the
patch applied, validates via `Scenario.Validate()`, and atomically writes it — official
`scenarios/wushu-ring-2026.json` and all existing fixtures/replays are never rewritten, so
old replay-check/parity stays bit-identical. The tool never edits code constants.
`--out` refuses to overwrite without `--force`; temp+move for all writes.

## CLI Shape

```
dotnet run --project src/Sim.Cli -- calibrate --input telemetry.json
    [--out calibration/report.json] [--vehicle-id ID]
    [--base-scenario scenarios/wushu-ring-2026.json] [--emit-scenario scenarios/calibrated-bot.json]
    [--apply-roles us,them] [--update-fidelity] [--force]
```

Exit codes: 0 success, 1 validation/fitting/report error, 2 usage. In-process
(`Program.Main`) so `Sim.Tests` covers the same path as `CliTests`.

## Testing and Fixtures

- `src/Sim.Tests/fixtures/telemetry-synthetic-v1.json`: legacy selftest data ported verbatim,
  extended with holdout-split trials per kind and labeled mount trials (both outcomes);
  asserts the AC2 numbers (8 / 3 / 0.45 / 0.33 / STALL∈[0.025,0.07)) within tolerance and
  that synthetic source ⇒ no promotion eligibility.
- `telemetry-invalid-*.json` fixtures: bad units, NaN pose, non-increasing time, unknown kind,
  insufficient samples, non-boolean label — each asserts a specific error and no output file.
- Determinism test: run `calibrate` twice, compare `contentSha256` + fits byte-for-byte.
- End-to-end: `calibrate --emit-scenario` (temp dir) on the synthetic fixture with
  `capture.source="real"` copy ⇒ run CLI `match --scenario <emitted>` a few ticks,
  `replay-record`+`replay-check` reproduce, and Godot-side `ParityCheck.Verify` passes
  (geometry/parameter plumbing); old seed-42 fixture remains PASS untouched.
- Fidelity promotion logic tested against a temp fidelity copy; the shipped file is asserted
  unchanged (statuses stay `uncalibrated`/`hand_drawn`).
- Experiment template: `telemetry/README.md` + `telemetry/template.telemetry-v1.json`
  documenting each kind's export columns and minimum counts for the first real trials.

## Risks and Rollback

- Numerics drift from legacy: same IEEE-754 doubles, same iteration counts → expect exact or
  ≤1e-6; the fixture tolerances are the tripwire.
- Mount gate refactor changes a hot path: protected by the identity replay gate before any
  calibration feature lands; rollback = revert two `_params` reads (parameters additive).
- `SimParameters.FromDictionary` throws on unknown names: adding keys is additive; old
  scenario files unaffected.
- Everything else is new project/command/files; rollback is deletion. The emitted-scenario
  path reuses existing loaders, so no new load semantics exist anywhere.
