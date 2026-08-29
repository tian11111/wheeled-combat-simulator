# Sim 层开发规范（.NET 确定性内核 + 桌面壳）

> 本项目实际是 Godot 4 .NET + .NET 8 仿真栈；`backend/`、`frontend/` 为模板占位，
> 写仿真相关代码前先读本目录。

## 铁律（违反即破坏验收标准）

1. **Sim.Core 不依赖引擎与 IO**：不得出现 Godot/文件系统/网络/进程/时钟调用。
   随机只能来自 `DeterministicRandom`（种子派生），时间只能来自固定步长累加。
2. **确定性契约**：同种子 + 同被接受动作序列 ⇒ 逐位一致的事件与比分。
   改任何物理/裁判/传感器逻辑后必须跑：
   ```bash
   dotnet test
   dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
   ```
3. **协议演进只加不改**：`Sim.Protocol` 现有字段的 JSON 形状（camelCase、枚举拼写）
   永远不变；新能力走新枚举成员/新字段/新 `ProtocolVersion`。EventKind 只允许增量追加。
4. **官方布局单一来源**：块坐标等布局常量用 `Sim.Protocol.OfficialLayout`，
   禁止在 CLI/桌面壳/测试里再抄一份数字。磁盘规范形态是 `scenarios/wushu-ring-2026.json`。
5. **渲染不复刻规则**：`godot/` 只消费 `Snapshot`（经 `SnapshotView` 投影）并发裁判指令；
   任何本地计分/物理判分都是缺陷。

## 层次与依赖方向

```
godot/ ─┐
Sim.Cli ─┼─→ Sim.Core ─→ Sim.Protocol
Sim.Tests(链接 godot/src/SnapshotView.cs 做无 Godot 回归)
```

- 无 Godot 环境的可测逻辑放纯文件（如 `godot/src/SnapshotView.cs`），
  用 `<Compile Include>` 链接进 `Sim.Tests`，不要为它新建工程。
- 外部控制器一律走 `Sim.Cli.PythonBridge`（JSONL、request-id 匹配、超时→零动作、计 fault）。

## 行为对齐参考

- 遗留原型 `D:/project/robocup/robot-simulator/wushu_ring_sim.html` 只读。
- 移植/对齐决策记录在 `docs/PORTING_NOTES.md`；新增有意差异必须追加条目。
- 基线再生成：`node tools/legacy-baseline.js` → `src/Sim.Tests/fixtures/`（禁止手改）。

## 保真度诚实性

对外声明保真度必须引用根 `fidelity.json`；未标定子系统（摩擦/碰撞/堵转/登台）
不得在文档或输出中暗示等同真机。

## 场地布局契约（arena-layout-v1）

- 一切场地几何（平台/走道/围栏/出发区/出生点/块位）以**场局部米制**存储；
  场局部→仿真世界的映射只有 `Sim.Core.FieldTransform` 一个实现。
  物理/传感器/渲染/相机一律经由它（或其宿主 `FieldModel` 的世界坐标入口），
  禁止在任何消费方手写旋转公式。
- **身份位姿逐位直通是兼容性门禁**：`IsIdentity` 短路返回原值（IEEE 精确），
  改任何几何相关代码后必须跑 `dotnet test` + `replay-check replays/...` 确认旧基线逐位一致。
- 新增几何能力先问：这是"场局部可表达"的吗？台壁/围栏求解保持场局部轴对齐，
  世界坐标修正只在边界处经 `FieldTransform` 进出。
- 编辑器/草稿只产 `Scenario` 数据并重建会话；任何编辑路径都不得触碰运行中的
  `MatchEngine`（比赛/回放进行中编辑必须被禁用）。
- 渲染层（含 `.glb/.gltf` 外观模型、本地偏好文件、台面灰度纹理）永不进入
  `Scenario`/`Snapshot`/回放指纹。
- 运行时 `GltfDocument` 导入**不会**像编辑器导入器那样自动合成法线——缺 NORMAL 属性
  的网格在 Forward+ 下渲染全黑；必须经 `SurfaceTool.GenerateNormals` 兜底
  （见 `godot/src/RobotModelLoader.cs::EnsureNormals`）。
