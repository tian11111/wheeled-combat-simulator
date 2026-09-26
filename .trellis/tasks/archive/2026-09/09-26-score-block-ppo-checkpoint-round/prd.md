# SCORE_BLOCK PPO 下一轮：checkpoint 选模与新留出集盲验

## 背景与前置

上一轮任务 [09-25-mujoco-score-rl-pilot](../../09-25-mujoco-score-rl-pilot/report.md) 的 AC4 **未通过**：冻结的 11 维 51,200 步模型在首次最终留出集 4001–4010 上锁定目标我方真实 `BlockScore` 为 1（FSM 0），但我方 `Drop` 为 8（FSM 7），掉台门槛失败。该结论已冻结，本任务**不得改写、不得追认为通过**。

本轮是实现该报告“SB3 官方参考后的下一轮规划”与 [research/references.md](../../09-25-mujoco-score-rl-pilot/research/references.md) 中已预注册方案的独立后续任务。3001–3010 与 4001–4010 都已被揭示，只能作为历史对照，**不能**作为新模型的盲验集。

## Goal

在**不改任务语义**的前提下补齐训练可观测性（SB3 CSV 诊断 + 定期 checkpoint），用全新的开发集在多个训练快照中按项目裁判指标选出唯一候选，再对新最终留出集盲验一次，并如实记录结果。

## Requirements

- **R1 语义冻结**：11 维观测、奖励 v2（0.0001 步长成本、1/0.5/1 事件、0.1 边缘 shaping）、官方场景、裁判、物理、单环境 PPO **默认参数**、默认内置 FSM 一律不变。观测仍含仿真真值块坐标，训练配置与报告必须始终标记 `privileged_state=true`，不得描述为真机可部署策略。
- **R2 数据划分 v2**：训练循环 seed 池仍为 42、1000–1999；SB3 RNG 与首次 reset seed 仍为 20260925。新开发集固定 5001–5020，新最终留出集固定 6001–6050。两组与训练池及历史已揭示的 3001–3010、4001–4010 互斥。评测入口必须显式打印/落盘新 split 名称与版本；旧 `--final-holdout` 继续**只**表示历史 4001–4010；自定义 seed 仍只能标为探索。split 混用或任何 seed 重叠必须被拒绝并给出明确错误。
- **R3 训练可观测性**：`train.py` 增加 SB3 CSV logger（保留 PPO 优化诊断，如 `train/approx_kl`、`train/clip_fraction`、`train/explained_variance`、`train/value_loss`）与每 **51,200 个单环境 step** 的 `CheckpointCallback`；继续保存最终模型与 Monitor 逐集 CSV。`run-config.json` 记录 split 版本、每个 checkpoint 的训练步数与 SHA-256、场景/CLI/依赖版本与关键哈希。**不得**用 `EvalCallback` 默认的平均回报在训练中自动覆盖“最佳”模型。
- **R4 开发集选模**：训练结束后，用**独立评测环境**、`deterministic=True`，逐个把每个 checkpoint 与最终模型在 5001–5020 上完整评测。PPO 与 FSM 必须按**相同 seed、相同首次 SCORE_BLOCK 入口**配对；保留全部 `no_score_block`、掉台、归因歧义与 controller fault 样本。合格模型须同时满足：①至少一次锁定目标我方**真实** `BlockScore`；②目标得分总数不低于同留出集 FSM；③我方 `Drop` 不高于 FSM。合格者按目标得分多 → 掉台少 → 训练步数多依次排序，选出**唯一**候选。若没有合格模型：记录失败并**停止**，不运行新最终集。
- **R5 冻结与单次盲验**：先冻结候选模型路径、训练步数、SHA-256、场景/CLI 哈希与开发集结果，再对 6001–6050 只运行**一次**最终评测。最终入口须校验冻结记录中的模型哈希，并拒绝覆盖已存在的最终结果文件（除非显式强制）。逐 seed 保存 PPO/FSM 真实裁判事件、失败样本与歧义样本；最终结果无论成败都如实报告。
- **R6 定向验证与回归**：为 split 路由与互斥、checkpoint 步数/哈希、CSV 可读性增加定向自动检查。代码变更后运行现有 `Sim.Tests`、Gymnasium 环境检查（`check_env`）与回放检查（legacy 与 MuJoCo）。
- **R7 报告**：报告写清训练诊断（CSV/逐集日志）、逐 seed 指标、吞吐、失败原因、`no_score_block` 与 fault，并明确“单个训练 RNG seed”的结论范围。不得接入默认 FSM，不得晋升 `fidelity.json`。

