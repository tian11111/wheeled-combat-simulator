# Directory Structure

The solution is split by runtime responsibility rather than HTTP layers.
There are no routes, controllers, ORM models, or database migrations.

```
src/Sim.Protocol    # DTOs, JSON converters, validation, versioned contracts
src/Sim.Core        # deterministic match engine, physics, sensors, FSM
src/Sim.Calibration # pure telemetry fitting and report models
src/Sim.Cli         # command parsing, file/process IO, replay/calibration commands
src/Sim.Tests       # xUnit regression and cross-layer tests
godot/src           # desktop shell and Godot-free adapters linked into tests
```

Put protocol changes in `Sim.Protocol`, deterministic behavior in `Sim.Core`,
pure calibration math in `Sim.Calibration`, and filesystem/process orchestration
in `Sim.Cli`. The core may reference Protocol, but must not reference CLI,
Godot, filesystem, network, or wall-clock APIs. See
`src/Sim.Core/MatchEngine.cs` and `src/Sim.Cli/CalibrateCommand.cs`.

Use one primary public type per PascalCase `.cs` file. Keep command helpers next
to their command and tests named after the behavior they protect.

Examples: `src/Sim.Core/Physics.cs`, `src/Sim.Protocol/Telemetry.cs`,
`src/Sim.Calibration/Fitters.cs`, and `src/Sim.Tests/ReplayHeaderTests.cs`.
