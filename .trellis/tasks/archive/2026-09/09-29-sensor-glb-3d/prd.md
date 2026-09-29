# 传感器真车标定与3D化：探点重标 GLB 光电节点 + 11 路通道 + raycast 传感器

## Goal

MuJoCo v2 已把碰撞体换成装配.glb 实测真车外形（frontExtent=0.103 / rearExtent=0.16949 / sideExtent=0.12185，含尾铲；`scenarios/wushu-ring-2026-mujoco-v2.json` 场景名即记录此实测），但传感器层仍停留在旧默认车时代，产生三类失真：

1. **探点位置失真**：传感器仍跑 Legacy14（旧默认车标定，`src/Sim.Protocol/Profiles.cs:304`）。以真车足迹衡量：sFL/sFR/uL/uR 探点悬在车头外 37mm（探点 forward=0.14 > frontExtent=0.103）、gF 超车头 7mm、gB 距尾 59mm——灰度/铲下探点不在车体投影内，读数代表的是"车前方的空地"而不是"车底的位置"。
2. **通道清单失真**：真车实配 11 路（底盘灰度×4 + 对角数字 IR×4 + 铲下 IR×2 + 铲前 IR×1 = WheeledCombat11 语义，`Profiles.cs:352`），但生产从未启用——v2 场景 vehicles 未配 `sensors` 字段，运行时回落默认 legacy14。
3. **维度失真**：传感器模型是 2D 平面投影（`src/Sim.Core/Sensors.cs` 的 `SensorPoint` 只取 XY），半悬/翘头等姿态下探点 XY 若仍落在台面投影内就照报"在台面"，登台临界姿态的感知与物理事实脱节；MuJoCo XML 无任何传感器定义，全部探测在核心层 SensorSampler 解析计算（`irProbeFor`/`edgeProbeFor`/`graySpotSample`），与 MuJoCo 实际碰撞几何无关——"传感器所见 ≠ 物理所碰"。

本任务把传感器从"旧车的 2D 平面近似"升级为"真车的 3D 物理感知"：探点位置重标至装配.glb 光电节点坐标、通道对齐真车 11 路、MuJoCo 侧用 raycast 打真实几何，使传感器所见与物理所碰同源。

## Requirements

- **R1 探点位置重标**：`WheeledCombat11` 各通道的 Forward/Lateral（+新增 Height）取自装配.glb 光电节点在车体坐标系的实测坐标（提取脚本 `tools/mesh/extend_vehicle_mounts.py` → `sensor_mounts.json`，映射与置信度见 design 决策②）。所有探点必须落在真车投影（或铲/支座合理区域）内，不再出现"悬在车头外 37mm"。
- **R2 通道清单对齐真车 11 路**：v2 场景显式配 `sensors=wheeledCombat11`，生产路径首次启用真车通道清单（物理 11 路 + 兼容逻辑别名），消除 14 路虚拟通道被当成真车的失真。
- **R3 传感器全面 3D 化**：地面类判断（灰度/铲下/台沿）与 IR 探测（对手/台沿/围挡）全部改为 3D 感知；MuJoCo 侧用 `mj_ray` 打真实 geom（传感器所见 = 物理所碰），半悬/翻覆姿态下地面类读数随姿态正确变化（探点离开台面即不再报"在台面"）。噪声、施密特触发、迟滞管线保持不动。
- **R4 身份与配置**：`MatchEngine.CoreVersion` `sim-core-1.0.3` → `sim-core-1.0.4`（`src/Sim.Core/MatchEngine.cs:41`）；v2 场景 vehicles 显式配 `sensors=wheeledCombat11`；CoreVersion 并入 v2 重训身份，训练用 holdout 一次盲验。
- **R5 训练吞吐软门**：以 `controllers/score_block_rl/run_throughput_suite.py` 五 seed 对比 v4 基线，吞吐回退 ≤ 15% 为通过线（软门，退出准则见 design 决策⑥：超线先优化再测，仍超则停下游、报告数据、等用户拍板，不自动回退 3D）。
- **R6 legacy 行为冻结**：legacy 路径行为位不变（`replay-check replays/seed-42.json` 逐位 PASS）；`Legacy14` profile 一字不动（`Profiles.cs:304`）；FSM 阈值逻辑不动（本任务只换感知来源，不碰决策阈值）。

