# 执行计划

## 阶段 1（可行性实验，全部产物在 tmp/，不入库）

1. **几何提取**：`tmp/mesh_extract.py` 解析 `装配.glb`，筛出后铲 / 底板 / 上板 / 铲臂，做坐标变换（`bodyX=−mZ, bodyY=−mX, bodyZ=mY`）与凸包简化，导出 STL 到 `tmp/assets/`；打印每件的车体局部包围盒（校验：后铲应在 +X 尾部、轮位应在 ±0.075/±0.115）。
2. **MJCF v2 生成**：`tmp/gen_mjcf_v2.py` 复刻 v1 场地（地面/平台/围栏/倒角/能量块）并把车体替换为 mesh 件 + 真轮尺寸；输出 `tmp/v2-us.xml`（用于单车登台实验）。
3. **仿真实验**：用 MuJoCo 跑"倒车登台"动作序列（对齐 FSM 的 `MOUNT_RING`：摆正 → 倒车 0.585 m/s → 直到上台或超时），记录后铲/前臂与台沿的接触 tick、车体爬升高度、是否 `on_stage`。
   - 判定：后铲是否真的先接触台沿；真轮能否越过高 6 cm 的台沿；若不能，量化还差多少（最大爬升高度、打滑位置）。
4. **对比实验**（必要时）：调整候选（倒角角度/延伸、`WheelForceLimit`、`WheelServoKv`）各跑一遍，记录每个候选的成功/失败与代价。
5. **性能基线**：v1 vs v2 单 tick 耗时（同一场次、同 seed）与 reset 耗时。

## 阶段 2（正式实现，入库）

6. `src/Sim.Mujoco/assets/*.stl` 入库 + `Sim.Mujoco.csproj` 拷贝/嵌入；`MujocoNative` 增 VFS P/Invoke；`MujocoModel` 增 v2 分支（几何 + 哈希含资产）；`PhysicsSpec` 增 `wushu-mjcf-v2`。
7. 新场景 `scenarios/wushu-ring-2026-mujoco-v2.json`（真车 `vehicles` 段与 glb 实测一致）。
8. 测试：新增 geom 断言测试（后铲/轮尺寸位置、哈希随资产变化）；更新受影响的 MuJoCo 用例预期；`SearchTurnCompensationTests` 在 v2 下重新做候选对照。

## 阶段 3（验证与收尾）

9. `dotnet test` 全量 + `mbri_adapter_selftest` + `score_block_rl/selftest` + v1 `replay-check`（必须仍 PASS）+ v2 replay 录制/校验 + Godot 构建 + 无头 parity + 视觉截图（真车几何下的登台姿态）。
10. `report.md`：几何/参数决策记录、登台证据、性能对比、作废范围（RL replay/checkpoint）与后续动作（重训、重评、冻结协议重走）。
11. 归档 + 期刊。

## 验证命令

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"
py -3.12 tmp/mesh_extract.py                     # 阶段 1
py -3.12 tmp/gen_mjcf_v2.py && py -3.12 tmp/run_v2_mount.py   # 阶段 1 实验
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli --no-build -- replay-check replays/seed-42.json
git diff --check
```

## 约束

- 阶段 1 的几何/参数**不改生产代码**；所有结论以数据钉住后才进阶段 2。
- 场地几何（倒角）或力矩若需调整，必须作为独立决策记录（前后数值 + 原因），不得顺手改。
- 不为了让旧 replay 通过而绕过 `physicsModelSha256` 校验。
