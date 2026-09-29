# 执行计划

前置阅读：`.trellis/spec/` 相应层规范（trellis-before-dev）；本任务七决策见 `design.md`。

## B. 挂点提取：装配.glb 光电节点 → sensor_mounts.json

1. 新增 `tools/mesh/extend_vehicle_mounts.py`：复用 `tools/mesh/extract_vehicle_mounts.py`（同目录 `extract_vehicle_mesh.py`）的 glb 解析与坐标映射（`bodyX=-modelZ / bodyY=-modelX / bodyZ=modelY`，sha256 门禁同口径），按光电节点名提取 → 车体坐标，输出 `sensor_mounts.json`（11 路通道的 forward/lateral/height + 来源节点名 + 置信度标记）。
2. 按 design 决策②映射：屁股光电→shovel_front（高置信，车尾≈165mm 高≈61mm）；巡台光电×4→diag_*（中置信，车头两舷外八）；侧底光电支座×2→shovel_under_*（中置信）；光电×2+矮光电座×2→底盘灰度 4 路**兜底工程默认**（投影内贴地、朝向前后左右）。
3. 兜底探点收进真车足迹：|forward| ≤ rearExtent=0.16949、|lateral| ≤ sideExtent=0.12185（对照 Legacy14 的 ±0.11 口径）。

验证命令：

```bash
py -3.12 tools/mesh/extend_vehicle_mounts.py --glb "C:/Users/Neco/Downloads/装配.glb" --out calibration
py -3.12 -c "import json; d=json.load(open('calibration/sensor_mounts.json',encoding='utf-8')); print(len(d['channels']), [c['id'] for c in d['channels']])"
```

## C. mj_ray ABI 探针（tmp/rayprobe/）

1. 新增探针（独立小程序或临时测试，放 `tmp/rayprobe/`）：在 `src/Sim.Mujoco/MujocoNative.cs` 追加 `mj_ray` DllImport（EntryPoint 口径同现有 `mj_geomDistance` 条目），加载最小模型打一条 ray，验证：导出存在、mesh geom 命中正确、距离数值合理。
2. 探针失败（导出缺失/崩溃）→ 按 design 决策⑥走**解析式 3D 实现**（接口不变），把探针结果记入 report；不阻塞 D/E。

验证命令：

```bash
"$DOTNET_ROOT/dotnet.exe" run --project tmp/rayprobe --  # 或对应 test filter；产物与结论留 tmp/rayprobe/
```

## D. 通道协议：SensorChannel.Height + WheeledCombat11 数值修正

1. `src/Sim.Protocol/Profiles.cs`：`SensorChannel`（:23）加可选 `double? Height`（车体坐标系 Z，m，null=旧 2D 平面语义）；`Validate()` 覆盖（非负、合理量程）。
2. `WheeledCombat11`（:352）按 `sensor_mounts.json` 修数值：只改 Forward/Lateral/Height，**不改 id / 不改逻辑别名 / 不改语义模式**（决策⑦）；`Legacy14`（:304）一字不动。
3. JSON 往返：场景/回放序列化含 Height 字段，`RoundTripTests` 补 Height 往返断言（null 省略、有值保真）。

