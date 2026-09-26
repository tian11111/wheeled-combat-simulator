# MuJoCo SCORE_BLOCK 强化学习试点

## Goal

在确定性 MuJoCo SCORE_BLOCK 修复通过后，建立最小可复现、可测吞吐的 Gymnasium 训练环境，用 Stable-Baselines3 PPO 训练并与当前确定性 FSM 基线比较。每集先由内置 FSM 到达 `SCORE_BLOCK`，学习策略只接管此后的推块阶段；不将其接入默认控制器，也不声称可用于真机。

## 前置条件

`.trellis/tasks/09-25-mujoco-search-targeting/report.md` 已记录 AC1–AC5 全部通过：官方 MuJoCo seed 42 全场由我方产生真实 `BlockScore`，且无我方掉台。此前置门槛现已满足；本 RL 任务仍为 `planning`，须完成规划审查和 Trellis 启动步骤后才能实施。

## Requirements

- R1 提供 episode 级 reset(seed) / tick 级 step(action) 环境。同一训练进程内编译官方 MuJoCo 模型一次；后续相同 MJCF/车辆配置的 reset 复用只读 `mjModel`，每集创建全新的 MatchEngine 运行时与独立 `mjData`，调用 Arm()，先用内置 FSM 自动推进到我方首次进入 `SCORE_BLOCK`，再返回训练初始观测；step 的一步对应场景固定 0.05 s。模型身份不同必须重新编译或明确拒绝，不能误用旧模型。若该 seed 比赛结束前未进入 `SCORE_BLOCK`，记录 `no_score_block`，该集作为零成功的终止样本，不悄悄改 seed。episode 完成后释放该集原生数据，训练进程结束时释放共享模型。IO 留在 CLI / Python，不能把进程、文件或 Gymnasium 依赖放进 Sim.Core。
- R2 Python 环境遵守 Gymnasium 五元组接口。动作为 Box([-1,-1],[1,1])，映射到 v = action[0] m/s、w = 2 * action[1] rad/s；观测固定为 9 个 float：目标增益块相对车体的 x/y、目标块绝对 x/y、车辆前向速度、偏航率、车辆是否在台、目标块是否在台、剩余比赛时间比例。坐标/速度按固定场景范围归一化并裁剪。
- R3 观测中的目标块坐标来自 MatchEngine.Blocks / Observation.objects 的仿真真值，属于仿真特权状态。报告不得把该模型描述为仅用传感器、视觉或可直接部署真机；相机/传感器观测策略留待独立任务。
- R4 每 episode 在进入 `SCORE_BLOCK` 时锁定该阶段的 `ScoreTarget`（若为空，按现有 FSM 的有效增益块选择规则兜底）。动作仅从此刻起控制我方，对手沿用内置 FSM。奖励只从接管后事件和块位姿变化计算：锁定目标由我方真实 `BlockScore` +1；目标块未得分即 `BlockOff` −0.5；我方 `Drop` −1；每步 −0.001；距离目标块到最近台沿的减少量按 0.1 倍作为 potential shaping。`ScoreClock`、消极、罚分和对手得分不得奖励为推块成功。目标块出界、我方掉台或比赛结束时结束 episode；单集上限为 2400 个策略 tick。
- R5 只训练一个 Stable-Baselines3 PPO MlpPolicy 连续动作策略，使用 Gymnasium API；训练和评测 seed 严格分离。策略以 deterministic mode 评测，并写出模型、依赖版本、随机种子、训练步数、回报日志和逐场事件指标到用户指定的输出目录。
- R6 在至少 10 个预先固定、且未参与训练的 seed 上，将学习策略与同场景、同 seed、默认内置 FSM 比较。两者从同一首次 `SCORE_BLOCK` 进入帧开始计推块指标；未进入该阶段的 seed 也保留为零成功样本。指标区分锁定目标的我方真实 `BlockScore` 数、成功场次数、我方 `Drop` 数、无归属的 `BlockOff`、控制器 fault 和最终比分；被动比分不得计入推块成功。
- R7 在同一机器、同一场景上分别记录冷建模、热 reset（不含 FSM 预推进）、含预推进的完整 reset 和策略 step 的 p50/p95 耗时及 steps/s。热 reset 不得重新编译 MJCF；热 reset 的 p95 建立引擎耗时须不高于冷建模 p95 的 50%（20 次冷建模、100 次热 reset，记录原始样本和环境）。本试点不预设“每秒千次完整 episode”或“比 JSONL 慢 2–3 个数量级”为已验证事实；若不达门槛，报告失败和瓶颈，不引入向量化/MJX 扩范围。

