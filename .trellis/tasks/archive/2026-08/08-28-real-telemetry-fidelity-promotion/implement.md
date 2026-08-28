# 真机遥测保真度门禁执行计划

## Ordered Checklist

1. Inventory telemetry/ and external supplied data for actual telemetry-v1 files; classify source, vehicle, session, and experiment coverage.
2. Validate every candidate input before fitting. Record rejected files and missing physical fields without producing an applicable patch.
3. Run the existing calibrator and inspect fit versus holdout metrics for each subsystem.
4. If real data and all gates pass, generate a versioned profile and update only the qualifying fidelity.json entries. If data is absent or insufficient, write the explicit no-promotion report and leave fidelity unchanged.
5. Run contract, calibration, full .NET, replay, and parity checks; verify synthetic fixtures never trigger real promotion.

## Validation Commands

    dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter "Calibration|Telemetry"
    dotnet run --project src/Sim.Cli --no-build -- calibrate --input <telemetry-v1-file> --out calibration/closeout-report.json
    dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json

## Rollback Points

- Do not update fidelity.json when source metadata is synthetic, missing, or ambiguous.
- If a promoted profile changes old replay output, remove only the new profile/registration and restore the prior fidelity entry.
