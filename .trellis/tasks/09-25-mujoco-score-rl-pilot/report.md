# MuJoCo SCORE_BLOCK RL 试点实施与验收记录（2026-09-26）

## 当前结论

任务状态 `in_progress`。训练专用持久 JSONL CLI、单环境 Gymnasium 包装、PPO 训练/评测脚本已能运行。11 维版本的 AC2 功能、确定性、资源与热建模门槛已复测通过；AC3 的干净环境默认步数训练与模型重新加载已完成。冻结的 11 维短程候选在首次最终留出集上得分条件达标，但我方掉台 8 次高于 FSM 的 7 次，**AC4 未通过**。模型只用于独立评测；观测含仿真真值块坐标，不能称为真机可部署策略。

## 本轮修复

- `RlEnvCommand`：FSM 预推进结束才设阶段事件游标；奖励要求锁定块出界、同 tick 我方真实 `BlockScore`，同名块同时出界标记歧义；输出阶段 seed/事件/比分与锁定块入口、出口位姿；异常和 close 先释放每集 `mjData`，再释放共享 `mjModel`。
- Python：`gym_env.py` 显式报告协议与子进程错误；依赖版本锁定；`train.py` 输出模型、环境/依赖/场景哈希、PPO 参数与 UTF-8 逐集 CSV；`evaluate.py` 固定互斥 seed，策略与 FSM 各自从相同 seed 的首次 `SCORE_BLOCK` 入口评测，记录目标块归因、裁判事件、掉台、出界、fault、比分和位置交叉核验。
- `TrainingResetPerformanceTests` 断言 20 次冷建模、100 次同模型热 reset 的编译数与 50% 门槛，另测模型内容变化会重新编译。`benchmark.py` 实测持久进程中的完整预推进 reset 与策略 step。训练专用模型所有权例外已写入 `.trellis/spec/sim/index.md`。
- 原奖励 v1 的每步 −0.001 在剩余约 2240 tick 内累计约 −2.24，早掉台只 −1；51,200 步训练的 142 集中 111 集掉台，3001–3010 开发验证集锁定目标得分 0。已把步长成本降为 −0.0001（全长最多约 −0.24）并同步 PRD/design，保留 v1 产物供对照。v2 同为 51,200 实际步，训练集 129 集中 100 次掉台、2 次目标得分；开发验证集仍 0 次目标得分、7 次掉台，未跨过 AC4 指标。
- MBri 适配器的 `robot.vehicle` 限幅和 tick 跳帧归零修复已存在于当前仓库；本轮重新运行自测通过，未覆盖或改写其历史改动。

## 可复核验证

11 维观测修订后的复测（同日，Windows x64 / 临时 .NET SDK 8.0.425 / Python 3.12.10）：`RlEnvCommandTests` 7/7、`dotnet test RobotSimulator.sln -m:1 --no-restore` 382/382；Python 三文件 `py_compile`、Gymnasium `check_env`、seed 42 重复 reset 与固定动作 100 步逐帧一致均通过。评测入口对含训练池 seed 42 的自定义 10 seed 清单返回明确重叠错误。这一组检查验证了新观测契约与确定性。下表保留修订前的历史证据。

11 维 PPO 短程对照：`py -3.12 controllers/score_block_rl/train.py --steps 51200 --out "$env:TEMP\robot-simulator-rl-11d-50k-20260926"` 成功，实际 51,200 步、150 行逐集日志、约 149.3 steps/s；锁定依赖版本为 Gymnasium 1.3.0、SB3 2.9.0、Torch 2.13.0、NumPy 2.5.0。默认开发集 3001–3010 deterministic 评测中，策略和 FSM 均有 1 次锁定目标我方真实 `BlockScore`、我方 `Drop` 均为 6；策略唯一得分在 seed 3008，事件/位姿交叉核验为 true。3 个 seed 未进入阶段，均保留。结果保存于同目录 `dev-evaluation.json`，split 明确为 `development`、`ac4_claim_eligible=false`。这些开发集数据当时支持继续训练，**不能作为 AC4 最终验收**；后续完整训练与最终评测结果见下文。

AC2 原生资源运行审计：用同一 CLI 进程连续做 101 次 seed 42 reset，外部读取 Windows 进程句柄数在第 0/20/60/100 次均为 187；私有内存依次为 233.3/249.0/251.1/248.2 MB，热身后没有单调增长。随后发送缺 seed 的 reset，桥明确报错；再次合法 reset 成功，重复 close 无异常且子进程退出。这是进程级泄漏检查，结合 `TrainingResetPerformanceTests` 的模型编译数与 `RlEnvCommand` 的数据/模型释放顺序，支持 AC2 资源门槛；它不等于原生分配器逐对象计数。

