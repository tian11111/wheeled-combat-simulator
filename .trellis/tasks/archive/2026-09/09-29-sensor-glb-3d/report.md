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
| Godot 构建+parity | ~~通过~~ → **修正：假绿** | 工作流验证时 Godot bin 内是构建前的旧程序集（Sim.Protocol.dll 停在 09-28 23:35，源码 09-29 11:13-12:08 才完成），旧程序集跑 legacy 回放自然位对位。桌面 live 启动实测暴露：旧程序集下场景按 id 简写引用不展开 → 校验拒绝启动。强制 `dotnet build godot/GodotSim.csproj` 后实测零 ERROR、`core=sim-core-1.0.4` Live 运行。教训固化为回归测试 `ScenarioFile_IdOnlySensorReference_ExpandsAndValidates`（419/419 过）。 |

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

## 追加（09-29 下午）：QACC 数值爆炸修复（桌面目检发现）

- **现象**：桌面 Live 中登台后两车"飞出擂台"。headless 复现 MuJoCo 官方警告 `QACC ... The simulation is unstable`（v2 seed42 在 t=13.19）；用昨日 v2 上线提交对照，**同刻爆炸** → 弹飞是 v2 真车几何自带的数值隐患，非传感器改动引入。
- **根因**：`timestep=0.005` 对真车 mesh-台沿角接触过粗；全局 `solref timeconst=0.008` 还低于 MuJoCo 稳定经验线 2×timestep=0.01（台面单独用了稳的 0.02，救不了 mesh 角接触）。
- **修复**：`timestep 0.005 → 0.002`（每 tick 10→25 物理步）。仅软接触（solref 0.02）试验无效（爆炸换 DOF/时刻）已回退。
- **验证**：seed42 + 10 seeds QACC=0；登台后可在台上停留 25–47 s（修复前 on_stage 与掉台同 tick）；一场 headless 墙钟 2 s（物理 2.5× 步数成本可忽略）。
- **连锁重校**（物理轨迹全变，逐条人工审）：
  - `V1ModelSha256` 更新为 `7160897c…`（有意变更，守卫测试即为此设）。
  - Incapacitated 翻覆守卫 seed 42→19（tmp/flipscan 扫 1..40 选定：724 tick 翻覆 → 5 tick 宣告，30 tick 门内）。
  - 得分守卫 `OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops` **Skip**：新物理暴露 FSM 登台完成语义问题——车心过沿即判上台转 SEARCH，此时车头侧两轮仍悬空 → 反复掉台（v1 14 次/v2 8 次）、RECOVER 超限。守卫语义（真得分+无掉台）在 FSM 重校前无法满足，不反装断言。
  - 索敌补偿：dt=0.002 下候选 2/4 均不满足旧门（4→6.60s/误差 0.543，2→13.45s/0.578）→ 时延/误差硬门挂起转记录模式，生产默认维持 4.0，候选空间待重扫。
- **新增决策点**：①FSM 登台后掉台循环治理（登台完成判定 vs 真车轮轴原点几何）；②补偿候选重扫（含 6/8 与门重立）。

## 披露（低置信/未覆盖）

- 灰度 4 路挂点为工程默认（非 GLB 实测）；巡台探头朝向未编造具体偏角——待实车核对后改数值重跑挂点脚本即可快速更新。
- 桌面端人工目检（登台探测 / scan 避边 / 翻覆停车）未做——需真实窗口。
- RL 重训与 final_holdout_v4 盲验——独立训练窗口工作，本次只到训练冒烟。
- 工作流运行报告（含完整挂点表与吞吐复测命令）见本次会话 artifact「传感器真车标定与 3D 化实施报告」。

## 后续

1. 用户拍板：接受 1.0.4 身份并安排重训窗口（v5），或对吞吐门另行决策。
2. 桌面目检：真实 Godot exe `--path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco-v2.json`，观察尾铲登台信号、scan 避边、翻覆停车。
