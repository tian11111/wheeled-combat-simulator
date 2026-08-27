# Hook Guidelines

React-style hooks are not used. The analogous reusable stateful helpers are
plain C# session/model classes.

Use `MatchSession` for replay/live lifecycle and `LayoutDraft` for editable
layout state with undo/redo. Keep pure transformations in static helpers such
as `SnapshotView`. There is no network data-fetching layer; files are loaded
explicitly by the session/editor boundary and validated as protocol objects.

Do not invent `use*` abstractions or hidden per-frame global state; name helpers
after the domain operation they perform. See `godot/src/MatchSession.cs`,
`godot/src/LayoutDraft.cs`, and `godot/src/SnapshotView.cs`.
