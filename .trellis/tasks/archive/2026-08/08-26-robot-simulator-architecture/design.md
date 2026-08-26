# Technical Design

## Decision

Build a desktop simulator in `D:/project/robot-simulator` with Godot 4 .NET as the 3D shell and a separate .NET 8 class library as the authoritative simulation core. Do not use a database or microservices for the local single-match workflow. The existing project at `D:/project/robocup/robot-simulator` is behavior/protocol reference material, not the new product entry point.

Godot is preferred over Unity here because it is lightweight, open-source, scriptable, supports desktop export and headless execution, and avoids locking the project to a proprietary editor/runtime. The core remains engine-independent so the choice can be revisited without rewriting rules or controller adapters.

## Repository Layout

```text
D:/project/robot-simulator/
├─ src/Sim.Core/          # .NET 8 deterministic match kernel
├─ src/Sim.Protocol/      # versioned DTOs and validation
├─ src/Sim.Cli/           # headless match/evaluation/replay commands
├─ src/Sim.Tests/         # deterministic and rule regression tests
├─ godot/                 # Godot 4 .NET desktop client
│  ├─ scenes/             # arena, robots, blocks, HUD
│  ├─ scripts/            # view models, input, camera, adapters
│  └─ project.godot
├─ controllers/           # Python examples and JSONL bridge helpers
├─ scenarios/             # versioned field/profile fixtures
└─ replays/               # local generated files, ignored by Git
```

## Core Boundaries

### `Sim.Core`

Pure .NET code with no Godot, filesystem, network, or process dependencies. It owns scenario/profile normalization, seeded random streams, fixed-step motion and contact resolution, sensor models, vision-cache reads, match lifecycle, referee scoring, rewards, events, snapshots, and replay input recording.

### `Sim.Protocol`

Versioned JSON contracts for `Observation`, `Action`, `Snapshot`, `Event`, `Scenario`, and `ReplayHeader`. Preserve `decide(obs) -> {v,w}`, legacy `sensors` aliases, request IDs, timeout semantics, and `diagnostic-v1` field meanings. New strategy code should use `rawSensors` and `sensorLayout`.

### `Sim.Cli`

Runs the same core without Godot for batch evaluation, deterministic regression, replay verification, and CI. It starts Python controller processes when requested and records accepted actions, process faults, and timing metadata.

### Godot client

Godot renders the arena, robots, stage, energy blocks, cameras, HUD, event log, and replay controls. It consumes immutable snapshots/events and sends typed commands (`arm`, `pause`, `resume`, `restart`, `scene`, `step`, `loadReplay`). It must not mutate core entities or duplicate scoring logic.

### Controller adapter

The core asks an adapter for an action from an observation. Built-in FSM and manual control are in-process; user strategies run as Python child processes using line-delimited JSON. The adapter owns process lifetime, request IDs, deadlines, late-action rejection, and zero-action fallback.

## Tick Semantics

Use a fixed `0.05s` simulation tick. Each tick:

1. Construct observations from the last committed state.
2. Resolve controller actions, request IDs, latency queues, limits, and timeout fallbacks.
3. Integrate deterministic motion and solve robot/block/stage contacts.
4. Evaluate drop/mount/contact conditions and emit domain events.
5. Apply referee scoring, countdowns, penalties, inactivity, and terminal transitions.
6. Sample sensors and perception metadata for the next observation.
7. Commit snapshot, reward delta, and monotonically numbered events.

Godot's render loop interpolates between committed snapshots. `Sim.Cli` advances ticks directly. Rendering stalls or closing the client must not change the simulation clock.

## Physics Policy

The deterministic 2D model in `Sim.Core` is authoritative for scores, replay, and AI observations. Godot physics is used for visual scene placement and optional contact diagnostics only. A future Jolt/3D authoritative mode would require an explicit mode/version and separate determinism/fidelity evidence; it is not part of this task.

## Replay and Diagnostics

Replay files contain a header with ruleset id, seed, parameters, vehicle profiles, field-gray id/hash, vision mode, core version, and accepted controller actions/commands by tick. They may include sampled snapshots for inspection. Replaying the same header and inputs must reproduce the same event sequence and final scores. Keep `diagnostic-v1` distinctions between requested actions, applied actions, actual velocity, raw sensors, and rewards.

## Compatibility and Migration

- Port behavior from the old HTML CORE without treating HTML as source code in the new repository.
- Keep the external Python `decide(obs) -> {v,w}` contract and legacy observation fields.
- Preserve fixed-seed rule scenarios and fidelity metadata semantics.
- Provide a small import/compare tool for old JSON traces during migration; do not keep a browser build pipeline.
- Keep generated replay files and local evaluation artifacts out of source control.

## Risks and Rollback

- **Godot/.NET availability:** core and CLI tests remain runnable with the installed .NET SDK; the desktop client requires Godot 4 .NET before graphical validation.
- **Physics divergence:** keep Godot physics non-authoritative and expose the active mode in diagnostics.
- **Behavior drift during port:** first create trace comparison fixtures against the old prototype, then extract modules without intentional rule changes.
- **Python protocol breakage:** validate at the adapter boundary and retain request-id/timeout regression tests.
- **Scope creep:** defer tournament management, networking, database storage, and visual polish to separate tasks.