验证命令：

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj --filter "FullyQualifiedName~RoundTrip" -m:1
```

## E. 3D 探测：backend 接口 + SensorSampler 改造 + MuJoCo raycast

1. `src/Sim.Core/IPhysicsBackend.cs`：新增 `ProbeRay` / `ProbeGround` 查询（design 决策③草案）。
2. `src/Sim.Core/Physics.cs`（legacy）：平面语义退化包裹——`ProbeRay` 接现有平面 ir/edge/fence 语义，`ProbeGround` 接 `_field.OnPlatform(XY)` + `GraySpotSample(XY)`；**位不变**（`IsFlipped => false` 先例）。
3. `src/Sim.Core/Sensors.cs`（SensorSampler）：`irProbeFor` / `edgeProbeFor` / 地面类判断全部改经 backend 接口取命中；探点世界坐标含 Height（`SensorPoint` 扩展出 Z）；噪声、施密特触发、迟滞、clamp、logical aliases 管线**不动**。
4. `src/Sim.Mujoco/MujocoPhysicsBackend.cs`：实现 raycast——`mj_ray` 打真实 geom（碰撞 geom），FOV 角度过滤 → 最近命中 → 命中面法线衰减（`SensorProbe.Atten`）；灰度 = 探点垂线颜色场采样 + 高度差量程（半悬/翻覆不再凭 XY 在台内误报）。mj_ray 不可用则按 C 探针结论走解析式 3D。

验证命令（编译 + 既有全量不回归）：

```bash
"$DOTNET_ROOT/dotnet.exe" build RobotSimulator.sln -m:1
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj --filter "FullyQualifiedName~LegacyAlias|FullyQualifiedName~Samples|FullyQualifiedName~MatchEngine" -m:1
```

## F. 配置与身份

1. `scenarios/wushu-ring-2026-mujoco-v2.json`：us/them vehicles 各加 `"sensors": {"id": "wheeledCombat11"}`（R2）。
2. `src/Sim.Core/MatchEngine.cs:41`：`CoreVersion` → `sim-core-1.0.4`（R4）。
3. `src/Sim.Tests/MujocoIntegrationTests.cs:228`（`Assert.Equal("sim-core-1.0.3", …)`）与 `:240`（`Assert.Contains("sim-core-1.0.3", …)`）两处断言改 1.0.4。
4. `src/Sim.Tests/fixtures/restart-replay-seed42.json`：header.coreVersion → `sim-core-1.0.4`。
5. 既有 MuJoCo replay 身份失效清单记入 report（预期披露）。

验证命令：

```bash
grep -n "sim-core-1.0.4" src/Sim.Core/MatchEngine.cs src/Sim.Tests/MujocoIntegrationTests.cs
grep -c "wheeledCombat11" scenarios/wushu-ring-2026-mujoco-v2.json
```

## G. 新增单测 + 验证矩阵

新增 `src/Sim.Tests/SensorMountTests.cs` 与 `src/Sim.Tests/Sensor3DTests.cs`：

1. 探点位置：每路探点 ∈ 真车投影/铲侧合理区域（灰度×4+对角×4 在 footprint 内、铲下×2/铲前×1 在铲区域），无悬空探点（R1）。
2. 通道数 == 11、id 集合与现行 WheeledCombat11 一致（R1/R2）。
3. 半悬/翻覆姿态读数变化：半悬时前方地面类不再报台面；翻覆时地面类失效（R3）。
4. MuJoCo raycast：IR 命中距离 ≈ 几何距离（±噪声），Atten 生效（R3）。
5. legacy 位不变事件流断言：legacy 场景传感器事件流与升级前一致（R6）。

验证矩阵（全绿才算完成）：

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"

# 1) 全量单测
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1

# 2) 回放身份
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- replay-check replays/seed-42.json            # legacy 逐位 PASS
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- replay-check replays/godot-parity-seed42.json # MuJoCo：CoreVersion 失效属预期，披露

# 3) 下游自测
py -3.12 controllers/mbri_adapter_selftest.py
python -X utf8 controllers/score_block_rl/selftest.py

# 4) 吞吐软门（R5，五 seed vs v4 基线）
python -X utf8 controllers/score_block_rl/run_throughput_suite.py   # 回退 ≤15% 通过；超线走决策⑥

git diff --check
```

## 硬约束

- legacy 场景逐位不变（`replay-check replays/seed-42.json` PASS 是唯一判据）；`Legacy14` 一字不动。
- `WheeledCombat11` 只改数值不改 id/逻辑别名/语义模式。
- FSM 阈值逻辑不动；物理参数不动。
- 不为吞吐数字静默回退 3D（决策⑥：优化→复测→停下游等拍板）。
- CoreVersion 升 1.0.4 是诚实标识，不得为旧 MuJoCo replay 放宽校验。
- 网格资产与 glb 哈希口径沿用 extract_vehicle_mesh.py（换 glb 必须同步 sha256 门禁）。

## 交付

- `report.md`：结论 + 半悬/翻覆读数证据 + CoreVersion 影响清单（godot-parity 等）+ 五 seed 吞吐数据与决策⑥执行记录 + 低置信兜底探点披露 + holdout 盲验结果。
