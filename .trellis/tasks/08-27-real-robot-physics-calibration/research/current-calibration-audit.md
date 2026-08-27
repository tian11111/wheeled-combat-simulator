# Current Calibration Audit

## Current Project

- `fidelity.json` explicitly waits for lateral slip, block sliding, collision, stall-label and mount telemetry.
- `VehicleProfile` already owns per-vehicle `latFrictionK` and `angDamping`.
- `Scenario.Parameters` maps legacy names to `SimParameters`, including `BLOCK_MU_K`, `COLLISION_RESTITUTION` and `STALL_SPEED`.
- Mount acceptance is still hard-coded in `PhysicsWorld` as `MountVMin = 0.3` and a 24-degree incidence-angle gate, so it cannot currently be calibrated through scenario data.
- No real telemetry file is present in the new or legacy repository. The only available calibration input is synthetic self-test data.

## Reusable Legacy Evidence

Source: `D:/project/robocup/robot-simulator/sim_calibrate.js` and `sim_calibrate_selftest.js`.

- Exponential least-squares fits recover lateral and angular decay constants.
- A bounded one-dimensional least-squares fit recovers block kinetic friction for the current discrete model.
- Relative normal velocity before/after impact recovers collision restitution.
- Labeled commanded-speed samples select a stall threshold.
- The tool validates timestamps and minimum sample counts, hashes its input, emits RMSE and a recommended patch, and updates fidelity only when explicitly requested.
- Mount trials are counted but never calibrated, which is the main functional gap for the current project goal.

## Planning Consequences

1. Port the algorithms and fixtures into the .NET solution instead of maintaining a second Node runtime.
2. Keep telemetry decoding and validation in one protocol owner; CLI commands must consume typed data.
3. Separate fit trials from held-out validation trials before any fidelity promotion.
4. Make mount thresholds explicit parameters, but only after defining a repeatable physical experiment and error target.
5. Preserve existing defaults and replay behavior when no calibrated profile is selected.