- 视觉 QA 以 `--capture` 视口像素分桶为机器可判定证据；桌面截图存 `godot/docs/`
  （其 `.import` 元数据不入库），调试用临时图不入库。
- 台面灰度显示：像素↔场局部轴契约由 `godot/src/FieldGrayTextureMap.cs` 单一实现
  （row 0 = 南、col 0 = 西），区域几何仍以 `FieldModel.FieldGrayLocal` 为唯一来源；
  显示用**官方效果图调色板**（走道深灰、擂台边带 300 → 黑边渐入白心、红区红底白"武"），
  传感器 0–1000 数值不变——调色板是视觉约定；材质必须 Unshaded，防止方向光制造假
  对角灰度带。Godot 4 PlaneMesh(FACE_Y) 的顶点与 UV 翻转互相抵消，改动映射前先以
  代表性像素测试验证（`src/Sim.Tests/FieldGrayDisplayTests.cs`）。

## 真实重启契约（restart-v1）

- `MatchEngine.RestartRobot` / `restart_robot:<role>` / `EventKind.Restart` 的
  签名、不变量、错误矩阵与测试点见 [restart-contract.md](./restart-contract.md)。
  旧 `restart:<role>:<kind>` 罚分命令逐位兼容，不得重解释。

## 标定契约（telemetry-v1）

- 真机数据 → 参数只能经 `calibrate` 命令的固定链路：遥测入口一次性严格校验
  （SI 单位、类型、时间戳、kind 必填字段），**校验失败不得产出报告或 patch**。
  拟合/验证数学全部在 `Sim.Calibration` 纯库内，CLI 只做 IO 编排。
- **拟合器与内核必须共享模型常数**（`PhysicsWorld.Gravity`/`BlockLinearDamping`）；
  改内核物理模型时同步改拟合模型，否则标定结果失真。
- **留出集门禁**：晋升 fidelity 需要 `set:"holdout"` 的独立试验达到目标误差；
  拟合集误差、合成数据、单次试验永远不够。`capture.source != "real"` 一律拒绝晋升。
- 标定只产**新场景/patch 文件**；官方场景、代码常量、运行中的引擎不受影响，
  旧回放逐位不变（晋升前后都须过 `replay-check` + `--parity-check`）。
- `mount` 门控只验证不拟合：真机成败与确定性门控的混淆矩阵误判率 >10% 或
  速度×角度覆盖不足时，必须如实报告"模型不足"，保持未标定。
- 标定原始遥测（`telemetry/data/`）与报告（`calibration/`）默认不入库，与
  replays 同理（可从数据重导）；审计需要时显式 `git add -f`。

## 传感器证据契约（sensor-calibration-v1）

- MBri 传感器判定模型（灰度/前 ADC/铲子）的证据导入与物理标定 telemetry-v1 **分线**：
  新 schema、新命令（`sensor-calibration import`），互不扩用。
- 只消费选择清单点名且表头精确匹配的文件；未选中文件必须列为 ignored、
  被拒文件必须带原因；禁止按列名猜文件。
- stored 模型 / 重算 / config.py 快照的差异只进 comparison 表，**永不自动合并**；
  不一致即压为 `evidence_only`/`rejected`。回放评估器必须是纯函数
  （无时钟/IO/随机），median 语义对齐 Python `statistics.median`。
- 灰度 CSV 无坐标 → 报告恒 `coordinateData=false`；禁止伪造成 FieldModel.GrayGridMap。
- 本产物不进入运行时（FieldModel/SensorSampler/FSM/官方场景/fidelity.json/回放
  字节不变）；运行时集成另立任务且只能消费人工接受的报告。

## 视觉证据契约（vision-replay-v1）

- 真实视觉回放证据与 telemetry-v1 / sensor-calibration-v1 同样**分线**：
  新 schema（vision-replay-v1）、新命令（`vision import|evaluate`）、新纯库
  `src/Sim.VisionReplay`（仅引用 Sim.Protocol）。rng 流纪律（回放适配器绝不
  消费 `context.Random`）、unknown 原因码、导入错误矩阵与 evidence_only 门禁
  见 [vision-replay-contract.md](./vision-replay-contract.md)。
