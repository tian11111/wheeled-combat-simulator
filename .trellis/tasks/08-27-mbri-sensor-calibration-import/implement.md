# Implementation Plan

## Ordered Checklist

1. Add protocol DTOs for `sensor-calibration-v1`: source file records, gray,
   front ADC, shovel model records, replay metrics, comparison/delta records,
   status and report validation. Add canonical serialization/hash helpers that
   exclude `generatedAt` and `contentSha256` from the hashed projection.
2. Add a pure CSV row model/parser in `Sim.Calibration` (or a shared non-IO
   helper) supporting quoted fields, UTF-8/UTF-8-BOM, exact headers, finite
   numeric conversion, row numbers, and deterministic column order. It must
   never enumerate files or write output itself.
3. Implement the three pure replay evaluators using the algorithms and
   constraints in `design.md`; add unit tests for median warm-up, invalid rows,
   threshold ordering, ratio-vs-diff behavior, and hang/clear transitions.
4. Implement model readers and recomputation/drift comparison. Require an
   explicit selection manifest; classify selected/ignored/rejected files and
   detect mixed-batch ambiguity before fitting. Add tests using small temporary
   CSV fixtures and the real MBri model files copied to a temp directory.
5. Wire `Sim.Cli sensor-calibration import` with usage/validation/IO exit codes,
   atomic output and `--force`. Ensure invalid input produces no report,
   evidence-only drift still produces a report, and repository paths are stored
   as normalized relative paths.
6. Add an integration fixture manifest pointing at a controlled subset of
   `MBri/data` (do not copy the 187 raw files into the repository). Assert the
   report lists ignored files, marks gray `coordinateData=false`, and exposes
   stored/recomputed/config drift without promoting any fidelity status.
7. Add CLI determinism tests: two runs have identical canonical report and
   content hash; different absolute temp roots yield the same hash; `--force`
   behavior is explicit; malformed headers/rows fail before output.
8. Update `docs/CLI.md` and `telemetry/README.md` with the separate sensor
   command and the rule that this evidence is not `telemetry-v1` physical
   calibration. Do not change the existing `calibrate` command semantics.

## Validation Commands

```powershell
dotnet build RobotSimulator.sln -m:1 -p:UseSharedCompilation=false
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 -p:UseSharedCompilation=false
dotnet run --project src/Sim.Cli --no-build -- replay-check src/Sim.Tests/fixtures/godot-parity-seed42.json
dotnet run --project src/Sim.Cli --no-build -- sensor-calibration import --help
git diff --check
```

For the external data audit, run the command against a temporary copy of the
selected MBri files and compare the JSON twice. Never write into
`D:/project/robocup/MBri/data` from this repository's tests.

## Review Gates

- Protocol validation and canonical hash tests pass before CLI wiring.
- No evaluator references `Sim.Core` mutable state, filesystem, clock, or random.
- The report cannot claim a measured field grid without coordinate data.
- Stored model/config drift is visible and never auto-resolved.
- Existing `fidelity.json` bytes and seed-42 event fingerprints are unchanged.
- Full build/test/replay gates pass before task activation is considered ready.

## Risky Files And Rollback Points

- `src/Sim.Protocol/`: additive contract only; rollback by removing new DTOs.
- `src/Sim.Calibration/`: numerical replay code; protect with fixture tests.
- `src/Sim.Cli/Program.cs`: add one dispatch branch; leave existing commands
  untouched.
- `src/Sim.Tests/`: tests must use temp copies and never mutate external data.
- `docs/CLI.md`, `telemetry/README.md`: document offline evidence boundary.
