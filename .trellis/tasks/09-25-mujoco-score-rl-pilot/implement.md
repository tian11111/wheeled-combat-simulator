# 实施清单：MuJoCo SCORE_BLOCK RL 试点

> 当前任务仍处于 planning。SEARCH 任务的 AC1–AC5 已在其报告中标记通过；完成本规划审查并按 Trellis 流程启动任务后才实施。逐项记录可复核命令和原始结果；不要把奖励回报代替裁判事件。

1. [x] 前置门禁：`.trellis/tasks/09-25-mujoco-search-targeting/report.md` 已记录 AC4 的官方 seed 42 我方真实 `BlockScore`、无我方掉台，并补齐 AC5；这是已完成的规划依赖，不需为开始编写训练桥重复跑整场。
2. [ ] 训练桥：在 Sim.Cli 加本地 JSONL rl-env 请求循环，只支持 reset/step/close；reset 创建新官方 MuJoCo 引擎、Arm() 后 `Tick(null, null)` 直到首次我方 `SCORE_BLOCK`，锁定其目标与入口 tick；仅后续 step 调用 `Tick(usAction, null)`。未进入阶段的 seed 记录 `no_score_block` 并在首个 step 终止，不跳过 seed。返回 Gymnasium 所需观测、reward、terminated/truncated 和指标。逐集释放引擎；stdout 仅发协议 JSON，错误写 stderr。Sim.Core、公共回放/Observation DTO 和既有 match 外部控制协议不改。
3. [ ] Gymnasium 环境：新增 controllers/score_block_rl/gym_env.py 封装持久 CLI 子进程；按 design.md 固定动作缩放、9 维空间、目标锁定、奖励和结束语义。处理协议版本/坏响应/子进程死亡为明确异常，close/context manager 关闭整个进程。新增 requirements 并锁定 Gymnasium、SB3、Python 版本；运行 Gymnasium env checker 和重复 seed/action 复现检查。
4. [ ] PPO 训练：实现 train.py，SB3 PPO MlpPolicy、单环境、默认 500,000 timesteps、固定训练 seed；保存模型、依赖版本、seed、配置、逐 episode 回报和运行耗时至 --out。不将生成的大型 artifacts 加入 Git。
5. [ ] 留出集评测：预先固定 10 个以上训练未用的 seed；evaluate.py 以 deterministic policy 从各 seed 首次我方 `SCORE_BLOCK` 入口运行，并以相同入口和策略 tick 上限跑内置 FSM 基线。保留没有进入阶段的 seed；逐场保存阶段入口、锁定目标、score/event/fault 指标。通过条件严格按 PRD AC4，真实我方 `BlockScore` 需能由目标索引出界、事件 tick、位移与角色归属相互印证。
6. [ ] 验证与交付：运行新增定向训练桥/环境测试、dotnet test RobotSimulator.sln -m:1、既有 MuJoCo integration/protocol tests 与 MuJoCo 新回放 replay-check；运行 replays/*.json legacy 回放检查。执行 git diff --check 并审阅 .trellis/spec/sim/index.md、.trellis/spec/backend/index.md。报告列明是否通过 AC4、策略使用 privileged state、依赖/模型哈希、各 seed 比较、失败场次及未验证项；不得晋升 fidelity.json。