## Constraints

- 只使用已修复且验收通过的 MuJoCo 官方场景；场景规则、裁判、台沿、物理参数和 fidelity.json 不因训练修改。
- Gymnasium / SB3 版本必须锁定在项目依赖文件；一次只支持单环境，不引入向量环境、分布式训练或自动超参搜索。
- 不将训练模型或大型日志默认写入 Git 跟踪目录；训练桥故障要明确失败，不能静默退回 FSM 或 legacy 物理。
- `match --controller-us` 是外部整场控制器入口；Gym 环境使用单个持久 CLI 子进程的训练专用 reset/step/close 入口，不能把每帧进程启动开销算进训练路径。训练复用模型的例外不改变普通比赛每场独立模型/数据的所有权。
- 若新建 Git 分支，按项目约定使用 test/ 前缀。

## Acceptance Criteria

- [x] AC1 前置 SEARCH 任务 AC4 通过；其报告记录官方 seed 42 我方真实 `BlockScore`，且无我方掉台。此项仅解除规划前置条件，不表示 RL 任务已经启动或训练通过。
- [ ] AC2 reset/step 符合 Gymnasium API；固定 seed 的 FSM 预推进终点与相同策略动作序列可复现，策略动作只在首次 `SCORE_BLOCK` 后生效；未进入阶段的 seed 有明确终止结果；MuJoCo 缺失/初始化失败时显式报错。相同模型连续至少 100 次热 reset 不重新编译 MJCF，模型身份变化不错误复用；每集 `mjData`/MatchEngine 状态隔离，连续 reset/close 后无未释放句柄。按 R7 保存冷/热/完整 reset 与 step 吞吐原始样本，热建引擎 p95 达到 50% 门槛。
- [ ] AC3 PPO 训练可从干净环境按文档命令完成，确定性评测能重新加载模型；输出包含实际依赖版本、seed、timesteps、逐场事件计数和回报统计。训练 seed 与 10 个留出 seed 不相交。
- [ ] AC4 留出集上策略至少产生一次可追溯的锁定目标我方真实 `BlockScore`；从相同阶段入口开始，累计我方该目标 `BlockScore` 数不低于同一留出集的 FSM 基线，且我方 `Drop` 数不高于基线。事件、台上位姿/块位移和得分归属吻合；若未达标如实判失败，不调裁判、不排除坏 seed、不以比分非零代替。
- [ ] AC5 运行定向环境检查、现有 Sim.Tests 和 MuJoCo 官方回放检查；legacy 回放逐位不变。策略只在独立评测命令中运行，默认 FSM、既有外部 JSONL 协议和默认场景不变。

## Out of Scope

- SAC 对照、MJX/JAX/GPU 移植、并行/分布式采样、在线自适应、真机迁移、视觉或传感器端到端训练。
- MBri 真车代码的灰度/数字 IR/电机量纲适配与 MANUAL 事件可观测性，归 `09-25-mbri-controller-portability`；本任务的块坐标仍为显式标记的仿真特权真值。
- 让 RL 策略取代默认 FSM，修改规则、奖励判分语义、物理模型、传感器契约或回放字段。

## 参考

GitHub 仓库及借鉴边界见 [research/references.md](research/references.md)；代码入口、训练桥和实施顺序见 [design.md](design.md) 与 [implement.md](implement.md)。
