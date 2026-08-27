# Rule Drawing And Code Audit

## Rule Evidence

Source: `D:/project/robocup/1779761830740288.pdf`, section 2.4, pages 10-11.

- Inner field: 3.8m x 3.8m.
- Platform: 2.4m x 2.4m, 0.06m high.
- Platform surface: grayscale from pure black at the outer corners to pure white at the center.
- Center: red square with a white Chinese character `武`.
- Start zones: pure yellow and pure blue, each 0.50m x 0.40m, 0.20m from the platform edge.
- Walkway: black, 0.70m wide around the platform.
- Fence: square black fence, 0.20m high.
- Blocks: two buff and one debuff cube, each 0.15m per side, referee-randomized placement.
- Surface: wood base with matte PVC film.

The drawing is marked as a visual reference. Numeric text is authoritative where the perspective drawing is ambiguous.

## Current Repository Evidence

- `src/Sim.Protocol/Scenario.cs` already contains official dimensions, start zones, start poses, and block specs, but no layout version or world transform.
- `src/Sim.Core/FieldModel.cs` assumes an axis-aligned square platform and mirrors the X platform bounds into Y. It cannot represent a translated or rotated field.
- `src/Sim.Core/Physics.cs` implements stage wall normals and outer fence clamps in global axis-aligned coordinates.
- `godot/src/ArenaVisualizer.cs` duplicates field constants and builds a fixed visual scene. It omits start zones and the white `武`; its fence mesh is 0.12m high rather than the rule's 0.20m.
- `godot/src/Main.cs` can load a scenario but has no editor/save workflow.
- `godot/src/MatchSession.cs` owns the current immutable scenario/engine and must recreate the engine after a saved or applied layout change.
- `godot/src/SnapshotView.cs` hardcodes 0.06m for block display height and must instead receive field geometry.
- `Sim.Tests.csproj` links Godot-free shell adapters into xUnit tests. New layout/editor state transformations should follow this pattern for headless coverage.

## Technical Conclusions

1. Additive protocol evolution is required; old JSON must deserialize with identity field transform and official defaults.
2. Geometry operations should be performed in field-local coordinates through one shared transform abstraction. Scattering rotation formulas through physics, sensors, and rendering would create drift.
3. Godot should render from the current `Scenario`, not local constants.
4. Runtime external glTF loading should use Godot `GLTFDocument` / `GLTFState`; `ResourceLoader` is only the fast path for already imported `res://` resources.
5. A layout cannot mutate under a running deterministic engine. Apply/load/reset must create a fresh `MatchSession` before the match starts.
