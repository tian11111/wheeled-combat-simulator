# Journal - neco (Part 1)

> AI development session journal
> Started: 2026-08-26

---



## Session 1: 武术擂台模拟器架构重构 — 内核/CLI/回放/Godot 脚手架/文档收尾

**Date**: 2026-08-26
**Task**: 武术擂台模拟器架构重构 — 内核/CLI/回放/Godot 脚手架/文档收尾

### Summary

完成 Sim.Core 确定性内核回归套件(89 测试全绿)、Sim.Cli 无头评测/回放闭环(含外部 Python 策略逐位复现)、Godot 脚手架与纯视图适配器、全套文档与保真度声明。

### Main Changes

- 新增 MatchEngineTests 回归(确定性/登台/推块+3/减益+6/同帧掉台/消极/判罚/超时/回放复现)
- Sim.Cli: match/replay-record/replay-check + PythonBridge(request-id 匹配、超时零回退) + example_controller.py
- godot/ 脚手架(project.godot/csproj/Main.tscn/Main.cs/ArenaVisualizer.cs)与可单测的 SnapshotView
- scenarios/wushu-ring-2026.json、fidelity.json、README + docs(架构/协议/CLI/移植/迁移)、.trellis/spec/sim
- OfficialLayout 常量统一, 消除 4 处块坐标重复

### Git Commits

(No commits - planning session)

### Testing

- [OK] dotnet test: 89/89 通过, 0 警告
- [OK] replay-check seed-42 与 seed-42-pyus 均 PASS(逐位复现)
- [OK] python -m py_compile 与 JSON 校验通过

### Status

[OK] **Completed**

### Next Steps

- 安装 Godot 4 .NET 后: 验证/补全 godot 场景与机器人可视网格
- 实现 Godot↔Sim.Cli 同种子一致性测试 (implement.md 第 7 项)
- 初始化 git 仓库并提交当前成果; 考虑归档 00-bootstrap-guidelines 模板任务


## Session 2: 武术擂台模拟器架构重构收尾 — 提交与归档

**Date**: 2026-08-26
**Task**: 武术擂台模拟器架构重构收尾 — 提交与归档
**Branch**: `main`

### Summary

完成收尾: 初始化 git 仓库并提交全部成果(28e516e), 归档任务 08-26-robot-simulator-architecture。89/89 测试通过, 两条回放校验逐位复现。

### Main Changes

