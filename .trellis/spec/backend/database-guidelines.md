# Database Guidelines

Database access is not part of this project. Do not introduce an ORM, migration
runner, connection pool, or persistence service for simulation concerns.

Simulation state is held in memory by `MatchEngine`; scenarios, reports, and
replays are explicit JSON files handled at the CLI boundary.

Use `ProtocolJson.Serialize`/`Deserialize` and typed DTO validation. Do not use
ad-hoc string manipulation for protocol JSON.

There are no migrations. Adding a persisted field is a protocol-evolution task:
preserve existing JSON shape and add fields or versions additively. DTO
properties use PascalCase in C# and serialize as camelCase.

Common mistake: placing file IO in `Sim.Core`. Keep writes in `Sim.Cli` or the
Godot shell; core tests must run without an engine or external services.
