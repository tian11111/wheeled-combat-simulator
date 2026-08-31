# Component Guidelines

Godot scenes/nodes are the UI components; the data contract is `RenderFrame`.
`ArenaVisualizer` owns Godot nodes and input. It consumes the immutable output
of `SnapshotView.From`/`Lerp` rather than recomputing physics, scoring, or sensor
rules locally.

Prefer typed records (`RobotVisual`, `BlockVisual`, `HudState`) over dictionaries
for data passed between shell helpers. Keep node lookup and visual updates in
the visualizer. Inputs map to referee/session commands and remain separate from
rendering; parity behavior must be testable headlessly through `ParityCheck`.

`MatchCamera` state is presentation-only: Overview and Follow orbit a ground
focus via left-drag under the reversed-direction contract (2026-08-29 real-
machine feedback: screen +X/right drag decreases yaw at −0.3°/px, screen
+Y/down drag raises pitch at +0.25°/px, pitch clamped 10°–85°; Top spins
−0.3°/px in-plane at fixed −90° pitch — assert all four drag directions in
`--camera-smoke` so the old signs cannot be pinned back), right-drag pans with
grab semantics — the grabbed ground
point follows the pointer so the focus moves opposite the cursor's ground
delta (Follow focus stays frame-driven) — and wheel zooms within clamped
distance/height (×0.3–3 overview, ×0.5–3 top, ×0.5–2.5 follow). `F5`/arena
rebuild resets the orbit angles to the default framing. Camera pointer
handling is gated behind a narrow ownership hook (`PointerInputEnabled`);
Main turns it off while `LayoutEditor.Active` so editor selection/drag owns
the mouse, and consumed events are marked handled. Never add a second scene
transform or duplicated field-size constants here.

Common mistake: adding local score or collision logic to a visual node. The
authoritative implementation remains in `Sim.Core`.

Visual-stack conventions (2026-08 visual-fidelity pass): lights, sky, SSAO,
tonemap, ReflectionProbe (UPDATE_ONCE, limited extents), MSAA, SDFGI, low-
density volumetric fog, thresholded Glow, and far DoF are presentation-only
render parameters. The platform-top gray texture stays `Unshaded` with the
official palette — never light it, never attach procedural noise to it, and
never let it become a Glow source; A/B captures must check the gray reading.
Glow/TAA are separate decisions: Glow is currently low-intensity and gated at
HDR threshold 1.2, while TAA remains off because it can leave motion trails.
Decorative nodes (robot detail parts, team strips, contact-shadow discs,
start-zone outlines, center ring) are tagged or kept in the visualizer layer,
derived from `Scenario.Field` geometry, and never participate in
collision/rules/sensors.
Z-order gotcha: the platform top plane sits at `PlatformHeight + 0.001` — any
under-robot overlay disc must be raised above that (and below the mount ring)
or it is occluded exactly on-stage where it matters.

## Scenario: Forward+ visual QA switches

### 1. Scope / Trigger

- Trigger: the third visual-fidelity pass adds independently reversible Godot
  renderer features and a repeatable performance measurement path.
- Scope: `godot/src/Main.cs`, `godot/scenes/Main.tscn`, and
  `godot/src/ArenaVisualizer.cs`; no `RenderFrame`, `Scenario`, `Snapshot`, or
  simulation rule changes.

### 2. Signatures

- `--visual-baseline`: disable SDFGI, volumetric fog, Glow, far DoF, and
  material detail together.
- `--visual-no-sdfgi`, `--visual-no-fog`, `--visual-no-glow`,
  `--visual-no-dof`, `--visual-no-material-noise`: disable one feature.
- `--visual-frame-stats <positive-int>`: sample the main-loop `delta` for a
  clamped 1–10,000 frame window and print count/avg/min/max milliseconds.

### 3. Contracts

- Flags are parsed only after Godot's `--` separator and affect presentation
  state before `BuildScenario()`; they must not alter simulation inputs,
  snapshots, scores, or replay output.
- SDFGI, volumetric fog, and DoF are effective only on supported renderers;
  `gl_compatibility` may log an engine warning and must still render without
  crashing.
- `MaterialDetailEnabled=false` leaves the platform-top `Unshaded`
  `MakeTexturedMaterial` path unchanged.

### 4. Validation & Error Matrix

| Condition | Required behavior |
|---|---|
| no visual flag | all default third-pass features remain enabled in Forward+ |
| one `--visual-no-*` flag | only that feature is disabled |
| `--visual-baseline` | all five feature groups report false in `[visual-qa]` |
| malformed/missing frame count | do not enable frame sampling; application still starts |
| compatibility renderer | unsupported effects are engine-degraded; shell exits normally |

### 5. Good/Base/Bad Cases

- Good: Forward+ capture prints the expected feature state and saves a PNG.
- Base: headless dummy renderer runs smoke tests and skips the unavailable
  viewport capture without changing the smoke exit code.
- Bad: putting flags before `--`, or applying noise to the gray platform,
  makes the A/B evidence invalid and is not an accepted implementation.

### 6. Tests Required

- `dotnet build godot/GodotSim.csproj --no-restore`: zero errors/warnings for
  the Godot C# layer.
- `--camera-smoke` and `--edit-smoke`: camera/editor assertions remain green.
- `--parity-check replays/godot-parity-seed42.json`: score, tick, reason, and
  event fingerprints remain identical.
- Forward+ A/B captures at 1280×720 and 1920×1080: verify feature logs,
  platform gray pixels, and frame-stat regression ≤30%.

### 7. Wrong vs Correct

#### Wrong

```text
--visual-no-sdfgi --path godot
```

#### Correct

```text
godot --path godot -- --visual-no-sdfgi
```
