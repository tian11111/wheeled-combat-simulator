# 仿真器交付收尾执行计划

## Ordered Checklist

1. Validate the parent and child Trellis artifacts; record the clean/remote baseline and confirm no product code is modified before starting.
2. Start and complete `sensor-calibration-drift-resolution`: reproduce the report, partition by manifest/batch, add regression coverage, and produce a reviewed drift decision without auto-merging thresholds.
3. Start and complete `real-telemetry-fidelity-promotion`: inventory real telemetry, run fit/holdout gates, and either promote only qualifying real subsystems or record the no-data/no-promotion blocker while leaving `fidelity.json` unchanged.
4. Start and complete `godot-render-smoke-closeout`: reproduce the dummy-renderer failure, run renderer-backed editor smoke, make the smallest safe smoke/QA adjustment if needed, and preserve parity.
5. Run the full cross-task quality gate: `dotnet test`, seed-42 `replay-check`, calibration/sensor regression, Godot parity, and `git diff --check`.
6. Start and complete `github-sync-closeout`: verify the final local HEAD and remote ref, push `main` if there is no divergence, and verify clean equality afterward.
7. Update applicable specs/journal, archive the child tasks and parent only after all acceptance criteria have evidence.

## Validation Commands

```powershell
dotnet test -m:1 -p:UseSharedCompilation=false
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
dotnet run --project src/Sim.Cli --no-build -- sensor-calibration import --data-dir "D:/project/robocup/MBri/data" --manifest selection.json --out calibration/sensor-report.json
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json
git diff --check
git status --short --branch
```

## Rollback Points

- Do not alter runtime sensor thresholds or `fidelity.json` until the corresponding child report passes review.
- If Godot visual validation is unavailable, retain the failed evidence and stop before claiming completion.
- If the remote branch diverges, stop synchronization and report the exact refs.