- git init + 工作提交: 内核/协议/CLI/测试/Godot脚手架/文档/保真度声明
- .gitignore 补充 __pycache__/.godot/*.user

### Git Commits

| Hash | Message |
|------|---------|
| `28e516e` | (see git log) |

### Testing

- [OK] dotnet test 89/89; replay-check 两条 PASS

### Status

[OK] **Completed**

### Next Steps

- 安装 Godot 4 .NET 后新建后续任务: 验证场景脚本 + Godot↔CLI 同种子一致性测试
- 评估是否归档遗留模板任务 00-bootstrap-guidelines


## Session 3: 完成 Godot 桌面端与跨端一致性验收

**Date**: 2026-08-27
**Task**: 完成 Godot 桌面端与跨端一致性验收
**Branch**: `main`

### Summary

Godot 4.7.2 .NET 桌面端从脚手架完成到可运行/可观察/可控制/可回放; 壳层按会话/可视化/HUD/相机/回放职责重构, 指令全部路由 Sim.Core; 回放由内核重构 ReplayFile 缓存并提供播放/暂停/单步/跳转/时间轴; --duration bug 修复(3s→60tick, 120s→2400tick); ReplayFile 移入 Sim.Protocol; ParityCheck 与 CLI replay-check 同语义, headless --parity-check 对 seed-42 基线 PASS(4:49, 2400 tick, 752 指纹); 桌面冒烟 1280x720/1920x1080 + --capture 像素分桶截图 QA; 95/95 测试全绿; 文档移除脚手架表述

### Git Commits

| Hash | Message |
|------|---------|
| `73fd2d1` | (see git log) |

### Status

[OK] **Completed**


## Session 4: 场地布局校准 + 桌面布局编辑器 + glTF 外观导入 (08-27-arena-layout-editor)

**Date**: 2026-08-27
**Task**: 场地布局校准 + 桌面布局编辑器 + glTF 外观导入 (08-27-arena-layout-editor)
**Branch**: `main`

### Summary

按 2026 规则图纸校准场地几何并交付 arena-layout-v1 布局层: 协议纯增量(layoutVersion/field.pose), Sim.Core 以 FieldTransform 统一场局部↔世界映射且身份位姿逐位直通, 桌面端 E 键编辑模式(选择/拖动/旋转/吸附/撤销重做/恢复官方/打开/另存/Apply 重建会话)与机器人 .glb/.gltf 外观导入(错误回退 primitive)。

### Main Changes

- 协议: Scenario.layoutVersion + FieldParams.Pose + 边界校验; scenarios/wushu-ring-2026.json 写入 canonical 字段; 尺寸回归断言(外场3.8/擂台2.4/6cm/走道0.7/围栏0.2/出发区0.5x0.4距台沿0.2)
- 内核: FieldTransform(身份短路逐位直通); FieldModel 世界/局部双入口; 台壁/围栏/FenceDist 场局部求解; 出生点/块种子放置经变换; 掉台方位词场局部罗盘
- 桌面壳: ArenaVisualizer/SnapshotView/MatchCamera 全 Scenario 驱动(FieldGray 同源灰度台面纹理、出发区、武字 Label3D、20cm 围栏、相机按位姿取景); LayoutDraft(快照历史/拖拽分组/原子保存)+LayoutEditor(E 编辑模式)+RobotModelLoader(GltfDocument 运行时导入、缺法线 GenerateNormals 兜底、上限/回退)

### Git Commits

| Hash | Message |
|------|---------|
| `6580e81` | (see git log) |

### Testing

- [OK] dotnet test 130/130; CLI replay-check + Godot --parity-check 对旧 seed42 基线逐位 PASS; rotated-seed42(340 事件/2400 tick/16:8)两端逐位 PASS; --edit-smoke 22 项断言全过; glTF 模型 capture model=114~404px, 坏路径/坏扩展回退 primitive; headless 构建/加载零错误; git diff --check 干净

### Status

[OK] **Completed**

### Next Steps

- 真机遥测标定(摩擦/碰撞/堵转/登台); 可选: 场地尺寸编辑器(官方固定尺寸不可缩放为当前 MVP 边界); 灰度实测表载入(GrayGridMap 已有槽位)


## Session 5: 真机遥测物理标定闭环 (08-27-real-robot-physics-calibration)

**Date**: 2026-08-27
**Task**: 真机遥测物理标定闭环 (08-27-real-robot-physics-calibration)
**Branch**: `main`

### Summary

建立 telemetry-v1 遥测→参数拟合→留出验证→场景/保真度晋升的可复现闭环: 遗留 sim_calibrate.js 算法数值等价迁入 Sim.Calibration 纯库, 登台门控从 PhysicsWorld 私有常量提升为显式场景参数 (identity 逐位门禁通过), CLI 新增 calibrate 命令。仓库无真机遥测, 按 PRD 缺省作用域交付工具链+模板+合成验证, fidelity 保持未标定。

### Main Changes

- Sim.Core: MountVMin/MountAngleMax 参数化 (MOUNT_V_MIN/MOUNT_ANGLE_MAX, 默认 0.3/0.26 逐位一致); PhysicsWorld 公开 Gravity/BlockLinearDamping 共享常数
- Sim.Protocol: telemetry-v1 严格契约 (SI 单位/时间戳/kind 必填字段/fit-holdout 分集), ProtocolVersion.TelemetryFormat
- Sim.Calibration 新纯库: 四族拟合器(指数衰减/块摩擦三元搜索/恢复系数/堵转阈值分类)+分解层+MountEvaluator(分桶混淆矩阵, 覆盖规则)+ReportWriter(contentSha256 排除生成时间)+ApplyPatch
- Sim.Cli calibrate 命令: 校验失败零输出; 合成数据永不晋升; --emit-scenario 生成新场景(官方/旧回放不动); --update-fidelity 仅晋升 holdout 达标+source=real 子系统; 报告含双列指标/SHA/晋升原因
- telemetry/ 实验模板+采集规范 README+data/ gitignore; docs CLI/ARCHITECTURE 标定闭环; fidelity evidence 诚实刷新(status 不变); sim spec 新增标定契约

### Git Commits

| Hash | Message |
|------|---------|
| `ad8502f` | (see git log) |
| `fb1a2ad` | (see git log) |

### Testing

- [OK] dotnet test 167/167 (含 AC2 合成恢复 8/3/0.45/0.33/STALL∈[0.025,0.07)、确定性指纹、无效输入零 patch、合成拒绝晋升、real 晋升到临时副本、应用场景回放逐位一致); CLI replay-check + Godot --parity-check 对旧基线 PASS; 校准场景 Godot 桌面烟测 0 错误; git diff --check 干净

### Status

[OK] **Completed**

### Next Steps

- 拿到真机遥测后按 telemetry/README.md 跑首轮真实拟合+留出报告, 达标子系统 --update-fidelity 晋升; 若 mount 误判超标需另立项改造登台模型(斜穿/铲面上台)


## Session 6: 收尾 00-bootstrap-guidelines: spec 填充提交与归档

**Date**: 2026-08-27
**Task**: 收尾 00-bootstrap-guidelines: spec 填充提交与归档
**Branch**: `main`

### Summary

另一会话已填充 backend/frontend 编码规范并勾选 PRD, 本会话完成核验 (无占位残留、内容与仓库真实形态一致)、提交 (spec + learnings 三条) 并归档任务。bootstrap 任务线结束。

### Git Commits

(No commits - planning session)

### Status

[OK] **Completed**


## Session 7: MBri 传感器标定证据导入 (08-27-mbri-sensor-calibration-import)

**Date**: 2026-08-27
**Task**: MBri 传感器标定证据导入 (08-27-mbri-sensor-calibration-import)
**Branch**: `main`

### Summary

交付 sensor-calibration-v1 离线证据线: 严格选择清单 + 表头精确匹配的 MBri CSV 导入、三个纯回放评估器 (灰度 zone/white、前差带、铲子迟滞)、stored/重算/config 三源漂移只报告不合并、运行时候选标志与 contentSha256 确定性指纹。真实数据实测: gray 可见 near-edge 漂移 (0.5 vs 0.35) 候选否、front 带模型重算精确复现 -75.1/63.5 候选是、shovel 混合批次漂移如实 rejected。运行时/fidelity/回放全程零触碰。

### Main Changes

- Sim.Protocol: SensorCalibration.cs (sensor-calibration-v1 DTO+校验) + ProtocolVersion 常量
- Sim.Calibration: CsvTable/SensorReplay/SensorEvidenceBuilder/ConfigSnapshot/SensorEvidence 指纹
- Sim.Cli: sensor-calibration import 命令 (校验先于输出/原子写/--force/0-1-2 退出码)
- fixtures/mbri-mini 12 个真实文件子集入库; docs CLI/telemetry/README; sim spec 传感器证据契约

### Git Commits

(No commits - planning session)

### Testing

- [OK] dotnet test 187/187; seed-42 replay-check 752/752 PASS; 确定性双跑同 contentSha256; 无效输入 4 条路径零输出; fidelity.json 字节断言不变; 真数据 (187 文件) 实测运行 14 用/173 忽略/0 拒

### Status

[OK] **Completed**

### Next Steps

- 数据批次明确后的人工复核; 若需运行时传感器响应集成, 另立 sensor-response-runtime-profile 任务并只接受人工批准的报告


## Session 8: Godot 桌面端 UI 优化

**Date**: 2026-08-28
**Task**: Godot 桌面端 UI 优化
**Branch**: `main`

### Summary

完成深色赛事控制台 HUD、回放控制条和布局编辑工具栏；通过真实 renderer 1152/1280/1920 截图、Godot parity、CLI replay-check、22 项编辑冒烟和 189 项 .NET 测试。

### Git Commits

| Hash | Message |
|------|---------|
| `6273ac3` | (see git log) |
| `4c41d91` | (see git log) |
| `57c1375` | (see git log) |

### Status

[OK] **Completed**


## Session 9: Godot 相机交互、灰度与真实重启收尾

**Date**: 2026-08-28
**Task**: Godot 相机交互、灰度与真实重启收尾
**Branch**: `main`

### Summary

完成 08-28-godot-camera-gray-restart：Sim.Core 新增 RestartRobot 真实重启契约（Running/Paused 门控、经 FieldTransform 回位、清瞬态、对手恰好 +4、EventKind.Restart + restart_robot:<role> 追加命令，旧 restart 命令逐位兼容），三处解码（CLI/ParityCheck/MatchSession）同语义；修复 MatchCamera Top 俯视、抓取平移、滚轮缩放与编辑器所有权钩子；ArenaVisualizer/FieldGrayTextureMap 灰度轴契约与 Unshaded 材质消除假对角灰带；Main.cs R/T 路由、F5 场景重建与 --camera-smoke 24 项断言。215/215 测试、双 parity、edit/camera smoke、真实渲染捕获全绿；新增 spec/sim/restart-contract.md 并沉淀镜头/灰度/smoke 约定。

### Git Commits

| Hash | Message |
|------|---------|
| `781a969` | (see git log) |
| `4a0ef71` | (see git log) |
| `4e7cb79` | (see git log) |
| `fde6644` | (see git log) |
| `7506b82` | (see git log) |

### Status

[OK] **Completed**
