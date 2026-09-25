# 设计：MuJoCo SCORE_BLOCK RL 试点

## 边界与决策

训练目标是让单车把一块增益块推出台面并获得可归属的 BlockScore，沿用现有 MuJoCo、裁判和官方场景。唯一算法为 SB3 PPO。Gymnasium 负责 Python 侧训练 API；C# 侧在 Sim.Cli 增加本地逐步训练入口，直接调用 MatchEngineHost.Create、Arm()、Tick() 和 Dispose()。以 JSONL stdin/stdout 交换 reset、step、close 消息；stdout 只输出协议响应，诊断写 stderr。Sim.Core 不增加 IO 或训练依赖，现有 match --controller-us 仍是整场策略接口，不冒充 episode 环境。

每次 reset(seed) 复制官方 MuJoCo 场景、覆盖 episode seed、创建新引擎并发令。最近的有效增益块按初始距离选定并锁定，Buff 列表原始稳定次序用于本场索引。只将动作交给我方 Tick(action, null)，对手保持既有 FSM。step 上限 2400；成功、目标块无归属出界、我方掉台或引擎结束会结束 episode。超时按 Gymnasium truncated=true 返回。

## Python 环境契约

实现 controllers/score_block_rl/gym_env.py，对外使用 Gymnasium Env.reset(seed, options) -> (obs, info) 与 Env.step(action) -> (obs, reward, terminated, truncated, info)。连续动作 Box(-1, 1, shape=(2,)) 映射为 v=action[0] m/s、w=2*action[1] rad/s，随后沿用内核既有车辆限幅和 MuJoCo 执行器边界。

观测长度为 9，依次为：目标块相对车体坐标 x/y、目标块绝对坐标 x/y、我方前向速度、我方偏航率、我方 OnPlatform、目标块 OnPlatform、剩余比赛时间比例。各坐标除以官方平台边长后裁剪到 [-1,1]，速度按车辆 MaxSpeed/MaxTurnRate 归一化，布尔值为 0/1。块位置是 privileged state，训练和报告均显式标记。

奖励由环境桥生成，防止 Python 复刻裁判：BlockScore(role=us) +1；目标块 BlockOff 且未归属我方得分 −0.5；我方 Drop −1；每 tick −0.001；再加 0.1 * (edgeDistanceBefore - edgeDistanceAfter)，edge distance 由当前 MatchEngine.Field.DistToNearestEdge 计算。其他比分事件不奖励。info 至少包含 seed、tick、目标块索引、该集 BlockScore/BlockOff/Drop 事件数、DoneReason 和 bridge faults。

## 训练与评测

- train.py 使用单个 ScoreBlockEnv 和 SB3 PPO MlpPolicy，默认 500,000 timesteps、固定训练 seed；hyperparameters 未显式指定时使用并记录该锁定 SB3 版本默认值。模型、运行配置、回报和检查点写入必填 --out 目录。
- 训练集为运行时 seed 流；评测 seed 清单显式固定并从训练采样器排除。evaluate.py deterministic 加载模型，对清单中至少 10 个官方 seed 逐个完整运行。FSM 基线以同一 CLI 官方场景/seed 独立完整运行并统计 BlockScore、Drop 与 BlockOff 事件；评测期间不更新权重。
- 结果按每场列出 seed、结束原因、真 BlockScore 角色/数、我方掉台、块出界归属和 fault，附汇总均值、成功场次和基线对照。比分单独报告，不作为推块成功替代指标。

## 失败与回退

若先决 AC4 未过，保持任务 planning，不建训练环境。MuJoCo 原生环境不可用时立即报错，不退到 legacy。Gym 检查、PPO 训练或留出集门槛失败时保留运行元数据和失败 seed，结论为试点未通过；不更改 FSM、裁判或留出集以获得通过。模型只由独立训练/评测入口载入，不修改默认控制链路。
