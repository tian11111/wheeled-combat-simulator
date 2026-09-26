# 设计：MuJoCo SCORE_BLOCK RL 试点

## 边界与决策

训练目标是让单车把一块增益块推出台面并获得可归属的 BlockScore，沿用现有 MuJoCo、裁判和官方场景。唯一算法为 SB3 PPO。Gymnasium 负责 Python 侧训练 API；C# 侧在 Sim.Cli 增加单个持久本地训练入口，以 JSONL stdin/stdout 交换 reset、step、close 消息；stdout 只输出协议响应，诊断写 stderr。Sim.Core 不增加 IO 或训练依赖，现有 match --controller-us 仍是整场策略接口，不冒充 episode 环境。

首次 reset(seed) 复制官方 MuJoCo 场景、覆盖 episode seed、生成 MJCF 并编译 `mjModel`。训练会话持有该只读模型；后续相同模型内容哈希与原生版本的 reset 不再编译，通过训练专用 physics factory 为每集新建 MatchEngine 运行时和独立 `mjData`。每集均重新 Arm，再用 `Tick(null, null)` 让双方内置 FSM 推进到我方首次 `SCORE_BLOCK`。以该帧为策略起点，锁定 `ScoreTarget` 的 Buff 列表索引；若目标为空，按现有 FSM 规则选择有效增益块。只有后续 step 才向我方传 `Tick(action, null)`，对手始终用内置 FSM。现有 `Tick` 一旦接受我方动作就将其切为 `Manual`，本试点因此不尝试在该集切回 FSM；策略阶段在锁定目标得分/出界、我方掉台、比赛结束或 2400 个策略 tick 时结束。若预推进直到比赛结束仍无 `SCORE_BLOCK`，reset 返回合法零观测及 `no_score_block` 信息，下一次 step 不执行动作并立即以零成功终止；评测保留这个 seed。超时按 Gymnasium `truncated=true` 返回。

共享模型仅在训练会话中存在，由 session 持有原生句柄；episode 持有独立 `mjData`，先释放旧 episode 再创建新 episode，session 关闭时释放模型。不要在现有普通比赛 factory 上加全局缓存。模型哈希不匹配时重新建模并记录，不能让缓存掩盖场景差异；模型/数据的所有权、异常路径和重复 close 需要定向测试。此为“复用模型 + 全新比赛状态”的 reset，暂不复用 `mjData`，避免 MatchEngine 的计时、裁判、FSM、接触记录跨集泄漏。若后续要加 `mj_resetData`，须另证所有宿主状态都被复位。现行 `.trellis/spec/sim/index.md` 的“每场独立模型/数据”继续约束普通比赛；实现完成后为训练专用例外更新该规范。

## Python 环境契约

实现 controllers/score_block_rl/gym_env.py，对外使用 Gymnasium Env.reset(seed, options) -> (obs, info) 与 Env.step(action) -> (obs, reward, terminated, truncated, info)。连续动作 Box(-1, 1, shape=(2,)) 映射为 v=action[0] m/s、w=2*action[1] rad/s，随后沿用内核既有车辆限幅和 MuJoCo 执行器边界。

观测长度为 11。前 9 项顺序不变：目标块相对车体坐标 x/y、目标块绝对坐标 x/y、我方前向速度、我方偏航率、我方 OnPlatform、目标块 OnPlatform、剩余比赛时间比例；末尾追加我方相对平台中心 x/y，除以平台半边长，使平台边缘为 ±1。前 4 项坐标仍按既有平台边长归一化并裁剪到 [-1,1]，速度按车辆 MaxSpeed/MaxTurnRate 归一化，布尔值为 0/1。块位置是 privileged state，训练和报告均显式标记。旧 9 维策略与新输入形状不兼容，保留旧结果但须另训 11 维策略。

奖励由环境桥生成，防止 Python 复刻裁判：接管后锁定目标被我方真实推下并获得 `BlockScore` +1；该目标 `BlockOff` 且未归属我方得分 −0.5；我方 `Drop` −1；每策略 tick −0.0001（原值 −0.001 会让早掉台优于待到时限，失败数据见 report.md）；再加 0.1 * (edgeDistanceBefore - edgeDistanceAfter)，edge distance 由当前 MatchEngine.Field.DistToNearestEdge 计算。其他比分事件不奖励。两个 Buff 可同名，须按锁定索引的 `Out` 状态转变、事件 tick 和得分角色共同确认目标归属；同 tick 无法辨清时不计成功并在 info 标记歧义。info 至少包含 seed、阶段入口 tick、策略 tick、目标块索引、该集 BlockScore/BlockOff/Drop 事件数、DoneReason 和 bridge faults。

## 训练与评测

- 在训练前用同机同版本基准命令比较 20 次冷建模与 100 次热 reset，分别量建模/引擎创建、FSM 预推进和策略 step，保存 p50/p95、原始样本、模型编译次数、steps/s、DLL/模型哈希。热建引擎 p95 必须不高于冷建模 p95 的 50%；完整 reset 时间单独报告，不承诺每秒千次完整 episode。持久 CLI 每帧 JSONL 开销也实测，不沿用整场外部控制器的吞吐猜测。
- train.py 使用单个 ScoreBlockEnv 和 SB3 PPO MlpPolicy，默认 500,000 timesteps、固定训练 seed；hyperparameters 未显式指定时使用并记录该锁定 SB3 版本默认值。完成后的模型、运行配置和逐集回报写入必填 --out 目录。试点不实现断点续训；中断运行保留日志并明确标为未完成。
- 训练 episode 池固定为 42、1000–1999；SB3 训练及首次 reset seed 为 20260925。3001–3010 已用于 v1/v2 调试，归开发验证集；4001–4010 在模型冻结前不得运行，归最终留出集。evaluate.py 默认运行开发集，显式 `--final-holdout` 才运行最终集，自定义 `--seeds` 结果标为探索评测且不计 AC4。deterministic 加载模型逐 seed 评测。FSM 基线用相同官方场景与 seed，先到同一首次 `SCORE_BLOCK` 入口，再在同一策略阶段上限内运行内置 FSM；只统计阶段入口后的锁定目标得分、掉台与出界。未到达阶段的 seed 不删除。评测期间不更新权重。
- 11 维候选仅比较固定种子与默认 PPO 参数下的 51,200 步短训和 500,000 步正式训练。模型冻结前只看开发集：先要求锁定目标得分至少 1、总得分不低于 FSM 且我方掉台不高于 FSM；合格候选中按目标得分多、掉台少排序，平局选训练步数更多者。冻结所选模型及哈希后，最终留出集只运行一次；若失败如实报告，不根据最终集换模型。
- 结果按每场列出 seed、结束原因、真 BlockScore 角色/数、我方掉台、块出界归属和 fault，附汇总均值、成功场次和基线对照。比分单独报告，不作为推块成功替代指标。

## 失败与回退

若先决 AC4 未过，保持任务 planning，不建训练环境。MuJoCo 原生环境不可用时立即报错，不退到 legacy。Gym 检查、PPO 训练或留出集门槛失败时保留运行元数据和失败 seed，结论为试点未通过；不更改 FSM、裁判或留出集以获得通过。模型只由独立训练/评测入口载入，不修改默认控制链路。
