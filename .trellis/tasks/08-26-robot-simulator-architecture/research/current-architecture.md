# Current Architecture Research

## Sources

- `D:/project/robocup/robot-simulator/README.md`
- `D:/project/robocup/robot-simulator/CONTRACT.md`
- `D:/project/robocup/robot-simulator/SIMULATOR.md`
- `D:/project/robocup/robot-simulator/game_engine.js`
- `D:/project/robocup/robot-simulator/physics_adapter.js`
- `D:/project/robocup/robot-simulator/sim_server.js`
- `D:/project/robocup/robot-simulator/sim_runner.py`
- `D:/project/robocup/1779761830740288.pdf`

## Findings

- The prototype already has a deterministic decision core, a Three.js renderer, an optional Rapier bridge, a Node HTTP service, a Python client, external controller subprocesses, fixed seeds, and diagnostic traces.
- The deterministic CORE is embedded in `wushu_ring_sim.html` and copied into the 3D template by `build_3d.js`; this is the main maintainability boundary to remove.
- The existing contract explicitly requires raw sensor profiles, request-id isolation, fixed-seed evaluation, and truthful fidelity metadata. These are compatibility constraints, not optional redesign ideas.
- The PDF defines single-match rules and scoring, while tournament ranking and qualification are separate workflows. The first architecture milestone should cover the former only.
- Rapier is currently described as an auxiliary 3D collision layer; the deterministic core remains the rule and scoring authority. Changing that authority would require a separate fidelity/determinism project.
