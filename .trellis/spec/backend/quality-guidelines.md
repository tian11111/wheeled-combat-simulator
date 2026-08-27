# Quality Guidelines

Backend/CLI changes are held to the deterministic regression bar.

Do not add a web framework, global mutable singleton, blocking IO to
`Sim.Core`, or broad exception swallowing. Do not alter protocol JSON names or
replay event ordering in a convenience refactor.

Keep parsing and IO at the command boundary, use typed DTOs and `Validate()`,
and cover new behavior with xUnit tests. Reuse `ProtocolJson`,
`Scenario.Validate`, and existing command helpers before adding utilities.

Run `dotnet build`, `dotnet test`, and for behavior affecting the kernel or
protocol run `dotnet run --project src/Sim.Cli -- replay-check <fixture>`.
Determinism-sensitive changes also require the Godot parity check in
`.trellis/spec/sim/index.md`.

Review boundary ownership, error exit codes, additive protocol compatibility,
test coverage, and whether old seed-42 replay fingerprints remain unchanged.