干净环境编码故障与修复：首次 500,000 步运行随交互工具会话中断，保留约 587 行未完成日志，无模型；随后后台重启在 Windows GBK 默认编码下于早期逐集日志写入时抛出 `UnicodeEncodeError`。已将 `rl-env` 的 stdin/stdout 固定 UTF-8，并要求 Windows 训练进程以 `-X utf8` 启动，编码不符时立即报错。锁定依赖的干净虚拟环境中 2,048 步 UTF-8 烟测已完成、逐集 CSV 可读；随后从头启动默认 500,000 步，输出目录为 `%TEMP%/robot-simulator-rl-11d-500k-utf8-20260926/`。这两次中断不计入训练步数。

模型冻结记录（最终留出集运行前）：干净环境默认训练完成 501,760 实际步（SB3 rollout 补齐）、728 行逐集日志、866.584 s，模型 SHA-256 `9d662c00994899ea34654f72821377e0c96d21e8f2e7eb3d1d457e940c446296`；其 3001–3010 开发集锁定目标得分 0、我方掉台 5，FSM 为 1/6，未达预定候选门槛。用当前 UTF-8 CLI 在同一干净环境重训 51,200 步，150 行日志、75.795 s，模型 SHA-256 `b943a92d2bee0095cd2be04535be07086ea4dbbeb61915afe15c4c23860104b7`；开发集策略与 FSM 均为目标得分 1、我方掉台 6。按 design.md 的预先选模规则，冻结这份 51,200 步模型作为唯一最终候选；在记录冻结决定时，最终留出集 4001–4010 尚未运行。模型路径 `%TEMP%/robot-simulator-rl-11d-50k-clean-20260926/ppo_score_block.zip`。

冻结后首次最终评测：`evaluate.py --final-holdout` 对 4001–4010 的 10 个 seed 全部保留，`evaluation_split=final_holdout`，`ac4_claim_eligible=true`。策略锁定目标我方真实 `BlockScore` 为 1（seed 4002，事件/目标块位姿交叉核验 true），FSM 为 0；策略我方 `Drop` 8 次，FSM 为 7 次，故 AC4 的掉台门槛失败。策略另有 seed 4009 的目标归因歧义，未计成功；双方 controller faults 均为 0。逐 seed 原始记录在 `%TEMP%/robot-simulator-rl-11d-50k-clean-20260926/final-holdout.json`；文件中的模型哈希与冻结值一致，CLI DLL SHA-256 为 `5ae11229095716c2d3e01709d0063debc13382d0b21cec134cf880a9de29eda4`，场景 SHA-256 为 `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3`。不可用 18:14 对 14:9 的比分总和替代锁定目标得分，不可删掉 seed 4003/4005/4006 等掉台差异，也不可基于这组最终集改选 500,000 步模型。

| 检查 | 结果 |
|---|---|
| `python controllers/mbri_adapter_selftest.py` | 全部通过，含嵌套车辆限幅和跳帧 `healthy=False` |
| `dotnet test RobotSimulator.sln -m:1 --no-restore` | 380/380 通过（Windows x64 / .NET 8.0.31） |
| Gymnasium `check_env`；seed 42 重复 reset + 同样 100 动作 | 通过，观测/奖励/比分逐帧一致 |
| seed 1017 未进入阶段；首个 step；重复 close | `no_score_block` 保留原 seed、零奖励并立即终止；重复 close 无异常 |
| 6 个 `replays/*.json` legacy `replay-check` | 逐文件 bit-for-bit PASS |
| 官方 MuJoCo seed 42 新录制回放 `replay-check` | 8:3，117/117 事件，bit-for-bit PASS；文件在 `%TEMP%/robot-simulator-rl-mujoco-seed42.json` |
| 20 冷 / 100 热建模（创建引擎与 `mjData`，各 tick 一次） | 最终复测冷 p95 1.188 ms，热 p95 0.123 ms，比率 0.103；20 次编译，100 次热建模零新增编译 |
| 100 次完整 reset（含预推进）/ 1000 策略 step | 最终串行复测 reset p50/p95 425.42/808.45 ms；step p50/p95 3.670/5.700 ms；采样段 264.7 steps/s；6 个 seed 未进入阶段。早前同机单次运行的 reset p50/p95 为 62.15/277.13 ms、step p50/p95 为 0.515/0.848 ms；性能随运行环境显著波动，原始文件保存最终复测样本，不能声称稳定达到早前吞吐 |
| 奖励 v1 的 51,200 实际 PPO 步 + 3001–3010 开发验证集 | 142 集日志可读取（1 次训练目标得分、111 次掉台）；开发集策略目标得分 0、掉台 7，FSM 得分 1、掉台 6，**未达到 AC4 指标** |
| 奖励 v2 的 51,200 实际 PPO 步 + 相同开发验证集 | 129 集日志可读取（2 次训练目标得分、100 次掉台）；开发集策略目标得分 0、掉台 7、无归属出界 2；FSM 得分 1、掉台 6，**仍未达到 AC4 指标** |

