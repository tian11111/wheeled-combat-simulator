# Technical Design

## Architecture

Keep the existing modular monolith and authority boundary:

```text
Godot editor UI -> immutable Scenario draft -> validate/save JSON
                                      |
                                      v
Godot live / Sim.Cli -> MatchEngine -> FieldModel local geometry
                                      |
                                      v
                                snapshots/replays
```

`Sim.Protocol` owns serializable layout data. `Sim.Core` owns all authoritative coordinate and collision calculations. Godot owns picking, handles, file dialogs, visual assets, and draft editing. No database, service, or Godot physics authority is introduced.

## Protocol Contract

Evolve `Scenario` additively:

- `layoutVersion`: optional string, default `arena-layout-v1` in new files; missing means legacy identity layout.
- `field.pose`: optional `Pose2`, default `(0,0,0)`. It maps field-local coordinates into simulation-world coordinates.
- Existing `platform`, `startZones`, `starts`, and block coordinates remain expressed in field-local metres. This keeps the official fixture readable and preserves existing values.
- Optional render-only robot asset settings belong in a separate Godot preferences JSON, not `VehicleProfile` or replay headers. Suggested schema: role -> path, scale, yawOffset, heightOffset. This prevents a local asset path from affecting replay determinism.

Validation must reject non-finite transforms, unsupported `layoutVersion`, invalid regions, start zones outside the 3.8m inner field, blocks outside the editable inner field, and duplicate/missing roles. Protocol JSON property names remain camelCase.

## Coordinate Model

Add a pure `FieldTransform` helper in `Sim.Core`:

- `LocalToWorldPoint`, `WorldToLocalPoint`.
- `LocalToWorldVector`, `WorldToLocalVector`.
- `LocalToWorldHeading`, `WorldToLocalHeading`.

`FieldModel` evaluates platform, gray map, stage height, nearest platform point, distance to edge, start zones, and fence bounds in local coordinates. Public methods accept world coordinates unless explicitly suffixed `Local`.

Physics transforms robot position, velocity and relevant contact normals through the helper at the geometry boundary. The stage-wall solver remains a deterministic axis-aligned solver in field-local coordinates, then maps the corrected pose/velocity back to world coordinates. Sensors and FSM continue calling `FieldModel`; they do not implement transforms themselves. Robot spawn poses and block specs are transformed once when runtime state is created.

The default identity transform must produce bit-for-bit identical seed-42 replay output. This is a compatibility gate, not a tolerance comparison.

## Field Rendering

`ArenaVisualizer.Configure(Scenario)` rebuilds a dedicated `ArenaRoot` from scenario data:

- 3.8m black matte floor and 0.20m fence.
- 2.4m square platform at 0.06m.
- A generated top-surface texture or shader implementing corner-black to center-white grayscale.
- Red center area with a bundled raster texture containing a white `武` glyph. The texture is visual only; gray sensor sampling stays in `FieldModel`.
- Yellow and blue 0.50m x 0.40m start-zone meshes at scenario coordinates.
- Blocks use scenario `blockSize`; block height in `SnapshotView` derives from scenario field height.

The complete arena root receives the field pose. Robot/block snapshots are already in simulation-world coordinates and must not be parented below the transformed arena root.

## Editor State And UX

Create a Godot-free `LayoutDraft`/command history layer and a Godot `LayoutEditor` interaction layer.

- Enter edit mode only from a fresh or reset live session; entering resets the current match after confirmation within the UI if it has advanced.
- Select arena, yellow/blue start zone, or block by ray picking or hierarchy list.
- Drag on the ground plane; rotate with a visible yaw control and numeric input.
- Default translation snap: 0.01m; default rotation snap: 5 degrees; both may be toggled.
- `Ctrl+Z` undo, `Ctrl+Y` redo, `Delete` only for optional block placement if the layout still validates, and a restore-official action.
- Inspector shows local X/Y/yaw and dimensions. Fixed official dimensions can be displayed but not resized in MVP.
- Save As writes validated canonical JSON atomically; Open loads into a draft and does not replace the active engine until Apply.
- Apply recreates `MatchSession` from the validated draft. Replay mode and an active match disable editing.

The editor changes layout geometry, not the application window or camera. Camera pan/orbit may remain a separate view interaction.

## Robot Model Import

Add `RobotModelLoader` in Godot:

1. For `res://` resources already imported by Godot, use `ResourceLoader`/`PackedScene`.
2. For external `.glb/.gltf`, use `GLTFDocument.AppendFromFile` with `GLTFState`, then generate the scene.
3. Validate extension, file existence, generated node count, and a conservative file-size limit; report errors in the HUD.
4. Parent the generated scene under the existing robot visual root and apply render-only scale/yaw/height offsets.
5. Keep the primitive body and selection/on-stage ring as fallback/diagnostic nodes. Hide the primitive body only after successful import.

Model configuration is local desktop state and never enters `Scenario`, `Snapshot`, or replay fingerprints.

## Compatibility And Migration

- Missing `field.pose` is identity; existing scenarios and replay fixtures must remain bit-identical.
- Existing `ScenarioPath` continues to work.
- New files set `layoutVersion`; readers reject future unsupported versions with a clear validation error.
- Do not alter replay-v1 field meanings. A replay embeds the complete scenario, so transformed layouts reproduce without another file.
- Keep current primitive visuals as fallback and preserve all live/replay controls outside edit mode.

## Risks And Rollback

- Highest risk: rotating the field can cause mismatched collision normals or spawn transforms. Protect each transform function and rotated stage/fence behavior with focused tests before UI work.
- Generated surface texture may differ visually across renderers. Test dimensions separately from pixel evidence and bundle a deterministic fallback material.
- Runtime glTF files may contain huge scenes or unsupported extensions. Bound file size/node count and fail closed to primitive visuals.
- Protocol additions are easy to roll back because legacy identity behavior remains the default; do not rewrite existing fixture files until compatibility tests pass.
