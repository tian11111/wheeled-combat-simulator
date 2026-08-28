# MBri 传感器漂移解析执行计划

## Ordered Checklist

1. Start this child task only after the parent planning review; validate the archived import contract and current report.
2. Re-run the importer with the exact selection manifest and capture the stored/recomputed/config comparison for all three target fields.
3. Partition source files by explicit batch/session evidence; reject ambiguous mixtures instead of selecting the newest value.
4. Trace the gray, front ADC, and shovel replay failures to their actual data/model semantics; add focused regression tests for each drift.
5. Produce a deterministic decision report. Keep unresolved candidates evidence_only or rejected; do not edit runtime defaults.
6. Run sensor tests, full .NET tests, and seed-42 replay-check.

## Validation Commands

    dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter SensorCalibration
    dotnet run --project src/Sim.Cli --no-build -- sensor-calibration import --data-dir "D:/project/robocup/MBri/data" --manifest selection.json --out calibration/sensor-report.json
    dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json

## Rollback Points

- Revert only the importer/report/test changes if the report fingerprint or old replay changes unexpectedly.
- Never overwrite the source CSVs or silently modify config.py.
