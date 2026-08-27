# Type Safety

The shell is nullable-enabled C# and shares typed protocol records with the
core; runtime validation remains necessary for JSON input.

Use records/readonly structs for value-like render data (`Vec3`, `Pose2`,
`RobotVisual`) and typed DTOs from `Sim.Protocol`. Use `required` properties for
render records where absence is invalid and explicit nullable types for optional
snapshot fields.

Deserialize with `ProtocolJson`, then call the relevant `Validate()` before
applying a scenario or replay. Do not treat JSON parsing success as semantic
validity. Use domain helpers such as `Math.Clamp` instead of unchecked casts;
`SnapshotView` is the reference mapping.

Avoid `dynamic`, untyped dictionaries across layer boundaries, and
null-forgiving casts that bypass protocol validation.
