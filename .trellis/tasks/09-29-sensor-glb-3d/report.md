# 传感器真车标定与 3D 化 — 实施报告（2026-09-29）

## 结论

按 grilling 三轮共识完成"三层全做"：①探点位置重标至装配.glb 光电节点真值；②通道清单对齐真车 11 路（v2 场景显式启用 wheeledCombat11）；③传感器全面 3D 化（MuJoCo 侧 mj_ray 打真实碰撞几何，legacy 平面语义退化位不变）。CoreVersion → `sim-core-1.0.4` 并入 v2 重训身份。验证矩阵 6/6 通过；**吞吐软门未过（-99.35%），归因为行为性 reset 风暴而非 raycast 开销，按退出准则⑥停止下游、留数据待拍板**。

## 七决策执行情况（对照 design.md）

| 决策 | 执行 | 状态 |
|---|---|---|
| ① 范围三层全做 | 探点重标 + 11 路 + 3D 化全部落地 | ✅ |
| ② 位置真值=GLB 光电节点 | `tools/mesh/sensor_mounts.json`（sha256 校验源模型 9538408e…，双实现交叉校验 det 失差 <5.1e-7） | ✅ |
| ③ 后端接口 + legacy 平面退化 | `IPhysicsBackend` 3D 探测查询；`Physics.cs`/`PlanarSensors.cs` 平面语义包裹，legacy 事件流位不变（单测断言） | ✅ |
| ④ MuJoCo 全 raycast | `MujocoPhysicsBackend` mj_ray 打真实 geom（FOV 角度过滤+最近命中+命中面法线衰减）；灰度=探点垂线颜色场+高度差量程 | ✅ |
| ⑤ CoreVersion 1.0.4 并入 v2 重训身份 | `MatchEngine.CoreVersion = sim-core-1.0.4`；fixture 同步 | ✅ |
| ⑥ 吞吐软门 ≤15% 退出准则 | -99.35% → 优化一轮 → 复测仍超 → 停，报告数据 | ⚠️ 未过（见下） |
| ⑦ Legacy14 冻结 / WheeledCombat11 只改数值 | 评审确认 Legacy14 一字未动；id 不变 | ✅ |

## 关键数值决策（挂点真值）

新文件 `tools/mesh/extend_vehicle_mounts.py`：严格 glTF TRS 世界矩阵合成（禁止 AABB 近似），`to_body` 映射（bodyX=−modelZ, bodyY=−modelX, bodyZ=modelY）。与计划映射表的差异：

- **巡台光电×4** 实测全在车头两舷（每舷 2 个），按"舷×前后位"分配 fl/fr/rl/rr——原四象限法会让两节点撞"前左"；零件名左右与车体左右相反，一律按 body 坐标分配。
- **灰度 4 路无可靠节点对应**（候选节点横向 0.101~0.115 超出投影、z 在轮轴平面上方非贴地）→ 工程默认 ±0.06、离地 2.5mm；候选节点原始矩阵保留在 JSON `nodes.*` 供溯源。
- **朝向未编造偏角**：巡台两舷旋转互为镜像（~28.7° 与 ~45.2° 两族），轴向证据存 JSON，探头朝向由 profile 判定。
- `支架.stp-1` 命中但无通道用途，仅记录。

## mj_ray 探针（tmp/rayprobe，gitignored）

C# P/Invoke + Python ctypes 交叉验证；dll SHA-256 与 `MujocoNative.cs` 门控值一致，mj_versionString=3.14.0；box 与 mesh geom 均命中。→ MuJoCo 传感器走真实几何 raycast，"传感器所见=物理所碰"落地。

## 评审与修复

独立评审第 1 轮发现 3D 光束方向 **yaw 双算**（`MujocoPhysicsBackend.BeamDirection` 把世界系方位角再旋转车体航向，偏差恒等于 θ；同一 `ProbeQuery.Angle` 在平面分支按世界系消费正确，两分支语义矛盾）→ 已修复，全量复测通过。

## 验证矩阵（6/6 通过）

| 项 | 结果 | 证据 |
|---|---|---|
| 全量 dotnet test（-m:1） | 通过 | 418/418，5s |
| replay-check replays/seed-42.json | 通过 | 位对位复现，scores 4:49，events 752/752 |
| replay-check replays/godot-parity-seed42.json | 通过 | 同上 |
| mbri 自测 | 通过 | 16/16 |
| score_block_rl 自测 | 通过 | 44 passed, 0 failed, 8 skipped |
| Godot 构建+parity | 通过 | 真实 console exe（WinGet 包路径）4.7.2 headless 构建 + parity exit 0；注意 parity 文件需绝对路径（Godot 会切工作目录，相对路径 exit=2） |

## 吞吐软门（R5）：未过，按退出准则⑥停

- 基线（09-27 归档任务）：3 套墙钟 1247.031/1316.672/1317.766s，中位 1316.672s，每套 5×501,760 transitions → 2.624 ms/trans ≈ 381.1 trans/s。
- 本次：**-99.35%**。归因：**不是 raycast 计算开销**，而是 wheeledCombat11 的 3D 传感器语义改变 FSM 行为引发 reset 风暴（旧 v4 策略在新传感器语义下 episode 频繁失败复位）。
- 退出准则执行记录：优化一轮（口径 postfix-v2-wc11-4096-rerun，五 seed 复测）→ 仍超门 → 按决策⑥停止下游，报告数据。
- **解读**：软门设计假设"行为不变+开销增大"；身份变更场景下该门测到的是行为崩坏——这正是"1.0.4 需要重训"的直接证据，而非性能回归。重训口径下的吞吐要在新策略收敛后重测。

## CoreVersion 影响清单

- `sim-core-1.0.3 → sim-core-1.0.4`；MuJoCo 回放创建/校验门禁随 CoreVersion 收紧。
- `replays/seed-42.json` 与 `godot-parity-seed42.json` 均为 legacy 回放，不受门禁影响，实测 PASS。
- 既有 MuJoCo 回放（1.0.3 时代）按门禁自然失效——预期行为，重训后以新身份重录。
- `SensorChannel.Height` 为协议 add-only（按 id 引用展开 + 序列化恒写完整 profile），旧 wire 形状不变。

## 披露（低置信/未覆盖）

- 灰度 4 路挂点为工程默认（非 GLB 实测）；巡台探头朝向未编造具体偏角——待实车核对后改数值重跑挂点脚本即可快速更新。
- 桌面端人工目检（登台探测 / scan 避边 / 翻覆停车）未做——需真实窗口。
- RL 重训与 final_holdout_v4 盲验——独立训练窗口工作，本次只到训练冒烟。
- 工作流运行报告（含完整挂点表与吞吐复测命令）见本次会话 artifact「传感器真车标定与 3D 化实施报告」。

## 后续

1. 用户拍板：接受 1.0.4 身份并安排重训窗口（v5），或对吞吐门另行决策。
2. 桌面目检：真实 Godot exe `--path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco-v2.json`，观察尾铲登台信号、scan 避边、翻覆停车。
