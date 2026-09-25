# MuJoCo 双物理后端：技术设计

## 架构边界

`Sim.Protocol` 只定义加性配置/快照/回放 DTO；`Sim.Core` 定义物理后端边界并继续拥有规则、FSM、传感器、事件与快照；新的 `Sim.Mujoco` 项目依赖 `Sim.Core`、通过 MuJoCo C API 执行原生物理。CLI 和 Godot 选择/注入后端，`Sim.Core` 不引用原生 DLL、Godot、文件系统、线程或进程。保持 `MatchEngine(Scenario)` 与现有视觉适配器构造路径使用旧后端；MuJoCo 场景若未注入后端则显式失败，不回退。

`IPhysicsBackend` 的最小职责是 `Step(dt)`、现有 `OnStage/HangOn/FullOn` 判定、目标机器人原生状态重置及 `Dispose`。现有 `PhysicsWorld` 适配此接口；FSM 改用接口。`MatchEngine` 仍负责动作限幅、传感器采样、接触归属收尾、掉台/推块判分与回放记录。保留当前只用于旧物理诊断的 `Physics` 访问路径：旧模式照常返回 `PhysicsWorld`，新模式调用则抛出说明性异常，不伪装成旧后端。

装配契约：`Sim.Core` 定义 `IPhysicsBackendFactory.Create(PhysicsBackendContext)`；上下文由 `MatchEngine` 在创建双方 `RobotRuntime`、`BlockRuntime`、`FieldModel`、`SimParameters`、`EventBus` 后构造，包含这些引用、完整 `Scenario` 和既有反僵局相位。保留的单/双参数 `MatchEngine` 构造路径只创建旧后端；新三参数构造路径接收可空视觉适配器及工厂。场景要求 `mujoco` 而工厂缺失时，在引擎构造期抛出明确错误。CLI/Godot 通过同一个宿主层选择入口按场景提供 `Sim.Mujoco` 工厂，避免 `Sim.Core` 反向引用适配项目。

## 数据流与模型

1. `Scenario.physics` 可空；空/`legacy` 是旧模式，`mujoco` 要求模型标记 `wushu-mjcf-v1`。未知标记在 `Scenario.Validate()` 被拒绝。首轮不增加 CLI 覆盖选项，场景文件是唯一模式来源；旧 JSON 的空字段仍省略。
2. 新模式把场局部米制几何和 `FieldTransform` 位姿编成规范 MJCF：台面/走道/台沿/围栏、双方车体/四轮/铲子、三块立方体。车辆已有长度、宽度、高度、质量、轮距、轴距及铲子参数来自 `VehicleProfile`。轮半径、电机限幅/速度伺服、接触与求解器初值作为 `wushu-mjcf-v1` 的明确未标定常量版本化；改变它们必须改模型版本。Godot 外观网格不进 MJCF。
3. 外部 `v/w` 保持“期望车体速度”含义；新后端用轮距把它换算成左右轮目标角速度，经受限电机驱动输入 MuJoCo。比赛 tick 仍为场景默认 0.05 s；v1 对其他 tick 配置预检拒绝，物理步长固定为 0.005 s × 10 子步。由原生状态回写世界坐标下的机器人/方块位置、速度、姿态与逐步接触方。每场独立拥有 `mjModel`、`mjData` 和相关句柄，先保证隔离；模型缓存只在后续性能证据需要时另议。
4. 裁判继续按现有场地投影/footprint 语义判断是否在台及方块中心是否出界，MuJoCo 不直接给分。新后端的接触体 ID 映射到 `us/them`，写入本帧方块接触列表；同帧最后接触及平局沿用现有归属收尾。真实重启在 `MatchEngine` 合法相位门控后，同时清零原生目标车的位姿/速度/驱动状态；另一车、方块、时钟与事件序列不动。
5. 现有解析传感器仍在物理步进前采样，读取上一个已提交物理状态，与当前固定帧顺序一致。首轮不改为 MuJoCo raycast；对物理碰撞几何与传感器目标几何的差异增加针对性测试与报告。

坐标契约：Sim 世界和 MuJoCo 均用右手系、`X/Y` 为场地平面、`Z` 向上，长度为米、角度为弧度；姿态 DTO 的四元数属性顺序固定为 `x,y,z,w`（MuJoCo 原生 `w,x,y,z` 在适配边界转换）。MJCF 生成时将场局部位姿经 `FieldTransform` **一次**映射为世界位姿；回写的 `X/Y` 已是世界坐标，不再二次旋转。Godot 仅在 `SnapshotView` 将 Sim `(x,y,z)` 映射到显示 `(x,z,y)`；旋转须用坐标变换矩阵换基后生成 Godot 四元数，不能直接交换四元数分量。身份与旋转场地均做往返测试。

## 协议、回放与展示

- `ReplayHeader` 加可空 `physicsBackend`、`physicsEngineVersion`、`physicsModelSha256`；旧 header 与旧场景同时缺字段才解释为旧模式，默认路径不序列化这些字段。场景要求 MuJoCo 而 header 身份缺失或不匹配时拒绝回放；检查先于动作流执行。
- 仅 `Snapshot` 加可空 `physicsPoses`：双方机器人按角色索引，方块按现有 `ObjectSet` 的 buff 顺序及单个 debuff 索引，每项为世界位置 + `x,y,z,w` 四元数。保留既有 `RobotState`、`EnergyBlockView`、`Observation` 的字段与语义，避免把新三维真值顺手暴露给控制器。`SnapshotView` 优先使用新姿态，缺失时维持旧投影。
- batch 完成行可空附加物理后端和模型哈希；旧行/旧固定指纹不变。新模式结果指纹使用带后端身份的独立规范输入，避免两种物理模式碰巧得到相同比分时被误认成相同实验。失败行仍不伪造结果字段。
- Windows x64 只使用一个锁定的 MuJoCo 官方稳定发布：实施前记录准确版本、下载 URL、二进制 SHA-256 和 Apache-2.0 许可；程序启动校验原生版本。GitHub 发布接口当前在本环境查询时认证失败，因此不在规划文档中猜版本号。

## 兼容、回退与风险

- 模式选择由场景显式决定，不增加全局开关。旧模式仍默认且不加载原生库；故新后端故障不影响旧比赛与回放。新场景需要移除/改回 `physics` 字段才可手动回退，不允许运行时静默降级。
- 所有创建 `MatchEngine` 的 CLI/Godot 路径，包括批量、录制、回放检查、桌面实况、回放重建与 parity，必须统一经过后端选择入口；会话结束/重建用 `Dispose` 释放原生资源。
- 当前模型参数未标定；MuJoCo 的软接触/摩擦/电机模型可能更真实地暴露登台失败、死锁或并发成本。验收报告必须将“数值稳定/可复现”和“真机准确”分开，禁止为了通过测试改旧基线或伪造接触。