原始样本：[perf-reset-2026-09-26.json](evidence/perf-reset-2026-09-26.json)、[perf-full-2026-09-26.json](evidence/perf-full-2026-09-26.json)。v1 模型、逐集日志与当时称为留出集的开发集结果保存在 `%TEMP%/robot-simulator-rl-50k-final-20260926/`；v2 对应产物在 `%TEMP%/robot-simulator-rl-50k-reward-v2-20260926/`。两者都是失败对照，不作为通过结果；大型模型和日志不入 Git。

最终复测的场景 SHA-256 为 `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3`，CLI DLL 为 `3ad8a8d06f70a4ddde655842971e1f7af88aa36525de0bb98cf454dd617d949a`，原生 `mujoco.dll` 为 `da487aed0d534fc52b1612a09571f9f2836b8abb9f46567ab5868620ca0e7418`。官方 seed 42 回放头的 MuJoCo 模型哈希为 `1ad75271868e3068230ab5f154218f2f3bf3b028150ab2504a83823ae0a75308`。训练 v1/v2 的 CLI 与策略模型哈希各以其 `run-config.json` 为准；比较两版奖励时不能混用策略模型。

## AC 状态与剩余工作

- **AC2：通过。** 11 维 Gym API、固定 seed/100 动作复现、无阶段结果、模型身份变化、20/100 建模样本与性能门槛有证据；101 次 reset、错误恢复、重复 close 的进程句柄/私有内存审计无持续增长。原生分配器逐对象计数未做，结论以进程级审计及代码所有权为限。
- **AC3：通过。** 干净 Python 3.12 虚拟环境安装锁定依赖并通过 `pip check`；默认 500,000 步请求完成 501,760 实际步，728 行 UTF-8 逐集日志和模型/版本/seed/哈希元数据可读；评测重新加载模型并输出逐 seed 事件与回报。训练池与开发集、最终留出集不相交。
- **AC4：未通过。** 冻结 51,200 步模型在首次 4001–4010 最终留出集上目标得分 1、FSM 0，但掉台 8 > 7；保留所有失败 seed 和归因歧义。不改裁判、物理或最终集，也不依据最终集更换模型。
- **AC5：通过。** 完整 Sim.Tests、六份 legacy 回放和新 MuJoCo 回放均通过；策略评测只通过独立 `evaluate.py` 命令运行，默认 FSM 未替换。
- 编码修复后再次运行 `dotnet test RobotSimulator.sln -m:1 --no-restore`：382/382；六份 `replays/*.json` 逐位 PASS，临时 MuJoCo seed 42 回放 8:3、117/117 逐位 PASS。

不得将本试点模型接入默认 FSM，不得据此晋升 `fidelity.json`。若继续优化，应先分析开发集及训练轨迹中的掉台模式，在独立后续任务中预注册新的最终 seed；4001–4010 已被揭示，不再用于新模型的盲验。

## 后续设计决策

本轮在追加 500,000 步训练前审阅了观测与奖励的可学习性。旧 9 维观测没有我方绝对 x/y 或到台沿距离，只有 OnPlatform 布尔值；这可能让策略难以区分“目标相对方向相同但车已靠近台沿”的状态。绝对块坐标按平台边长相除再裁剪，东/北侧部分坐标会饱和为 1。这些是待验证假设，不能写成已证实根因。已在旧 9 项后追加我方相对平台中心 x/y（半边长归一化），形成 11 维观测，并同步 Python 契约；旧模型输入形状不兼容，v1/v2 对照结果保留。3001–3010 已被用于调试，故改称开发验证集；4001–4010 已按预注册方案首次盲验且失败，不能再用于下一模型的盲验。旧版本约 128 PPO steps/s 是历史测速，新干净环境完整训练实测 501,760/866.584≈579 steps/s，仍只代表本机本轮条件。

曾试验 `batch_size=256,n_epochs=4` 的单次 2048 步测速，约 24 秒，未比默认参数快；已撤回这组临时参数，正式训练脚本仍使用并记录 SB3 默认 PPO 参数。
