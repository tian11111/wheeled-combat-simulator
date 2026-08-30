# Godot 实体点选与拖拽布局编辑设计

## 1. Architecture and boundaries

The feature stays inside the existing Godot layout-editing boundary:

```text
mouse screen event
  -> LayoutEditor entity picker
  -> selected entity + ground-plane drag delta
  -> LayoutDraft (field-local position)
  -> RefreshPreview()
  -> temporary MatchEngine -> SnapshotView -> ArenaVisualizer
  -> Enter applies Scenario and Main rebuilds MatchSession
```

- `LayoutEditor` owns pointer input, selection state, hit testing, and the
  selection highlight.
- `LayoutDraft` owns the pure mutation for a vehicle start position. It keeps
  the existing immutable-state/undo grouping behavior and does not reference
  Godot.
- `SnapshotView` remains the only snapshot-to-render projection.
- `ArenaVisualizer` remains a display consumer. The task does not add physics
  bodies, teleport commands, score logic, or camera-relative transforms.
- `Main` only extends the existing edit smoke and apply/rebuild flow.

## 2. Selection model

Extend `LayoutEditor.Selection` with `RobotUs` and `RobotThem`. Keep the block
index for `Selection.Block`; map robot selections to a role through a small
typed helper rather than a free-form dictionary value.

Selection order is deterministic:

1. Find all entity hit proxies under the pointer.
2. Choose the smallest positive ray distance (the visually nearest entity).
3. If distances are effectively equal, prefer a robot over a block.
4. If no entity is hit, retain the current fallback: ground-plane pick for a
   start zone or the whole field, otherwise clear the selection.

Entity picking must not infer the target solely from the intersection of the
mouse ray with `y=0`. That intersection is still suitable for calculating a
horizontal drag delta, but at a low camera angle it can be materially behind a
raised cube or vehicle.

## 3. World-space hit proxies

Use the existing camera methods `ProjectRayOrigin` and `ProjectRayNormal` and
test against render-only analytic proxies. Do not add `StaticBody3D` or
collision shapes, because those would create a second collision representation
and could be mistaken for authoritative simulation geometry.

### Energy blocks

- Read fixed coordinates from `_previewFrame.Blocks` (the editor enters with
  spawn-resolved blocks) or derive the same world positions from the draft and
  `FieldTransform` when the preview is unavailable.
- Test a world-aligned box from the block base height through
  `base + field.BlockSize`, with a small input-only margin.
- The proxy follows the current visual support height (`OnPlatform` versus
  ground) but does not change the block's simulation data.

### Vehicles

- Read the current preview robot base position from `_previewFrame.Us` or
  `_previewFrame.Them`.
- Test an upright world-space box/cylinder proxy centered on the vehicle's
  start location, using that role's `VehicleProfile.CollisionRadius` plus a
  small input-only margin and a generous visual pick height. The proxy is
  deliberately independent of whether the vehicle uses primitive fallback or
  an imported glTF model.
- Vehicle yaw is used only for the render preview; the hit proxy may remain
  axis-aligned because the body footprint is circular and the task edits
  position only.

The picker returns a typed hit record containing the selection kind, block
index or role, and ray distance. All coordinates used for mutation are still
converted through the existing `FieldTransform`; no local rotation formula is
introduced in the picker.

## 4. Drag and draft mutation

- On left-button press, run entity picking before the existing ground fallback.
  If the pointer has a valid ground projection, store it as the drag anchor,
  begin one `LayoutDraft` group, and mark the event handled.
- On mouse motion, calculate the world-XZ delta from successive ground
  projections. Convert it to field-local delta for zones, blocks, and vehicles;
  keep whole-field movement in world axes as it is today. Apply the existing
  snap setting and call the selected mutation.
- On release, end the group once. A continuous drag is one undo entry.
- Add `LayoutDraft.MoveStart(role, localX, localY)`. It copies the selected
  `Pose2`, changes only `X/Y`, preserves `Th`, and leaves `StartZones` intact.
- Existing `MoveBlock` and `MoveStartZone` semantics remain unchanged.
- `RefreshPreview()` rebuilds the temporary valid preview exactly as it does
  today. If a draft becomes invalid, the current validation/message behavior is
  preserved.

## 5. Highlight and UX

- Reuse `_highlight` as a horizontal selection marker. For a robot, position it
  at the robot's current start location and size it from its collision radius
  plus the existing visual selection margin. For a block, retain the current
  block marker. Field and zone markers keep their current behavior.
- `SelectedLabel` gains distinct labels for `我方小车` and `对手小车`.
- Reset `_selectedBlock` when switching to a robot or non-block selection.
- `Main` continues to set `MatchCamera.PointerInputEnabled` false while the
  editor is active, so camera orbit/pan cannot consume the same pointer event.

## 6. Compatibility and safety

- The feature is available only in the existing preparation-only editor gate.
  Replay mode and an already-running match remain non-editable.
- Apply writes the edited vehicle start pose and block coordinates into the
  normal `Scenario`, then rebuilds `MatchSession`; no live engine state is
  mutated.
- Existing `Scenario.Validate()` remains the validation authority. This task
  does not add a start-zone-membership rule.
- Imported robot models remain render-only and do not participate in picking
  through Godot physics.
- Old scenarios, replay JSON, protocol shapes, seeds, and deterministic
  simulation behavior remain byte-compatible.

## 7. Verification design

- `LayoutDraftTests` cover move-start position, heading preservation, zone
  immutability, undo/redo, and canonical scenario output.
- `--edit-smoke` exercises the screen-space entity picker for both a vehicle
  and a block, verifies the vehicle-only start mutation, and verifies Apply
  rebuilds the engine from the edited scenario.
- Existing `--camera-smoke` verifies that camera input remains unchanged and
  that editor ownership still blocks camera pointer handling.
- Run the full .NET test suite, both Godot smoke commands, replay parity, and
  `git diff --check` before activation/implementation completion.
