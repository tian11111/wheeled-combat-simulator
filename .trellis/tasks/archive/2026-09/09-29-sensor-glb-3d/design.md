# 技术设计：传感器真车标定与3D化（grilling 共识七决策）

grilling 已收敛的七个决策，本设计的全部取舍都从这七条推出：

> ① 范围三层全做；② 位置真值 = 装配.glb 光电节点；③ 实现层 = IPhysicsBackend 3D 探测接口 + legacy 平面语义退化；④ 架构 = MujocoPhysicsBackend mj_ray 打真实 geom、灰度走颜色场采样、噪声/施密特/迟滞管线不动；⑤ CoreVersion 1.0.4 并入 v2 重训身份、holdout 一次盲验；⑥ 吞吐软门退出准则 = 超线先优化再测、仍超停下游等拍板；⑦ Legacy14 冻结、WheeledCombat11 只改数值不改 id。

## 决策① 范围：三层全做

挂点数据（B）、通道协议（D）、探测执行（E/F）三层都在本任务内完成，不做分期。理由：探点坐标（层 1）与 3D 探测（层 3）互相依赖——3D 化后探点不重标会在错误的物理位置上"更精确地"感知；通道清单（层 2）是前两者的载体。拆开交付只会得到两个中间态。

## 决策② 位置真值 = 装配.glb 光电节点（含映射与置信度）

真值来源：`装配.glb`（SolidWorks 导出，sha256 `9538408e…` 已记录于 `tools/mesh/extract_vehicle_mesh.py`，换 glb 必须同步 EXPECTED_GLB_SHA256）。坐标映射沿用 extract 脚本已验证的口径（det=+1 非镜像）：`bodyX = -modelZ`（车头）、`bodyY = -modelX`、`bodyZ = modelY`（上），原点 = 轮轴平面。新增 `tools/mesh/extend_vehicle_mounts.py` 复用同一 glb 解析（load_glb/坐标映射），按节点名提取光电节点世界坐标 → 车体坐标 → `sensor_mounts.json`。

光电节点 → 11 路通道的映射与置信度：

| GLB 光电节点（组） | 通道（WheeledCombat11） | 位置口径 | 置信度 |
| --- | --- | --- | --- |
| 屁股光电 | `shovel_front`（铲前红外） | 车尾约 165mm、高约 61mm | **高置信**（节点语义明确，直接采用） |
| 巡台光电 ×4 | `diag_left_front` / `diag_right_front` / `diag_left_rear` / `diag_right_rear` | 车头两舷"外八"朝向 | 中置信（位置采用，朝向按外八角） |
| 侧底光电支座 ×2 | `shovel_under_left` / `shovel_under_right` | 尾铲下 | 中置信 |
| 光电 ×2 + 矮光电座 ×2 | `gray_front` / `gray_rear` / `gray_left` / `gray_right` 参考 | — | **低置信** → **兜底**：不用节点坐标，用工程默认探点（投影内贴地、朝向前后左右，参照 Legacy14 的 ±0.11 口径收进真车足迹） |

置信度处置：高/中置信直接采用节点坐标并在 `sensor_mounts.json` 里记来源节点名（可审计、可回溯）；低置信组一律兜底为工程默认——位置数据不足以支撑重标就不硬标，宁可给"投影内贴地朝向正确"的保守值，留给实测标定数据后续替换。

## 决策③ 实现层：IPhysicsBackend 3D 探测接口 + legacy 平面语义退化

探点在哪、看到什么，是物理问题，接口放 `IPhysicsBackend`（先例：`IsFlipped`，`src/Sim.Core/IPhysicsBackend.cs:22`——上一任务用同样的方式让"legacy 恒 false、MuJoCo 走真实姿态"共存）：

```csharp
// IPhysicsBackend 新增（草案）
/// <summary>从探点(世界系)沿方向(世界系)的最近命中；无命中返回 null。</summary>
PhysicsRayHit? ProbeRay(in ProbeQuery q);   // 起点含 Z、方向含俯仰、range、类别过滤
/// <summary>探点正下方颜色场灰度采样 + 高度差量程判定（地面类 3D 语义）。</summary>
double ProbeGround(in GroundQuery q);
```

- legacy（`PhysicsWorld`）：`ProbeRay` 退化为现有平面 irProbe/edgeProbe 语义，`ProbeGround` 退化为 `_field.OnPlatform(XY)` + `GraySpotSample(XY)`——**位不变**（与 `IsFlipped => false` 同一模式）。
- MuJoCo（`MujocoPhysicsBackend`）：走 mj_ray / 真实几何 + 颜色场。
- `SensorSampler` 保留在核心层负责"通道 → 语义值"的公共管线（噪声、施密特、迟滞、clamp、逻辑别名），只把"命中判断"下沉到 backend。

