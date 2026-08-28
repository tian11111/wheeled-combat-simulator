# Error Handling

Errors are explicit at boundaries and deterministic inside the kernel.

Protocol DTOs expose `Validate()` as an enumerable of stable human-readable
messages. Constructors and engine entry points reject invalid state with
`ArgumentException`/`ArgumentNullException`; see `Scenario.Validate()` and
`MatchEngine`.

The CLI validates once before fitting or writing. `CalibrateCommand` returns
exit code `1` for input/fitting/IO failures and `2` for usage errors; invalid
telemetry must not create a report or patch. `Program.Main` is the top-level
catch boundary (`src/Sim.Cli/Program.cs`).

There is no HTTP error envelope. CLI errors go to stderr with a concise reason;
successful artifacts are written as JSON files. Do not catch and silently ignore
validation failures.

Common mistakes: allowing non-finite actions into physics, writing output before
validation, or turning an unknown replay command into a state mutation. Additive
referee commands (e.g. `restart_robot:<role>`, see
`.trellis/spec/sim/restart-contract.md`) follow the same policy: the CLI warns,
the shell ignores — never reinterpret old replay bytes. Use the invalid telemetry
cases in `src/Sim.Tests/CalibrationPipelineTests.cs` as regression examples.
