# 真机遥测保真度门禁技术设计

## Boundary

Use the existing telemetry-v1 DTO validation, Sim.Calibration fitters, CalibrateCommand, holdout metrics, and fidelity registry. This child decides whether evidence qualifies; it does not invent missing physical observations.

## Promotion Rule

For friction, collision, stall, and mount independently, require valid real-source metadata, sufficient coverage, an untouched holdout split, and the existing acceptance thresholds. A synthetic fixture may prove numerical implementation only. A failed or missing gate leaves the subsystem at its current status and records the reason.

## Data Flow

    telemetry-v1 file → validation → fit/holdout decomposition
                      → parameter/report candidate → fidelity decision

If no suitable real telemetry exists, the deliverable is a deterministic availability report listing missing fields, experiment kinds, labels, and next collection requirements. fidelity.json remains unchanged.

## Compatibility and Rollback

Any promoted profile is a new scenario/profile artifact; official scenarios and old replay fingerprints remain untouched. Revert the profile or fidelity entry independently if parity or holdout evidence changes.
