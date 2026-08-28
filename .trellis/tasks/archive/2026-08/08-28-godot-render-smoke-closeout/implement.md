# Godot 窗口渲染冒烟执行计划

## Ordered Checklist

1. Validate the current Godot project and reproduce --edit-smoke in headless and renderer-backed modes.
2. Confirm the 22 logic assertions and the applied-layout parity result independently of screenshot capture.
3. Run the windowed smoke/capture with the explicit Godot 4.7.2 Mono executable; collect exit code, log, dimensions, and non-empty pixel evidence.
4. If needed, make the smallest capability-aware smoke harness fix, then rerun both logic and renderer gates.
5. Run .NET tests, CLI replay-check, Godot parity, and git diff --check.

## Validation Commands

    godot --headless --path godot -- --edit-smoke
    godot --path godot -- --edit-smoke --capture godot/docs/desktop-editsmoke-closeout.png
    dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
    godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json

## Rollback Points

- Do not weaken the screenshot assertion just to make dummy-renderer execution exit 0.
- Keep any environment approval failure separate from product failure.
