# Backend / CLI Development Guidelines

This repository has no web backend or database. The backend template maps to
the headless .NET command layer and the IO boundary around simulation
libraries. Keep deterministic-kernel rules in [sim](../sim/index.md).

| Guide | Description |
|---|---|
| [Directory Structure](./directory-structure.md) | .NET project ownership and IO boundaries |
| [Database Guidelines](./database-guidelines.md) | Why database guidance is not applicable |
| [Error Handling](./error-handling.md) | CLI and protocol error behavior |
| [Quality Guidelines](./quality-guidelines.md) | Build, test, and determinism gates |
| [Logging Guidelines](./logging-guidelines.md) | CLI output and controller diagnostics |

All guidance describes the current codebase, not a proposed web stack.
