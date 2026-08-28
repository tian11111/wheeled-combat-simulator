# Godot 窗口渲染冒烟技术设计

## Boundary

Keep the existing Main --edit-smoke flow, LayoutEditor, LayoutDraft, MatchSession, and ParityCheck as the source of truth. Separate three claims: headless scene loading, editor logic assertions, and renderer-backed screenshot capture.

## Smoke Evidence

The renderer-backed run must exercise selection, drag, snapping, rotation, undo/redo, restore, apply, and capture a non-empty image containing arena, robot, and HUD pixels. The same applied scenario must still pass CLI/Godot parity. A dummy renderer failure cannot be reclassified as a visual pass.

## Smallest Fix Policy

First reproduce with the installed Godot 4.7.2 Mono executable. If the failure is only the headless dummy texture, isolate the capability check or split logic smoke from renderer smoke without weakening either gate. Do not move scoring, collision, or layout authority into Godot.

## Rollback

Revert smoke harness changes independently from the renderer and core; retain failed logs and screenshots as evidence if the environment blocks the windowed run.
