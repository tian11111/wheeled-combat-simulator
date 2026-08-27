# Directory Structure

Godot code is organized by shell responsibility under `godot/src`.
Pure adapters are kept free of Godot namespaces so they can be linked into
`Sim.Tests`; rendering and input code may depend on Godot.

```
godot/src/SnapshotView.cs    # pure Snapshot -> RenderFrame projection
godot/src/MatchSession.cs    # replay/live session lifecycle
godot/src/ParityCheck.cs     # headless cross-end validation
godot/src/LayoutDraft.cs     # pure layout editing model and serialization
godot/src/ArenaVisualizer.cs # Godot rendering/input orchestration
```

Keep rendering consumers downstream of `SnapshotView`; keep layout edits in
`LayoutDraft` and apply them by building a new `Scenario`. Do not mutate a
running `MatchEngine` from editor UI code. Use PascalCase C# names and suffix
pure projections/models with `View`, `Draft`, or `Check` when appropriate.

Examples: `godot/src/SnapshotView.cs`, `godot/src/LayoutDraft.cs`, and
`godot/src/ParityCheck.cs`.