## 决策④ 架构：MujocoPhysicsBackend mj_ray 打真实 geom

- **IR 类（target/edge/fence）**：`MujocoPhysicsBackend.ProbeRay` 用 `mj_ray` 从探点世界坐标沿光束方向打真实 geom（车身 mesh geom、台面、围挡、对方车）。FOV 角度过滤（目标偏离光束轴 > half-FOV 不算命中）→ 最近命中 → 命中面法线与光束夹角算衰减 `Atten`（`SensorProbe.Atten` 已有字段）。**传感器所见 = 物理所碰**：ray 打的 geom 就是碰撞 geom。
- **地面类（gray / ir_ground）**：不 ray 打，走"探点垂线颜色场采样 + 高度差量程"：探点世界坐标垂线取颜色场灰度，并以探点离地/离台高度差做量程判定——探点高于量程（半悬、翻覆朝天）即报无反射。半悬不再因"XY 落在台内"而误报在台面。
- **管线不动**：`SensorNoiseFor` / 施密特触发 / 迟滞 / `ClampSensorValue` / logical aliases 原样保留，只替换它们上游的"命中/地面判断"来源。
- mj_ray ABI 探针（implement 步骤 C）先行：当前 MuJoCo 二进制若缺该导出，按决策⑥回退——MujocoPhysicsBackend 用解析式 3D（探点世界坐标 + 姿态修正的几何判断），接口不变，后续可无感切换 mj_ray。

## 决策⑤ 身份：CoreVersion 1.0.4 并入 v2 重训身份

- `MatchEngine.CoreVersion`（`src/Sim.Core/MatchEngine.cs:41`）`sim-core-1.0.3` → `sim-core-1.0.4`。MuJoCo 回放创建/校验比较该字段，升版本即诚实宣告"MuJoCo 轨迹已变"（传感器语义 + 通道清单都变了，轨迹必然变）。
- v2 场景 `vehicles` 显式配 `sensors=wheeledCombat11`，随新 CoreVersion 进入 v2 重训身份；既有 MuJoCo replay 身份失效属预期并披露。
- 训练侧 holdout 一次盲验（不做超参重调）：新感知语义下的策略质量以 holdout 结果为准记录进 report。

## 决策⑥ 吞吐软门退出准则

通过线：`run_throughput_suite.py` 五 seed 对比 v4 基线回退 ≤ 15%。软门执行序：

1. 超线 → **先优化再测**（按性价比排序：射线频率——不必每 tick 每通道全量 ray，可事件驱动/隔 tick；缓存——静态台面/围挡命中缓存；简化命中几何——IR 只对候选 geom 打）。
2. 优化后复测仍超线 → **停下游**（不合并、不重训），把五 seed 数据 + 优化尝试清单报告给用户**拍板**（接受回退 / 再优化 / 其他）。
3. **不自动回退解析 3D**——3D 化是本任务的需求本体，不允许为了吞吐数字静默降级；回退与否是用户决策。
4. mj_ray ABI 不可用时的"回退解析 3D"是**实现路径回退**（决策④），不是吞吐回退，两者不混用：解析 3D 同样要过 R5 吞吐门。

## 决策⑦ 兼容冻结

- `Legacy14`（`Profiles.cs:304`）**一字不动**：legacy/API 默认路径的传感器定义冻结，位不变由 `replay-check replays/seed-42.json` 逐位 PASS 兜底。
- `WheeledCombat11`（`Profiles.cs:352`）**只改数值不改 id**：通道 id/逻辑别名/语义模式保持现行定义（`gray_front`…`shovel_front`、`f`=max 虚拟、`r`=Unmapped），只按决策②更新 Forward/Lateral/新增 Height——下游按 id 引用的代码（HUD、mbri 适配、RL 观测）零改动。
- FSM 阈值逻辑不动：本任务只换感知来源，登台信号阈值、掉台判定等 FSM 决策逻辑原样。

## 风险

- mj_ray 对 mesh geom 的射线命中在部分 MuJoCo 版本走 mjc_RayMesh 慢路径——吞吐门（R5）是为此设的软门，不是摆设。
- 低置信灰度兜底探点是工程默认，与实车位置可能有厘米级偏差——已按决策②标注置信度，实测标定数据到位后替换 `sensor_mounts.json` 数值即可，无需改代码结构。
- 半悬读数变化会改变登台临界行为（感知更真实 → 更保守），属本任务目的本身，报告披露即可。
