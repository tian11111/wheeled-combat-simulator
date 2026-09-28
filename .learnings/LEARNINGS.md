# Learnings

Corrections, insights, and knowledge gaps captured during development.

**Categories**: correction | insight | knowledge_gap | best_practice

---

## [LRN-20260830-001] correction

**Logged**: 2026-08-30
**Priority**: medium
**Status**: resolved
**Area**: frontend

### Summary

A visual-only support plane needs a shared lift and an explicit contact cue for rendered energy blocks.

### Details

The energy mesh was a correctly sized cube, but its simulation support height differed from the intentionally lifted rendered platform plane. With an unshaded top surface, the white six-face sticker had no contact contrast and read as suspended from oblique views.

### Suggested Action

Derive both the platform surface and block visual base from one rendering lift, keep simulation coordinates unchanged, and add a minimal visual contact shadow.

### Metadata

- **Source**: user_feedback
- **Related Files**: godot/src/ArenaVisualizer.cs
- **Tags**: godot, rendering, grounding, energy-block

### Resolution

- **Resolved**: 2026-08-30
- **Notes**: Shared the visual surface lift, anchored the rendered block base, and added a tiny contact shadow; verified with real-renderer captures.

## [LRN-20260830-002] correction

**Logged**: 2026-08-30
**Priority**: medium
**Status**: resolved
**Area**: frontend

### Summary

An energy-block contact cue must not use a protruding 3D disc that can read as a pointed underside from a low camera angle.

### Details

The first grounding pass used a thin cylinder beneath each block. Although it improved contact contrast, the cylinder was visible as a dark round protrusion and made the upright cube look tilted or point-bottomed while orbiting the camera.

### Suggested Action

Keep the energy block as an axis-aligned world-space cube and use a flat, slightly oversized plane at the support height for the minimal contact cue.

### Metadata

- **Source**: user_feedback
- **Related Files**: godot/src/ArenaVisualizer.cs
- **Tags**: godot, rendering, grounding, energy-block, camera

### Resolution

- **Resolved**: 2026-08-30
- **Notes**: Replaced the cylindrical cue with a flat plane, explicitly reset block rotation and scale, and verified low-angle 1280x720 and 1920x1080 captures.
