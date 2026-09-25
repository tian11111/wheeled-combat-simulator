# MuJoCo 模式 SEARCH 索敌闭环

## Goal

让官方 MuJoCo 场景的内置 FSM 在倒车登台后完成 SEARCH 索敌、分类和真实对抗：增益块进入 `SCORE_BLOCK` 并推块得分，对手进入 `ATTACK`。官方 seed 42 全场产生可追溯的非零比分；legacy 行为与旧回放逐位不变。

## 已知事实与证据边界

- 归档登台报告记载：双方约 3.2 s 登台后，反复“发现增益块 (4.0m, 左后) → 3 s 后目标丢失”，此前 60 s 比赛和 32 场短 batch 为 0:0；同 seed 的 legacy 历史记录为 4:49。本任务尚无可复核的原始逐 tick MuJoCo 轨迹；这些是历史观察，须在当前 checkout 重测。
- 当前 `FindTargetFor` 从红外 `Probe` 的反射对象选目标，按逻辑红外阈值与台上条件过滤，不从 `Observation.ObjectSet` 读取目标。默认 `legacy14` 对角探针量程 1.6 m；`Probe.D` 为中心距离减目标半径，超量程会先被剔除。日志对 `Probe.D` 做一位小数格式化，不能把 ≤1.6 m 显示成 4.0 m。先核对日志来源、profile 和原始值。
- SEARCH `turn` 使用 `V=0`、有界 `W`；方位误差 <0.15 rad 才进入 `classify`，超过 3 s 清除目标。历史诊断记载 `W=2.0 rad/s` 时实际偏航约 0.068 rad/s，但当前目录没有原始轨迹可独立复核。
- 先前试验记载：整体提高轮速伺服 `kv` 或降低摩擦会破坏登台；直接加前进弧线可能掉台或绕块公转。历史试验中弧线接近曾推动方块并记录位移，但未完成对准、分类与得分；它们不是已验收修复。现有 MuJoCo 集成测试只证明内置 FSM 登台进入 SEARCH，未证明索敌后的得分闭环。

## Requirements

- R1 在当前构建、官方 seed 42 复现并保存逐 tick 证据，区分传感器量程/事件来源、转向收敛和后续推块问题。记录 profile、探针通道与原始 `D`、目标坐标、方位误差、FSM 状态及 phase、命令 `V/W`、实际偏航率、车体位置和台上状态；明确传感器在物理步进前采样的时间相位。
- R2 优先验证 MuJoCo 执行器边界的最小有界原地转向补偿。只在有效纵向命令近零且存在转向命令时放大差速轮目标速度；保留现有轮速上限、力上限、伺服 `kv`、摩擦、登台动作和 legacy FSM。候选系数与淘汰条件见 `design.md`；若证据否定此方案，停止并记录新卡点，不放宽 SEARCH 超时或场景。
- R3 用同一内置 FSM 分别验证增益块和对手的分类分流，以及真实接触后的推块/攻击结果。将“已对准”“已分类”“已推块”“已得分”作为不同门槛，不以中间状态代替闭环。
- R4 控制映射变化须进入 MuJoCo 回放身份校验，重录新回放；旧 legacy 回放继续逐位通过。保持协议字段形状、官方场景、Godot 渲染与 `fidelity.json` 不变。

## Acceptance Criteria

- [x] AC1 当前官方 seed 42 的基线与候选试验各保存同一格式的逐 tick 轨迹及构建标识；每次发现事件可对应探针通道、实际 profile/range、原始 `Probe.D`、目标对象和采样前一帧位姿。若当前默认对角通道的原始 `D` 超过 1.6 m，先定位数据契约问题，再进行转向调参；报告解释历史 `4.0m` 是否在当前版本重现，不能用显示舍入作解释。
- [x] AC2 受控左后目标从首次发现到 `classify` 的时间 <3.0 s，结束时绝对方位误差 <0.15 rad；车未掉台、未进 RECOVER。记录基线与候选 2/4/6 的实际偏航和位移，选择满足条件的最小系数；全部失败则本 AC 不通过并附轨迹返回设计。
- [x] AC3 两个受控目标场景均通过：增益块走 `SEARCH → classify → SCORE_BLOCK`，随后发生接触、方块实际位移和归属明确的 `BlockScore`；对手走 `SEARCH → classify → ATTACK` 并产生真实追击/接触。减益块不得被当作增益块得分；不得以日志文本单独证明物理接触或比分。
- [x] AC4 `scenarios/wushu-ring-2026-mujoco.json` 官方 seed 42、默认 120 s、内置 FSM 的比赛产生非零比分，且比分增量能追溯到真实 `BlockScore` 或接触导致的对手掉台；保留事件、接触/位姿和比分对应证据。若仅对准或阶段转移而仍为 0:0，记录新卡点并保持本 AC 失败。
- [x] AC5 原有登台测试与新增索敌测试、`MujocoProtocolTests`、全套 `dotnet test` 通过；所有 `replays/*.json` 的 legacy `replay-check` 逐位通过。控制映射改动前录制的 MuJoCo 回放明确因版本不匹配被拒绝，改动后重录的回放通过 `replay-check`；同一新回放通过 Godot headless parity。相同 seeds 的 batch 并行度 1/4 去除 `createdAt` 后稳定字段逐行一致，32×5 s 资源门禁 32/32 completed。

## Out of Scope

- 不改变 legacy FSM 的索敌/分类阈值、SEARCH 的 3 s 超时或官方规则与布局。
- 不改动 MuJoCo 车辆/台沿几何、轮力上限、`kv`、摩擦来掩盖转向问题；不将未标定模型宣称为真机保真。
- 不为诊断引入长期公共参数、独立测试框架或新的 Godot 视觉效果。

## 参考

代码锚点、历史观察与未证实项见 `research/current-evidence.md`。执行顺序、命令与失败留证见 `implement.md`。
