# MBri 传感器漂移解析技术设计

## Decision

Treat stored model values, recomputed values, and config.py values as separate candidates. The importer remains evidence-only until batch identity and signal semantics are confirmed; no candidate silently becomes a runtime threshold.

## Inputs and Outputs

- Inputs: calibration/mbri-summer-sensor-report.json, its selection manifest and hashed source CSVs, plus the archived sensor-import task artifacts.
- Outputs: a deterministic drift report with one section per model family, batch membership, source hashes, sample counts, replay status, candidate values, and an explicit decision or rejection.
- Runtime impact: none unless a separately reviewed profile is explicitly approved.

## Analysis

Reuse SensorEvidenceBuilder, SensorReplay, and the existing report fingerprint. For each of gray near_edge_enter, front ADC diff_low, and shovel hang_enter, compare only like-for-like signal semantics and batch selections. Explain whether the delta is caused by mixed batches, recomputation, model meaning, or invalid/insufficient labels. A failed shovel replay remains visible and blocks promotion.

## Compatibility and Safety

Keep sensor-calibration-v1, old replay files, SensorSampler, FSM behavior, and fidelity.json unchanged by default. Any approved runtime profile must be a new explicit artifact with provenance and a regression fixture.

## Verification

Require deterministic report fingerprints, malformed-input rejection, source-list auditability, and unchanged seed-42 replay behavior.
