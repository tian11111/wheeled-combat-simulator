# Implementation Plan

1. Core restart contract: add explicit `RestartRobot` in `src/Sim.Core/MatchEngine.cs`; reset mutable target fields from `src/Sim.Core/RuntimeState.cs`; preserve timer and apply exactly one opponent +4.
2. Replay and parity route: record `restart_robot:<role>`; teach `src/Sim.Cli/Program.cs`, `godot/src/ParityCheck.cs`, and `godot/src/MatchSession.cs` to apply it; preserve old penalty-only commands.
3. Godot command/session fixes: route R/T in `godot/src/Main.cs`, guard live phases, refresh the next frame, update HUD text, retain the current scenario, and reapply the scene after F5.
4. Camera input: correct Top in `godot/src/MatchCamera.cs`; add clamped pan/zoom and ground-ray pointer math; coordinate ownership with `godot/src/LayoutEditor.cs`; keep Follow and C stable.
5. Gray evidence: correct UV/axis/material shading in `godot/src/ArenaVisualizer.cs` without changing core gray; add representative `FieldGray` tests and real-renderer evidence; do not import coordinate-less sensor CSV.
6. Test harness/docs: handle headless dummy-renderer capture safely and update `godot/README.md` plus relevant architecture notes.

## Validation

```powershell
dotnet test --no-restore

$godot = 'C:\Users\Neco\AppData\Local\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
& $godot --headless --path godot -- --parity-check D:\project\robot-simulator\src\Sim.Tests\fixtures\godot-parity-seed42.json
& $godot --headless --path godot -- --edit-smoke
& $godot --path godot --rendering-method gl_compatibility -- --edit-smoke --capture <temp-capture.png>
```

Add a deterministic real-restart replay fixture checking both directions, score +4, target pose/FSM reset, event order, old command compatibility, and Godot/CLI parity. Add deterministic camera input evidence for Overview, Top, pan, and zoom.

## Risky files

- `src/Sim.Core/MatchEngine.cs` and `src/Sim.Core/RuntimeState.cs`: authoritative state and scoring.
- `src/Sim.Cli/Program.cs`, `godot/src/ParityCheck.cs`, and `godot/src/MatchSession.cs`: command decoding.
- `godot/src/MatchCamera.cs` and `godot/src/LayoutEditor.cs`: input ownership and viewport behavior.
- `godot/src/ArenaVisualizer.cs`: visual-only but requires coordinate-aware capture evidence.

Do not run `task.py start` in the planning turn; implementation requires a subsequent explicit approval of the final planning summary.
