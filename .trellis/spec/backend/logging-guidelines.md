# Logging Guidelines

The project uses deterministic console diagnostics, not a logging framework.
`Console.WriteLine` is for command results and summaries; `Console.Error` is
for usage errors, validation failures, warnings, and controller faults.

There are no configured log levels or structured logger sinks. Keep output
stable enough for CLI tests and scripts; do not print per-tick noise by default.
Include the command, path, exit-relevant reason, and useful counts or hash
prefixes. `CalibrateCommand` and `PythonBridge` are reference sites.

Never log secrets, full controller payloads, or nondeterministic timestamps in
the simulation event stream. Authoritative events are emitted through
`EventBus`, not through logging.