### 验收清单

**R1 探点位置重标**
- [ ] `py -3.12 tools/mesh/extend_vehicle_mounts.py --glb <装配.glb> --out <路径>` 产出 `sensor_mounts.json`，含 11 路通道的车体坐标与来源节点名。
- [ ] 新单测（SensorMountTests）：每一路断言探点 ∈ 真车投影/铲侧合理区域——底盘灰度×4 与对角 IR×4 在车体 footprint（|forward| ≤ rearExtent、|lateral| ≤ sideExtent）内；铲下×2、铲前×1 在尾铲/前臂区域；无一路悬空于车体外。
- [ ] 新单测：通道数 == 11，`Id` 集合与 WheeledCombat11 现行定义一致（只改数值不改 id）。

**R2 通道清单对齐**
- [ ] `scenarios/wushu-ring-2026-mujoco-v2.json` 两车显式 `"sensors": {"id": "wheeledCombat11"}`；跑出的 replay header 里 `vehicles.sensors.id == "wheeledCombat11"`。

**R3 3D 化**
- [ ] 新单测（Sensor3DTests）：构造半悬（前半出台）与翻覆姿态，断言地面类读数与 2D 时代不同且随姿态正确变化（半悬时前方灰度/铲下不再报台面；翻覆时读数失效/翻转）。
- [ ] 新单测：MuJoCo raycast 路径下 IR 对对手车的命中距离与两车间几何距离一致（±噪声幅值内），命中面法线衰减（`SensorProbe.Atten`）生效。
- [ ] legacy 平面语义退化单测：legacy backend 对同一姿态集合的传感器输出与升级前逐位一致（事件流断言）。

**R4 身份与配置**
- [ ] `src/Sim.Core/MatchEngine.cs` CoreVersion == `sim-core-1.0.4`；`src/Sim.Tests/MujocoIntegrationTests.cs:228`、`:240` 两处断言已改 1.0.4 并通过；`src/Sim.Tests/fixtures/restart-replay-seed42.json` 的 header.coreVersion 同步为 `sim-core-1.0.4`。
- [ ] 既有 MuJoCo replay（godot-parity-seed42 等）身份校验失效属预期，在 report.md 披露受影响清单。

**R5 吞吐软门**
- [ ] `run_throughput_suite.py` 五 seed 套件跑通，对比 v4 基线回退 ≤ 15%（或按决策⑥完成优化循环并留档数据后升级为人工决策）。

**R6 legacy 冻结**
- [ ] `dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1` 全量通过。
- [ ] `replay-check replays/seed-42.json` 逐位 PASS；`replay-check replays/godot-parity-seed42.json` 按其门禁结论披露（CoreVersion 变更导致的身份失效属预期）。
- [ ] `py -3.12 controllers/mbri_adapter_selftest.py` 与 `python -X utf8 controllers/score_block_rl/selftest.py` 通过（下游适配器/RL 环境无回归）。
- [ ] `git diff --check` 干净。

## Notes

- 位置真值置信度与兜底方案见 design.md 决策②（屁股光电高置信、巡台/侧底中置信、灰度低置信→工程默认兜底）。
- mj_ray ABI 若当前 MuJoCo 二进制不可用，按决策⑥回退解析式 3D（探点世界坐标 + 姿态修正的几何判断），不阻塞任务。
- 不做：物理参数调整、FSM 阈值改动、Legacy14 数值修订、训练超参重调（R4 重训身份由训练侧独立承接）。
