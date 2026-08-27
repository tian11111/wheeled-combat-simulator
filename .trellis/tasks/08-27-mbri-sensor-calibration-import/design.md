# Technical Design

## Scope And Boundary

Add an offline sensor-evidence pipeline beside the existing physical
calibration pipeline. The command consumes an explicitly selected MBri data
directory and writes a new JSON evidence report/profile. It does not modify
`Sim.Core` runtime behavior, official scenarios, replays, `fidelity.json`, or
the MBri checkout.

```
MBri data/ + selection manifest
          |
          v
Sim.Cli sensor-calibration import (filesystem + CSV parsing)
          |
          v
Sim.Calibration (pure models, replay evaluators, drift checks)
          |
          v
sensor-calibration-v1 JSON (profile + report + source hashes)
```

`Sim.Protocol` owns the versioned DTOs and validation only. `Sim.Calibration`
owns deterministic calculations and comparison logic. `Sim.Cli` owns path
resolution, CSV decoding, file enumeration, and atomic output. No new Godot
dependency is needed.

## Input Selection

The CLI requires `--data-dir <path>` and a small JSON selection manifest (or
explicit model paths) rather than guessing across all 187 files. The first
supported input is the three generated model files plus their matching raw
families:

- Gray: `gray_model.csv`, `gray_model_summary.csv`, and explicitly named raw
  files with exact header `t,front,rear,left,right`.
- Front ADC: `front_adc_model.csv`, `front_adc_summary.csv`, and raw files with
  exact header `t,left,right,diff,valid`.
- Shovel: `shovel_model.csv` and raw files with exact header
  `t,left,right,valid` whose names classify as `hang` or `stage`.

The selection manifest records the chosen files, vehicle/session label, and
optional expected model hashes. Missing files, duplicate roles, mixed encoding,
or an ambiguous filename classification fail before any output is written.
Files outside the selected families are reported as ignored, never silently
treated as calibration input.

## `sensor-calibration-v1` Contract

Top-level fields are stable and camelCase:

- `schema`, `schemaVersion`, `generatedAt` (informational), `toolVersion`;
- `source`: absolute input root is excluded from the content hash; selected
  relative paths, raw SHA-256, byte count, and capture labels are included;
- `models.gray`, `models.frontAdc`, `models.shovel`, each with typed parameters,
  source files, replay metrics, status (`evidence_only` or `rejected`), and
  limitations;
- `comparison`: stored model vs recomputed model vs optional config snapshot,
  with per-field numeric deltas and `consistent` flags;
- `contentSha256`: canonical report hash excluding `generatedAt` and the hash
  itself.

All numeric values are finite. Gray thresholds must be ordered per channel;
filter windows must be positive odd integers; shovel clear/enter and all model
channel sets must satisfy their domain constraints. Validation errors are
indexed by model/file/row and prevent report creation.

## Deterministic Replay Evaluators

Implement the algorithms already used by MBri, using `double` and fixed order:

1. Gray: rolling median per channel, normalized zone from edge/center
   references, median `zoneScore`, per-channel white enter/clear flags.
2. Front ADC: rolling median, `signal=max(left,right)`, normalized ratio
   `(left-right)/(left+right)`, signal floor, and left/forward/right decision.
   The stored absolute-diff fields are retained as evidence but are not silently
   substituted for the current ratio model.
3. Shovel: rolling median of channel max/min, enter when filtered min is above
   `hangEnter`, clear when filtered max is below `hangClear`; preserve valid-row
   behavior and report transition counts.

Each evaluator returns counts, first/last timestamps, invalid-row counts,
decision distributions, and pass/fail against explicit replay gates. It must be
pure and reusable from tests; it must not call `DateTime`, filesystem, or
random APIs.

## Drift And Eligibility

The report compares the checked-in/generated model CSV with values recomputed
from the selected raw files and, when supplied, a read-only MBri config snapshot.
Any mismatch beyond the declared numeric tolerance or any mixed-batch evidence
sets `status=evidence_only` with a reason. There is no automatic “latest wins”
policy. A future runtime-integration task may consume only a report whose model
status and source manifest are explicitly accepted by a human.

Gray reports always include `coordinateData=false` and a limitation explaining
why they cannot populate `FieldModel.GrayGridMap`.

## CLI And Filesystem Behavior

Use a dedicated command (`sensor-calibration import`) with `--data-dir`,
`--manifest`, `--out`, and optional `--config`. Parse and validate every input
before creating the output directory or report. Writes use temp-file plus move;
existing output requires `--force`. Exit code `2` is usage, `1` is validation or
IO failure, `0` is a report (including `evidence_only` models) successfully
written.

The input root is never copied into the repository. The report uses normalized
relative paths so the same dataset in two checkout locations has the same
content hash.

## Compatibility And Rollback

No existing protocol type is changed; the new schema is additive and offline.
No changes are made to `FieldModel`, `SensorSampler`, `SimParameters`, FSM,
`fidelity.json`, official scenarios, or replay headers. Rollback is deletion of
the new DTO/calibration/CLI files and tests. Existing `dotnet test` and seed-42
replay-check are mandatory gates.
