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

HUD cards are scaled around the viewport center. Convert the visible viewport
corners into the scaled HUD's local coordinates before anchoring cards; otherwise
uiScale=1.4 pushes the top and bottom cards off-screen. Respect the actual card
minimum sizes and leave space between the status, event and replay areas. If a
compact fallback needs scrolling, keep its input region confined to the cards so
the remaining scene still accepts camera input. Keep the help card's width equal
across normal and compact layouts; a width change can alter wrapped text height
and make the layout switch back and forth on every frame.

The scrollable event text uses `MouseFilter.Pass` and
`MouseForcePassScrollEvents=false`: the wheel scrolls the event list without
zooming the camera, while left/right drags can reach the camera. Verify both
paths through Godot's input pipeline, including releasing a drag over the feed;
a static screenshot cannot establish these interactions. When events refresh,
follow the newest item only if the reader was already at the bottom; preserve
the scroll position while the reader is inspecting older events.

Camera/input smoke checks (`--camera-smoke`, `--edit-smoke`) run through the
real input pipeline and are timing-sensitive: the headless viewport is 64×64,
the camera pose is damped, and Godot's buffered-input flush jitters, so
assertions must wait for pose convergence before ray-cast math and action
injection must poll until an effect is observed instead of sleeping a fixed
frame count. `--capture` must not run its countdown while a smoke is active —
schedule captures after the smoke finishes plus a short settle, or the smoke
exits before asserting. Real-renderer captures are the evidence for visual
claims (e.g. gray-band absence); keep them out of Git.

Visual-fidelity claims need dual-resolution real-renderer captures
(1280×720 and 1920×1080), a framing-ratio assertion in `--camera-smoke`
(unclamped NDC — clamping corner projections can mask fence-cutting
regressions), and a frame-time comparison with a ~30% regression gate; any
expensive effect that fails the gate ships off by default with a
reproducible experiment recipe in `godot/README.md`. Refresh committed
evidence screenshots with dated new files instead of overwriting historical
"before" images referenced by task PRDs.

Review source-of-truth ownership, nullable handling, replay compatibility,
headless testability, and whether the UI remains a pure consumer of snapshots.
