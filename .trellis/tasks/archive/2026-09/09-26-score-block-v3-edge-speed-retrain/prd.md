# SCORE_BLOCK v3 归属修复后重训与近沿限速候选

## Goal

在归属判定已修复的官方 MuJoCo 场景中重新训练 PPO，检验它能否在预注册的 `development_v3` 上同时达到锁定目标真实得分与我方掉台门槛。若无防护策略未达标，只增加一项近沿前进限速，再按同一门槛评测。只有冻结合格候选后才打开 `final_holdout_v3` 一次。

## Confirmed facts

- 归属修复任务已归档；修复前训练模型受错误奖励污染。已揭示集重放的掉台为我方 28、FSM 21，这是诊断材料，不是 v3 门槛证据。
- 掉台诊断中，6001–6050 的策略掉台瞬间速度 >0.4 m/s 为 23/28，FSM 为 3/21；22/28 次策略掉台时已发出强倒车。相关性不等于限速有效的因果证明。
- FSM 在目标块距台沿 <0.45 m 时把前进速度限为 0.35 m/s。`rl-env` 的 reset/step info 提供当前目标块 `target_edge_distance`；Gym 线速度动作单位为 m/s。
- `development_v3` 固定 7001–7020，`final_holdout_v3` 固定 8001–8050；历史训练/开发/留出 seed 已揭示，不得重新作盲验。11 维 PPO 观测含仿真真值块坐标。

## Requirements

- R1 保持当前观测、奖励、物理、裁判、场景和 SB3 2.9.0 默认 PPO 参数不变，按既定训练池、RNG seed 20260925、500,000 请求步数和 51,200 步 checkpoint 间隔训练无防护基线。保存模型、CSV、checkpoint 步数与哈希、场景/CLI/依赖哈希。
- R2 基线全部 checkpoint 与最终模型在 `development_v3` 用 deterministic 动作评测；逐 seed 与同入口 FSM 配对，保留 `no_score_block`、真实锁定目标 `BlockScore`、我方 `Drop`、无归属出界、fault 和归因歧义。合格条件：至少一次真实得分、目标得分总数不低于 FSM、我方掉台不高于 FSM、得分可追溯。合格者按目标得分多、掉台少、训练步数多排序。
- R3 若基线在开发集有合格候选，直接冻结它。若没有，仅增加一种候选：当前目标块距台沿严格小于 0.45 m 时，将 PPO 正向线速度指令上限钳为 +0.35 m/s；其余正向指令、倒车与角速度保持原样。训练和评测使用同一模式；启用模式而距离缺失/非有限时明确失败。记录原始/实际动作及触发次数，不能称为纯 PPO 输出。
- R4 若启用限速候选，用同样训练预算、seed、依赖和 checkpoint 设置重训；只在 `development_v3` 按 R2 评测。模式参数进入训练配置、评测结果和冻结记录；模型与评测模式不符必须拒绝。若仍无合格模型，记录失败并停止，不打开最终集。
- R5 冻结唯一候选的模型路径、步数、SHA-256、限速模式、场景/CLI 哈希及开发集指标后，只对 `final_holdout_v3` 做一次同 seed 配对盲验。按真实裁判事件门槛判定并保留失败 seed；不根据最终集换模型或改门槛。
- R6 不改 `Sim.Core`、`Sim.Mujoco`、场景、默认 FSM、奖励常量、11 维观测或 `fidelity.json`。训练产物写在 Git 跟踪目录之外；报告明确 privileged state、单训练 RNG seed、历史集合已揭示与未验证项。

## Acceptance Criteria

- [x] AC1 无防护的修复后 500,000 步训练完成（请求 500,000，实际 501,760），模型、9 个 checkpoint、`run-config.json`、`progress.csv`、`episodes.monitor.csv` 与 SHA-256 可核对；开发集 20 个 seed 的 PPO/FSM 逐 seed 结果完整。证据见 `dev-v3-sweep.json` 与 `report.md` 第 1–2 节。
- [x] AC2 **未触发、不适用**：基线在 `development_v3` 存在合格候选（`ppo_score_block.zip`、`rl_model_409600_steps.zip`），按 R3 直接冻结，未实现 `target_edge_speed_cap_v1` 限速层。报告已写明基线合格并标记 AC2 不适用，满足 AC2 的未触发分支。
- [x] AC3 开发集选模和冻结遵守 R2–R5；`development_v3` 有合格模型故打开最终集一次；冻结记录早于任何 v3 最终集运行，`freeze_checks` 四项全 true；`final_holdout_v3` 只运行一次，结果失败（策略得分 2 < FSM 6）已如实记录，未换模型、未改门槛、未用 `--force`。
- [x] AC4 代码变更后定向 Python 检查（收尾复跑 40 通过 / 0 失败 / 0 跳过）、Gymnasium `check_env`（obs `(11,)`）与固定 seed 确定性、完整 Sim.Tests（388/0/0）、六份 legacy 回放（全 PASS）及官方 MuJoCo 新模式回放（8:3、117/117、bit-for-bit PASS）通过；`git diff --check` 通过，默认比赛链路未改动。此前两项 `TemporaryDirectory` 权限失败及其后续直接复跑证据见 `report.md` 第 5 节。
- [x] AC5 报告（本任务 `report.md`）含逐 seed 裁判事件、PPO/FSM 对照、训练诊断、模型/场景/CLI 哈希、限速触发量（未触发，标记不适用）与失败场次，并声明单次训练与特权状态的结论范围。

## Out of scope

- SAC、超参搜索、向量环境、MJX、真机迁移、传感器/视觉策略、修改评分规则、在已揭示集合上宣称新盲验通过。
