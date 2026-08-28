# Quality Guidelines

Godot changes must preserve headless behavior and cross-layer determinism.

Do not duplicate core rules in nodes, depend on editor-only APIs in pure
helpers, or put render assets/preferences into `Scenario`, `Snapshot`, or replay
hashes. Keep adapters engine-free where possible and link them into `Sim.Tests`
as done in `src/Sim.Tests/Sim.Tests.csproj`.

Add focused tests for projection, interpolation, layout validation, and parity-
sensitive behavior. Run `dotnet test` plus the Godot headless parity command
from the README for cross-layer changes. Visual changes should use the
project's `--capture` pixel evidence workflow and keep temporary screenshots out
 of Git.

For bottom-anchored HUD cards, account for the control's minimum size rather than
relying only on offset height. When replay controls are visible, position the
event feed above the replay bar and verify the rendered gap at the supported
window sizes; otherwise Godot may expand the card into the replay controls.

Camera/input smoke checks (`--camera-smoke`, `--edit-smoke`) run through the
real input pipeline and are timing-sensitive: the headless viewport is 64×64,
the camera pose is damped, and Godot's buffered-input flush jitters, so
assertions must wait for pose convergence before ray-cast math and action
injection must poll until an effect is observed instead of sleeping a fixed
frame count. `--capture` must not run its countdown while a smoke is active —
schedule captures after the smoke finishes plus a short settle, or the smoke
exits before asserting. Real-renderer captures are the evidence for visual
claims (e.g. gray-band absence); keep them out of Git.

Review source-of-truth ownership, nullable handling, replay compatibility,
headless testability, and whether the UI remains a pure consumer of snapshots.
