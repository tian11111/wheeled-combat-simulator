# Implementation Plan

## Ordered Checklist

1. Create the .NET solution and Godot 4 .NET project under `D:/project/robot-simulator`; copy only required reference assets/data from the old prototype, never its generated browser entry point.
2. Build `Sim.Protocol` DTOs, JSON validation, version fields, and fixtures for observations, actions, snapshots, events, scenarios, and replay headers.
3. Build `Sim.Core` as a DOM/engine-independent deterministic kernel: state, scenario/profile normalization, fixed-step loop, motion/contact, sensors/perception, referee scoring, rewards, and event log.
4. Port the old prototype's fixed-seed scenarios and create trace-comparison tests for mount, drop/recovery, block scoring, simultaneous drop, inactivity, restart penalties, and match timeout.
5. Build `Sim.Cli` for headless matches, fixed-seed batch evaluation, replay recording/replay, and structured diagnostics. Add Python JSONL process adapter while preserving `decide(obs) -> {v,w}`.
6. Create the Godot arena scene and typed view adapter. Add cameras, robot/block visuals, status/HUD, event log, pause/restart controls, and replay timeline; all state changes go through core commands.
7. Add Godot-to-core integration tests that run the same seed/actions as `Sim.Cli` and compare final scores/events. Keep Godot physics diagnostic-only.
8. Document the new architecture, CLI commands, controller protocol, fidelity boundaries, and installation prerequisites. Add migration notes from the old `D:/project/robocup/robot-simulator` prototype.
9. Run the full .NET test gate, headless replay checks, and Godot project validation once Godot 4 .NET is installed.

## Validation Commands

From `D:/project/robot-simulator`:

```powershell
dotnet build
dotnet test
dotnet run --project src/Sim.Cli -- match --seed 42 --duration 120
dotnet run --project src/Sim.Cli -- replay-record --seed 42 --out replays/seed-42.json
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
godot --headless --path godot --editor --quit
git diff --check
```

The Godot commands are conditional on Godot 4 .NET being installed. Before claiming parity, compare final scores, terminal states, event sequence, and `diagnostic-v1` action/sensor fields against the old prototype's fixed-seed baseline.

## Risky Files / Rollback Points

- `src/Sim.Core`: port in small rule slices; retain trace fixtures and revert only the slice that changes behavior unexpectedly.
- `src/Sim.Protocol` and Python adapter: do not remove legacy aliases until bridge fixtures pass.
- `godot/`: keep rendering changes isolated from core and replay tests; a broken scene must not block headless evaluation.
- `scenarios/` and replay fixtures: version changes explicitly; never overwrite a baseline fixture silently.

## Out of Scope for This Task

- Browser UI, Three.js, HTML build pipeline, or Web deployment.
- Full tournament scheduling, group ranking, knockout brackets, or appeals.
- Microservices, remote/cloud execution, persistent match database, or multi-user collaboration.
- Unity migration, authoritative 3D physics, real-robot physical calibration, or new YOLO models.
- Second-phase visual polish and custom art production.

## Completion Status (2026-08-26)

| # | 条目 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | 解决方案 + Godot 工程骨架 | ✅ | `RobotSimulator.sln`、`godot/project.godot`（旧原型仅作参考，未复制其浏览器产物） |
| 2 | Sim.Protocol DTO/校验/固件 | ✅ | 协议测试 + `src/Sim.Tests/fixtures/` 旧基线（`tools/legacy-baseline.js` 再生成） |
| 3 | Sim.Core 确定性内核 | ✅ | 7 步流水线逐行移植；`dotnet test` 全绿 |
| 4 | 固定 seed 场景与回归测试 | ✅ | `MatchEngineTests`（确定性/登台/推块/同帧掉台/消极/判罚/超时）、`scenarios/wushu-ring-2026.json`、`docs/PORTING_NOTES.md` |
| 5 | Sim.Cli 无头评测/回放 + Python 适配器 | ✅ | `match`/`replay-record`/`replay-check`；`CliTests`；`controllers/example_controller.py` 实测回放逐位一致 |
| 6 | Godot 场景 + 视图适配器 | ⚠️ 脚手架 | `godot/src/SnapshotView.cs`（纯、已纳入单测）；场景/脚本待 Godot 4 .NET 安装后验证 |
| 7 | Godot↔CLI 一致性测试 | ⏸ 阻塞 | 依赖 Godot 安装；方案见 `godot/README.md` |
| 8 | 架构/协议/CLI/保真度/迁移文档 | ✅ | `README.md`、`docs/*`、`fidelity.json` |
| 9 | 完整测试门禁 + 回放校验 | ✅（.NET 部分） | 89/89 测试通过；两条回放 `PASS`；Godot 验证待安装 |

阻塞项（6 的编辑器验证、7、9 的 Godot 部分）源于同一外部依赖：本机未安装 Godot 4 .NET。
内核/协议/CLI 的验收标准已全部满足：2 分钟无头完整比赛、同种子逐位一致、
`decide(obs)->{v,w}` 可替换控制器、固定种子回归、保真度声明。
