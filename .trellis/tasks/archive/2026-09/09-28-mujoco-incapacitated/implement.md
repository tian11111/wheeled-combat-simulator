# 执行计划

1. `src/Sim.Core/RuntimeState.cs`：`FsmState.Incapacitated` + `FsmStateNames.ToWire` + `FsmRuntime.FlipT/UprightT`。
2. `src/Sim.Core/IPhysicsBackend.cs` + `Physics.cs`（legacy false）：新增 `IsFlipped`。
3. `src/Sim.Mujoco/MujocoPhysicsBackend.cs`：`CopyStateToRuntime` 记录 `upright = 1 − 2(qx²+qy²)`；`ResetRobot` 置 1；实现 `IsFlipped`。
4. `src/Sim.Protocol/Event.cs`：`EventKind.Incapacitated`。
5. `src/Sim.Core/Fsm.cs`：`FsmTickFor` 里 CrisisGate 之后的翻覆门 + `Incapacitated` tick + 恢复分支。
6. `src/Sim.Core/MatchEngine.cs`：`CoreVersion` → `sim-core-1.0.3`；`ResetRobotToStart` 清 `FlipT/UprightT`。
7. `src/Sim.Tests/IncapacitatedTests.cs`：三条测试（判定单测、MuJoCo 集成、legacy 不变）。
8. 验证：`dotnet test` 全量 + `replay-check` + `git diff --check`；跑一次 MuJoCo 场景确认翻覆后停车的行为（可选：rl-env 采样）。
9. `report.md`：结论 + 事件轨迹证据 + CoreVersion 影响清单 + RL 语义变化 + 未覆盖（半悬/卡死、v2 下的实际翻覆率）。
10. 提交（独立批次）→ 归档 → 期刊。

## 验证命令

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- replay-check replays/seed-42.json
git diff --check
```

## 硬约束

- legacy 场景必须逐位不变（`replay-check` PASS 是唯一判据）。
- 不得为了让旧 MuJoCo replay 通过而放宽 `CoreVersion` 比较——升版本是诚实标识。
- 不为 v2 真车几何调整任何物理参数（用户已接受登台困难）。
