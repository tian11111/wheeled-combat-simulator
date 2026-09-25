# MuJoCo SCORE_BLOCK 强化学习试点

## Goal

在确定性 MuJoCo SCORE_BLOCK 修复通过后，建立最小可复现的 Gymnasium 训练环境，用 Stable-Baselines3 PPO 训练并与当前确定性 FSM 基线比较。试点只评估单车推块行为，不将学习策略接入默认控制器，也不声称可用于真机。

## 前置条件

.trellis/tasks/09-25-mujoco-search-targeting/ 的 AC4 必须先通过：官方 MuJoCo seed 42 全场由我方产生真实 BlockScore，而非 ScoreClock 等被动比分；已有掉台循环卡点须关闭。AC4 未通过时本任务保持 planning，不开始训练或策略调参。

## Requirements

- R1 提供 episode 级 reset(seed) / tick 级 step(action) 环境。每次 reset 创建全新的官方 MuJoCo 场景和 MatchEngine，调用 Arm()，一步对应场景固定 0.05 s；episode 完成后释放原生资源。复用 Sim.Cli 宿主边界，IO 留在 CLI / Python，不能把进程、文件或 Gymnasium 依赖放进 Sim.Core。
- R2 Python 环境遵守 Gymnasium 五元组接口。动作为 Box([-1,-1],[1,1])，映射到 v = action[0] m/s、w = 2 * action[1] rad/s；观测固定为 9 个 float：目标增益块相对车体的 x/y、目标块绝对 x/y、车辆前向速度、偏航率、车辆是否在台、目标块是否在台、剩余比赛时间比例。坐标/速度按固定场景范围归一化并裁剪。
- R3 观测中的目标块坐标来自 MatchEngine.Blocks / Observation.objects 的仿真真值，属于仿真特权状态。报告不得把该模型描述为仅用传感器、视觉或可直接部署真机；相机/传感器观测策略留待独立任务。
- R4 每 episode 在 reset 时锁定距离我方初始位置最近的有效增益块；动作只控制我方，对手沿用内置 FSM。奖励只从事件和块位姿变化计算：我方归属的真实 BlockScore +1；目标块未得分即 BlockOff −0.5；我方 Drop −1；每步 −0.001；距离目标块到最近台沿的减少量按 0.1 倍作为 potential shaping。ScoreClock、消极、罚分和对手得分不得奖励为推块成功。目标块出界、我方掉台或比赛结束时结束 episode；单集上限为 2400 tick。
- R5 只训练一个 Stable-Baselines3 PPO MlpPolicy 连续动作策略，使用 Gymnasium API；训练和评测 seed 严格分离。策略以 deterministic mode 评测，并写出模型、依赖版本、随机种子、训练步数、回报日志和逐场事件指标到用户指定的输出目录。
- R6 在至少 10 个预先固定、且未参与训练的 seed 上，将学习策略与同场景、同 seed、默认内置 FSM 比较。指标区分我方真实 BlockScore 数、产生至少一次我方 BlockScore 的场次数、我方 Drop 数、无归属的 BlockOff、控制器 fault 和最终比分；被动比分不得计入推块成功。

## Constraints

- 只使用已修复且验收通过的 MuJoCo 官方场景；场景规则、裁判、台沿、物理参数和 fidelity.json 不因训练修改。
- Gymnasium / SB3 版本必须锁定在项目依赖文件；一次只支持单环境，不引入向量环境、分布式训练或自动超参搜索。
- 不将训练模型或大型日志默认写入 Git 跟踪目录；训练桥故障要明确失败，不能静默退回 FSM 或 legacy 物理。
- 若新建 Git 分支，按项目约定使用 test/ 前缀。

## Acceptance Criteria

- [ ] AC1 前置 SEARCH 任务 AC4 通过；任务报告能定位到官方 seed 42 我方真实 BlockScore，且无重复掉台循环。未满足时停止并保持本任务 planning。
- [ ] AC2 reset/step 符合 Gymnasium API；固定 seed 重置结果和相同动作序列可复现；MuJoCo 缺失/初始化失败时显式报错；连续 reset/close 后无未释放引擎或原生句柄。
- [ ] AC3 PPO 训练可从干净环境按文档命令完成，确定性评测能重新加载模型；输出包含实际依赖版本、seed、timesteps、逐场事件计数和回报统计。训练 seed 与 10 个留出 seed 不相交。
- [ ] AC4 留出集上策略至少产生一次可追溯的我方真实 BlockScore；累计我方 BlockScore 数不低于同一留出集的 FSM 基线，且我方 Drop 数不高于基线。事件、台上位姿/块位移和得分归属吻合；若未达标如实判失败，不调裁判、不排除坏 seed、不以比分非零代替。
- [ ] AC5 运行定向环境检查、现有 Sim.Tests 和 MuJoCo 官方回放检查；legacy 回放逐位不变。策略只在独立评测命令中运行，默认 FSM、既有外部 JSONL 协议和默认场景不变。

## Out of Scope

- SAC 对照、MJX/JAX/GPU 移植、并行/分布式采样、在线自适应、真机迁移、视觉或传感器端到端训练。
- 让 RL 策略取代默认 FSM，修改规则、奖励判分语义、物理模型、传感器契约或回放字段。

## 参考

GitHub 仓库及借鉴边界见 [research/references.md](research/references.md)；代码入口、训练桥和实施顺序见 [design.md](design.md) 与 [implement.md](implement.md)。