## Constraints

- 不改上一轮任务的 `status` 与其 AC4 失败结论；本任务为独立任务，依赖关系写在本文件而非树形结构。
- 不改 `.trellis/tasks/09-25-mujoco-score-rl-pilot/` 下已提交的内容（其 report/references 的工作区新增规划章节保留）。
- 不引入向量环境、断点续训、自动超参搜索、`EvalCallback` 选模、SAC、MJX。
- 训练产物（模型、checkpoint、CSV、评测 JSON）写在 Git 跟踪目录之外（`%TEMP%`），报告只引用路径与哈希。
- 保留工作区已有的无关改动；若新建 Git 分支，名称以 `test/` 开头。

## Acceptance Criteria

- [x] **AC1（可观测性）** `train.py` 在默认 500,000 步训练中产出：`progress.csv`（含 PPO 诊断列）、至少 9 个 `rl_model_<steps>_steps.zip` checkpoint、最终 `ppo_score_block.zip`、`episodes.monitor.csv`、`run-config.json`；`run-config.json` 内每个 checkpoint 的步数与 SHA-256 与磁盘文件一致；未使用 `EvalCallback` 选模。
- [x] **AC2（split 路由与互斥）** 新开发 5001–5020、新最终 6001–6050 可被显式路由并正确标注；旧 `--final-holdout` 仍只解析为 4001–4010；split 混用、seed 重叠（训练池/历史 split/跨 split）均被拒绝并有明确错误；定向检查全部通过。
- [x] **AC3（开发集选模）** 所有 checkpoint 与最终模型均在 5001–5020 上用独立评测环境、`deterministic=True` 完成 PPO/FSM 同 seed 配对评测；逐模型给出三项合格判定与排序键；选出唯一候选或明确记录“无合格模型并停止”。判据只用真实裁判事件，不用回报或最终比分。
- [x] **AC4（冻结与单次盲验）** 候选冻结记录含路径/步数/SHA-256/场景与 CLI 哈希/开发集结果摘要，并在最终评测前生成；最终评测只运行一次 6001–6050，输出标注 `final_holdout_v2`、逐 seed 事件与门槛结论；若 AC3 无合格模型则不运行最终集并如实记录。
- [x] **AC5（回归与交付）** 变更后 `dotnet test RobotSimulator.sln -m:1`、Gymnasium `check_env`、legacy 六份回放与 MuJoCo 新回放 `replay-check` 全部通过；`git diff --check` 干净；README 与 `.trellis/spec/sim/index.md` 同步新 split/checkpoint 纪律。
- [x] **AC6（报告诚实性）** 报告含训练诊断、逐 seed 配对指标、吞吐、失败样本、特权状态限制、单 seed 结论范围，并显式声明本轮结果**不**追认为上一轮 AC4 通过。

## Out of Scope

- 跨随机初始化稳定性（需多次独立训练）、超参搜索、奖励再设计、观测再设计、视觉/传感器端到端策略。
- 修改场景、裁判、奖励语义、物理参数、默认 FSM、`fidelity.json`、回放字段。
- 复用 3001–3010 / 4001–4010 作为新模型盲验；用最终集调参或换模型。

## 参考

- 上一轮报告与失败结论：[../09-25-mujoco-score-rl-pilot/report.md](../09-25-mujoco-score-rl-pilot/report.md)
- SB3 核对与借鉴边界：[../09-25-mujoco-score-rl-pilot/research/references.md](../09-25-mujoco-score-rl-pilot/research/references.md)
- 本轮 SB3 接口核实：[research/sb3-checkpoint-diagnostics.md](research/sb3-checkpoint-diagnostics.md)
- 技术设计：[design.md](design.md)；执行清单：[implement.md](implement.md)
