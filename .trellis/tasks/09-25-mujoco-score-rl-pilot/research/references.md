# GitHub 参考仓库及本任务取舍

以下只借鉴接口、算法封装和评测方法；不复制其他机器人的动力学，也不把不同物理栈混成一个 MuJoCo 后端。

| 仓库 | 可借鉴内容 | 本任务采用方式 |
|---|---|---|
| [Farama-Foundation/Gymnasium](https://github.com/Farama-Foundation/Gymnasium) | 环境契约：reset(seed, options) 返回 observation/info；step(action) 返回 observation、reward、terminated、truncated、info；通过环境 checker 检查 space 和返回值。 | ScoreBlockEnv 使用标准接口；将比赛终局与步数截断分开表达。 |
| [DLR-RM/stable-baselines3](https://github.com/DLR-RM/stable-baselines3) | Gymnasium 兼容的 PPO/SAC 实现、连续 Box 动作策略与 deterministic policy 推理。 | 试点只实现 PPO MlpPolicy；SAC 不进入本任务，避免扩成算法竞赛。 |
| [DLR-RM/rl-baselines3-zoo](https://github.com/DLR-RM/rl-baselines3-zoo) | 训练配置、独立评测、checkpoint、日志和固定 seed 管理方式。 | 只借鉴配置与评测纪律；不引入 Zoo 的完整超参搜索/训练框架。 |
| [Farama-Foundation/Gymnasium-Robotics](https://github.com/Farama-Foundation/Gymnasium-Robotics) | Fetch Push 等 goal-conditioned pushing 环境把任务成败与目标距离/进展拆开；适度 dense shaping 能辅助稀疏成功事件。Fetch 是机械臂，任务状态和接触物理与本项目不同。 | 只借鉴真实成功事件 + potential progress 奖励原则；按 BlockScore 记成功，按块至最近台沿距离变化塑形，不复制 Fetch 奖励常数或动作模型。 |
| [google-deepmind/mujoco_playground](https://github.com/google-deepmind/mujoco_playground) | 使用 MuJoCo/MJX 与 JAX 的可加速 GPU 环境组织方式。 | 当前模拟器是 .NET 经官方 MuJoCo C API 运行；本任务先复用它的确定性内核。MJX/JAX 需另建等价性与物理验证任务，暂缓。 |

## 本仓库接口核实

- src/Sim.Core/MatchEngine.cs 已有 Arm()、Tick(RobotAction?, RobotAction?)、Done、Scores、Events、Blocks 与 Field。传入我方动作会让我方逐 tick 进入 manual 控制，null 使对手留在内置 FSM；每个 episode 可通过新引擎获得清洁 reset。
- src/Sim.Hosting/MatchEngineHost.cs 按场景选择 MuJoCo；native model 生命周期由 MatchEngine.Dispose() 结束。桥应位于 CLI 宿主。
- docs/CONTROLLER_PROTOCOL.md 与 src/Sim.Controller/ExternalControllerBridge.cs 当前提供逐 tick JSONL 策略 I/O，但 CLI match 将其运行到整场结束；它本身不提供 Python 侧 Gymnasium episode reset/step/reward，因此训练计划增加本地逐步桥。
- src/Sim.Protocol/Observation.cs 的 objects.buffs[].x/y 含物体位置真值，故使用它是 privileged state。此 pilot 的观测策略和评测声明必须保留该限制。
