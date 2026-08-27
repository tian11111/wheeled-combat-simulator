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

Review source-of-truth ownership, nullable handling, replay compatibility,
headless testability, and whether the UI remains a pure consumer of snapshots.
