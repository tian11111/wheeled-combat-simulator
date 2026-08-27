# Frontend / Godot Shell Guidelines

The frontend template maps to the Godot 4 .NET desktop shell. There is no
React, browser, CSS, or server-state layer in this repository.

| Guide | Description |
|---|---|
| [Directory Structure](./directory-structure.md) | Godot shell organization |
| [Component Guidelines](./component-guidelines.md) | Scene/node and adapter boundaries |
| [Hook Guidelines](./hook-guidelines.md) | C# session/model helpers; React hooks do not apply |
| [State Management](./state-management.md) | Match session and immutable render state |
| [Quality Guidelines](./quality-guidelines.md) | Headless/parity and visual checks |
| [Type Safety](./type-safety.md) | C# records, DTOs, and nullability |

All guidance describes the current Godot shell and its tested, engine-free
adapters; do not infer browser conventions from these filenames.
