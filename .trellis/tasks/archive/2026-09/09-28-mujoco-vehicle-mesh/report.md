# 验收报告：真车几何进 MuJoCo 物理（wushu-mjcf-v2）

## 结论

真车几何（后铲 + 底盘凸体）与真实轮径已落进生产代码并通过全部门：新增 9 条几何/身份断言测试，全量 **402/402** 通过；v1 路径逐字节不变（v1 模型哈希仍等于记录值 `1ad75271…`，`replay-check` 逐位 PASS）。提交 `c74cd0c`。

**一条重要纠错**：任务早期记录的轮径 `r=0.046`（及"四轮直径 0.0918/0.0845/0.0812 不一致"）是错误数据，根因是 `tmp/inspect_glb.py` 的"局部 AABB 八角点旋转法"对旋转节点按相位假膨胀。独立复核（自写 glTF 解析）与主代理复核（节点世界矩阵列范数）双重确认：glb 内**非单位缩放 0 个、非单位四元数 0 个**，四轮世界尺寸一致 = 直径 0.0650 m（**r=0.0325**）、宽 0.029 m；轮距 0.229、轴距 0.150。落地的 v2 实现按实测值，`prd.md`/`design.md` 已回写更正。

**用户决策（2026-09-28）**：按实测轮径（真车 6.5 cm 轮）在 6 cm 台沿下实测"能爬上去但站不稳"（末态半悬、`on_stage` 仅 1 帧）。用户选择**接受真车登台困难**：不动物理（不改倒角/力矩），把它当作真实约束，后续由 FSM/规则适应这类状态（翻覆/半悬时停车等裁判重启）。

## 登台实验（证据：tmp/run_v2_mount.py、tmp/v2/experiment-summary.json、tmp/v2/data/*）

| 候选 | 后铲触沿 | 最大爬升 (m) | 上台 | 观察 |
| --- | --- | --- | --- | --- |
| base：r=0.046 / 20° 倒角 / 3.0 N·m | 否 | 0.0776 | 是 | 四轮单独完成（后轮 tick26/sub5 先触倒角）；后铲零接触（与倒角最小间隙 0.0175 m）。**r=0.046 实测不存在 → 此结论作废** |
| wheel065：**r=0.0325（真车）** / 20° | **是** | 0.1087 | **否（半悬）** | 后铲 tick24/sub8 触倒角、穿透 0.84 mm、0.04 s 后被抬离；末态 y=0.6520 前轮仍在倒角上、`on_stage` 仅 1 帧；\|pitch\|max 36.6°、\|slip\|max 0.863 m/s |
| step6：r=0.046 / 无倒角（6 cm 竖沿） | 是 | 0.0163 | 否 | 100% 打滑、车体位移 6.8 mm；**r=0.046 也滚不过竖沿**（沿顶 0.06 > 轴高 0.046） |
| chamfer15：r=0.046 / 15° 倒角 | 否 | 0.0775 | 是 | 更平缓（pitch 15.1° vs 20.2°），说明倒角是有效自由度（本轮不采用） |
| base_far：基线起点远 0.10 m | 否 | 0.0781 | 是 | 复现性检验，与 base 一致 |
| torque_cal：forcerange=0.2 平地阶跃 | — | — | — | 标定实验：0.2 让车 1 tick 到 0.287 m/s；若是 0.8 N 力只能给 0.04 m/s（差 7 倍）⇒ `forcerange` 在 hinge 上是**力矩 N·m**（单次实验推断，未独立重跑） |

复核员的半径扫描结论：后铲"零接触"仅对 **r≳0.039** 成立；r≤0.038 起后铲碰台面——真车 r=0.0325 落在"会碰"一侧。

## 落地内容（提交 c74cd0c）

- 资产：`src/Sim.Mujoco/assets/{robot_chassis,robot_rear_shovel,robot_front_arm}.stl`（668/4166/3766 三角面），`tools/mesh/extract_vehicle_mesh.py` 可从 glb 复现，csproj 随构建产出。
- `MujocoNative`：内存 VFS（`mj_defaultVFS` / **`mj_addBufferVFS`** / `mj_deleteVFS`）——design.md 写的 `mju_addBufferToVFS` 在 3.14.0 DLL 中不存在（核对 797 个导出符号）。
- `MujocoModel` v2 分支：chassis + rear_shovel 凸体 geom + 四真轮；`ModelSha256` 并入资产字节（改一字节哈希必变、改名被拒）。
- 新场景 `scenarios/wushu-ring-2026-mujoco-v2.json`：`modelVersion=wushu-mjcf-v2`，足迹 0.103/0.16949/0.12185、wheelBase 0.150、trackWidth 0.229、mass 1.0。
- 测试 `MujocoVehicleMeshTests`（9 条）：从编译后的 mjModel 读 geom 类型与尺寸、`mj_geomDistance` 量前后轮距 = 0.15−2r / 左右 = 0.229−0.029 / 四轮离地 = 0.5−r、后铲网格最低边 z=−0.01245 低于底盘 −0.00825、资产字节敏感哈希、v1 场景哈希不变。

## 验证门

```text
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1     402 passed / 0 failed（基线 393 + 9）
replay-check replays/seed-42.json                   PASS（4:49 / 752 events）
replay-check .sim_runs/.../replay-seed-42-mujoco-v3.json  PASS（8:3 / 117 events）
git diff --check                                    干净
dotnet build RobotSimulator.sln / godot/GodotSim.csproj  0 错误
mbri_adapter_selftest / score_block_rl selftest     ALL PASSED / 44 passed 0 failed
```

## 未覆盖与后续

- **Godot 桌面端视觉/parity**：v2 几何未跑 Godot 构建外的截图与 parity（本轮未做）。
- **RL**：物理身份已变（`physicsModelSha256` 变），既有 checkpoint 作废，重训/重评不在本轮；`SearchTurnCompensationTests` 的补偿系数 4.0 是为 v1 几何标定的，v2 下未重标。
- **复核覆盖度**：独立复核未重跑 `torque_cal` 的 flat 相位、未验证"两次运行 CSV SHA-256 相同"的声明；`base_far` 摘要 note 里的 finalY 数字与数据文件不符（0.8082 vs 0.7759307，笔误级）。
- **下一步（用户已确认方向）**：真车登台困难作为真实约束接受；翻覆/半悬等"失去行动能力"状态由 FSM/规则处理（停车等待裁判重启），另立任务实现。
