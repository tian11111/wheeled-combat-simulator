# SCORE_BLOCK PPO 下一轮实施与验收记录（checkpoint 选模 + 新留出集盲验）

任务：`09-26-score-block-ppo-checkpoint-round`（分支 `test/score-block-ppo-checkpoint-round`）。
日期：2026-09-26。机器：Windows 11 x64，Python 3.12.10，.NET SDK 8.0.425（临时 SDK），MuJoCo 官方场景 `scenarios/wushu-ring-2026-mujoco.json`。

## 当前结论

- **代码与纪律目标达成**：`train.py` 有了 SB3 `progress.csv` 诊断与每 51,200 单环境 step 的 checkpoint；`evaluate.py` 有了显式 split v2 路由、互斥/重叠拒绝、逐 checkpoint 选模、冻结记录与单次盲验守卫。定向自测 33/33 通过。
- **开发集（5001–5020）选模成功**：10 个模型里 2 个合格，按“目标得分多 → 掉台少 → 步数多”排序选出唯一候选 `rl_model_307200_steps.zip`（4 次锁定目标真实 `BlockScore`，我方 `Drop` 11；FSM 基线 2 / 14）。
- **新盲验集（6001–6050）只运行一次，门槛失败**：候选锁定目标真实 `BlockScore` 5 次、我方 `Drop` 28；FSM 为 8 次、21 次。两项计数条件都不满足，`new_round_blind_gate_passed=false`。**如实判失败。**
- **上一轮 AC4 结论未改**：4001–4010 仍是已揭示的失败留出集，本任务任何结果都不追认为该 AC4 通过（评测输出里 `ac4_claim_eligible=false` 是常量）。

## 本轮改动

| 文件 | 改动 |
|---|---|
| `controllers/score_block_rl/splits.py`（新增） | split v2 唯一来源：`legacy_development`(3001–3010)、`legacy_final_holdout`(4001–4010)、`development_v2`(5001–5020)、`final_holdout_v2`(6001–6050)、`exploratory`；训练池与历史 seed 域；`resolve_selection()` 路由 + 混用/重叠拒绝 |
| `controllers/score_block_rl/train_artifacts.py`（新增） | `sha256_file`、`package_version`、checkpoint 文件名步数解析、`discover_checkpoints`、步数唯一性不变量、`progress.csv`/`episodes.monitor.csv` 读取、`audit_checkpoints` |
| `controllers/score_block_rl/train.py` | 显式 `Logger`+`CSVOutputFormat` → `progress.csv`；`CheckpointCallback(save_freq=51200)` 并断言 `n_envs==1`；`run-config.json` 增加 `split_version`、checkpoint 注册表（步数+SHA-256）、`checkpoint_audit`、logger 说明、`best_model_selection.uses_eval_callback_mean_reward=false`；保留最终模型与 Monitor CSV |
| `controllers/score_block_rl/evaluate.py` | `--split` 五值路由 + `--final-holdout` 旧语义别名；`--model` 可重复 + `--checkpoints-dir` 扫描；FSM 基准每 seed 一次、逐模型 `deterministic=True` 配对（含 `paired_per_seed` 显式配对表）；`--select-candidate` 门槛与排序键；`--freeze`/`--require-freeze`；`--out` 已存在即拒绝；输出 `split_version`/`paired_by`/`previous_round_ac4_claim_eligible`；无合格模型时退出码 3 |
| `controllers/score_block_rl/gym_env.py` | 新增 `step_fsm()` 与 `MAX_POLICY_TICKS`，把 FSM 基线路径变成公开 API（评测脚本不再碰私有成员）；`step()` 行为不变 |
| `controllers/score_block_rl/selftest.py`（新增） | 定向检查：split 路由/互斥矩阵、checkpoint 步数与哈希、`run-config.json` 与磁盘一致性、CSV 可读性、最终盲验守卫、可选 `--gym-check` |
| `controllers/score_block_rl/README.md` | 新 split、checkpoint/诊断、选模与单次盲验命令 |
| `.trellis/spec/sim/index.md` | 增补 split v2 与 checkpoint/选模纪律（含“SB3 默认不装 writer”的坑） |

未改动：11 维观测、奖励 v2（`RlEnvCommand.cs` 常量）、官方场景、裁判、物理、默认 FSM、`fidelity.json`、`Sim.Core`。本任务没有 C# 逻辑改动。

## 1. 训练与诊断（AC1）

命令（仓库根目录，产物在 Git 之外）：

```powershell
$py = "$env:TEMP\score-block-rl-venv-11d\Scripts\python.exe"
$out = "$env:TEMP\score-block-rl-v2-500k-20260926"
& $py -X utf8 controllers/score_block_rl/train.py --steps 500000 `
      --dotnet "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe" --out $out
```

| 项 | 值 |
|---|---|
| 请求 / 实际步数 | 500,000 / **501,760**（SB3 rollout 补齐） |
| 墙钟 / 吞吐 | 704.298 s / **712.4 steps/s**（`time/fps` 逐 rollout 623–793，末值 714） |
| 逐集日志 | `episodes.monitor.csv` 728 行 |
| PPO 诊断 | `progress.csv` 245 行 × 17 列 |
| checkpoint | 9 个：`rl_model_51200…460800_steps.zip`（每 51,200 单环境 step） |
| 最终模型 | `ppo_score_block.zip`，SHA-256 `10e91af9d13b04647b02f9b00668738dd1ba58142da53d5d3fa86c4f13f21dc3` |
| 场景 / CLI DLL | `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3` / `b3d4fd0142e6dc4c20648a42dc339b8f27ca2d1e324c4b67e49b6c087b9dfadf` |
| 依赖 | numpy 2.5.0、gymnasium 1.3.0、stable-baselines3 2.9.0、torch 2.13.0（CPU） |
| `checkpoint_audit` | `ok=true`，注册表 9/9 与磁盘步数、SHA-256 一致 |

训练期 PPO 诊断（`progress.csv`，仅用于解释训练，不是得分证据）：

| 指标 | min | max | 末值 |
|---|---|---|---|
| `train/approx_kl` | 0.00295 | 0.01414 | 0.00708 |
| `train/clip_fraction` | 0.0224 | 0.1348 | 0.0816 |
| `train/explained_variance` | −1.434 | 0.955 | 0.160 |
| `train/value_loss` | 1.48e−5 | 0.0390 | 0.00328 |
| `train/entropy_loss` | −2.838 | −2.154 | −2.253 |
| `rollout/ep_rew_mean` | −1.003 | −0.398 | −0.475 |
| `rollout/ep_len_mean` | 27.5 | 1056.2 | 1005.7 |

逐集（训练 seed 池 42、1000–1999）：728 集中 34 集出现我方 `BlockScore`（合计 34）、**396 集出现我方 `Drop`（合计 396，54.4%）**、`no_score_block` 0 集。`ep_len_mean` 从 27 涨到约 1006，说明策略从“开局很快掉台”变成了“能撑到时限”，但掉台绝对数并未随训练步数单调下降；`explained_variance` 末值仅 0.16，价值函数拟合一般。这些只解释训练趋势，选模不看回报。

## 2. 开发集选模（AC2 + AC3）

命令：

```powershell
& $py -X utf8 controllers/score_block_rl/evaluate.py --split development_v2 `
      --checkpoints-dir "$out\checkpoints" `
      --select-candidate --freeze "$out\candidate-freeze.json" `
      --dotnet "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe" `
      --out "$out\dev-v2-sweep.json"
```

同 seed、同首次 `SCORE_BLOCK` 入口的 FSM 基线（5001–5020，20 集，模型无关路径故只跑一次并在输出标注）：**2 次锁定目标真实 `BlockScore`**（seed 5014、5019）、**14 次我方 `Drop`**、0 `no_score_block`、0 fault、0 归因歧义。

| 模型 | 步数 | 目标得分 | 我方 Drop | 三项门槛 | 备注 |
|---|---|---|---|---|---|
| `rl_model_51200` | 51,200 | 0 | 18 | 失败 | 无得分 |
| `rl_model_102400` | 102,400 | 3 | 16 | 失败 | 得分够，掉台 16 > 14 |
| `rl_model_153600` | 153,600 | 0 | 16 | 失败 | 无得分 |
| `rl_model_204800` | 204,800 | 1 | 15 | 失败 | 得分 1 < 2；掉台超 |
| `rl_model_256000` | 256,000 | 1 | 15 | 失败 | 得分 1 < 2；掉台超 |
| **`rl_model_307200`** | **307,200** | **4** | **11** | **合格** | 4 次得分 seed 5001/5006/5011/5015 全部位姿-事件交叉核验 `true` |
| `rl_model_358400` | 358,400 | 2 | 15 | 失败 | 得分够，掉台 15 > 14 |
| `rl_model_409600` | 409,600 | 2 | 12 | 合格 | 排序第二 |
| `rl_model_460800` | 460,800 | 1 | 10 | 失败 | 得分 1 < 2 |
| `ppo_score_block`（最终） | 501,760 | 0 | 12 | 失败 | 无得分 |

排序键 `(-目标得分, 掉台, -步数)` 下唯一候选为 `rl_model_307200_steps.zip`（`[-4, 11, -307200]` 优于 `[-2, 12, -409600]`）。所有 10 个模型的 `traceability_ok=true`，无归因歧义，fault 全 0，`no_score_block` 0 集。

## 3. 冻结记录（AC4 前半）

`candidate-freeze.json`（写入发生在任何 6001–6050 运行之前）：

```json
{"candidate": {"path": "...\\checkpoints\\rl_model_307200_steps.zip",
               "filename": "rl_model_307200_steps.zip",
               "training_steps": 307200,
               "sha256": "6ed360757633d63553978285e1455c980f66993a3685ae727ef6ddc4ed2bd829"},
 "ranking_key": [-4, 11, -307200],
 "gate": {"has_at_least_one_locked_target_score": true,
          "locked_target_scores_not_below_fsm": true,
          "us_drops_not_above_fsm": true, "traceability_ok": true, "gate_passed": true},
 "split_version": "score-block-split-v2",
 "final_holdout_split": "final_holdout_v2",
 "final_holdout_seeds": [6001, ..., 6050],
 "frozen_before_final_holdout": true}
```

冻结同时记录场景哈希 `52ec978e…`、CLI DLL 哈希 `b3d4fd01…` 与开发集结果摘要。

## 4. 最终盲验（AC4 后半，只运行一次）

命令（`--out` 不存在，未传 `--force`，因此该 split 不可重复写入）：

```powershell
& $py -X utf8 controllers/score_block_rl/evaluate.py --split final_holdout_v2 `
      --model "$out\checkpoints\rl_model_307200_steps.zip" `
      --require-freeze "$out\candidate-freeze.json" `
      --dotnet "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe" `
      --out "$out\final-holdout-v2.json"
```

冻结校验：`split_version_matches`、`final_holdout_split_matches`、`candidate_sha256_matches`、`candidate_training_steps_matches` 全为 `true`；`blind_run_index=1`。

| 指标（50 seeds） | PPO 候选 | FSM 基线 |
|---|---|---|
| 锁定目标我方真实 `BlockScore` 次数 | **5**（seed 6001、6007、6021、6033、6047，交叉核验全 `true`） | **8**（seed 6012、6016、6021、6026、6034、6037、6039、6040） |
| 得分场次 | 5 | 8 |
| 我方 `Drop` | **28** | **21** |
| `no_score_block` | 5（6006、6018、6036、6038、6046） | 5（同 5 个 seed） |
| 锁定目标无归属出界 `BlockOff` | 5（6022、6037、6040、6043、6048） | 0 |
| 任意块无归属 `BlockOff` | 10 | 1 |
| controller fault | 0 | 0 |
| 归因歧义 | 0 | 0 |
| 比分总和（非门槛） | 123 : 77 | 66 : 67 |

门槛：①至少一次真实得分 ✅；②得分总数 5 ≥ FSM 8 ❌；③我方 `Drop` 28 ≤ 21 ❌ → `new_round_blind_gate_passed=false`，**本轮盲验失败**。

**失败原因（只列可核对的事实，不做事后调参）**：

1. **掉台是主要缺口**：候选 28 次 vs 基线 21 次，净多 7 次；逐 seed 配对里 15 个 seed 是“策略掉、FSM 不掉”，另有 6 个 seed 反向，总体净差与汇总一致。
2. **锁定目标得分不足**：5 vs 8。候选在 5 个 seed 上确有可追溯得分，但基线在另外 6 个 seed（6012、6016、6026、6034、6037、6039、6040 中除去 6021 的部分）拿到分而策略没有。
3. **开发集优势没有迁移**：开发集 FSM 只有 2 次得分、候选 4 次；盲验集 FSM 有 8 次得分。也就是说基线的强弱本身随 seed 集变化，开发集上“超过基线”不代表新集上仍超过。这是设计时就接受的盲验风险，不是数据泄漏。
4. **推块与计分脱节**：候选另有 5 个 seed 锁定目标以“无有效得分归属”的 `BlockOff` 出界（共 10 次任意块无归属 `BlockOff`，FSM 仅 1 次）。策略能把块弄出台，但相当一部分出口裁判不认我方得分。该观察与“得分 5 次”并不矛盾，也没有被计入成功。
5. **5 个 `no_score_block` seed 两边完全相同**，说明它们是场景/对手侧性质（阶段前比赛结束），不是策略差异。
6. **比分不能替代指标**：候选比分总和 123:77 明显高于 FSM 的 66:67，但它的掉台更多、锁定目标得分更少。按预注册规则，比分与回报都不进入门槛，报告中单独列出即为此。

未做也不该做的事：在已知 6001–6050 之后回头换更“耐掉台”的 `rl_model_409600`（它在开发集排第二）重跑最终集；用最终集放宽门槛；把 4001–4010 改称新模型盲验；用 `--force` 重跑同一 split。6001–6050 现已揭示，后续模型不能再用它作盲验。

## 5. 定向验证与回归（AC5）

`selftest.py`（33/33 通过，`skipped` 0）：

```powershell
& $py -X utf8 controllers/score_block_rl/selftest.py --train-dir $out `
      --dev-sweep "$out\dev-v2-sweep.json" --freeze "$out\candidate-freeze.json" `
      --gym-check --dotnet "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe" `
      --out "$out\selftest-full.json"
```

| 检查 | 结果 |
|---|---|
| split 路由：4 个命名 split 的 seed 列表、默认 `development_v2`、`--final-holdout` → 4001–4010、盲验标签、探索标签 | 6/6 通过 |
| 互斥/重叠矩阵：混用 split、混用 seeds、复用命名 split seed（新/旧开发、旧留出）、命中训练池、过短、重复、未知 split | 11/11 通过 |
| checkpoint：`rl_model_<steps>_steps.zip` 步数解析、发现+SHA-256、步数唯一性不变量、冻结训练默认（51200 / seed 20260925） | 4/4 通过 |
| 产物：`run-config.json` 注册表与磁盘一致（9 个）、split 版本与间隔一致、`progress.csv` 245 行可解析（含 `train/approx_kl` 等）、`episodes.monitor.csv` 728 行含全部 info 列、`checkpoint_audit.ok` | 5/5 通过 |
| CLI 守卫：盲验缺冻结被拒、`--require-freeze` split 范围、split 混用被拒、自定义 seed 复用命名 split 被拒、已存在结果不被覆盖 | 5/5 通过 |
| Gymnasium：`check_env` 通过（`observation_space=(11,)`）、seed 42 两次 reset + 100 固定动作 **101 帧逐位一致** | 2/2 通过 |
| 选模一致性：候选三项门槛 + 可追溯性 + 排序头 + 冻结 SHA-256 与文件一致 | 1/1 通过 |

回归：

| 命令 | 结果 |
|---|---|
| `dotnet build RobotSimulator.sln -m:1` | 成功，0 错误（6 个既有警告） |
| `dotnet test RobotSimulator.sln -m:1 --no-restore` | **382/382 通过**，0 失败，4 s |
| `Sim.Cli replay-check replays/*.json`（6 份 legacy） | 全部 `PASS: bit-for-bit`：752/752、752/752、278/278、38/38、752/752、752/752 |
| `Sim.Cli replay-record --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json` + `replay-check` | 8:3、117 events、`117/117` `PASS`；回放文件 SHA-256 `CAD2B240184714014494DA41D0D935F8F5E14EF9E6EC02CF0BC315C0B7707D8E` |
| `python -m py_compile`（6 个脚本） | 通过 |

## 6. 吞吐

| 阶段 | 规模 | 墙钟 | 速率 |
|---|---|---|---|
| 训练（单环境 PPO 默认参数） | 501,760 步 | 704.298 s | 712.4 steps/s（SB3 自报） |
| 开发集 sweep | 220 集（20 FSM + 10×20 策略） | 约 176 s | ≈1.25 集/s |
| 最终盲验 | 100 集（50 FSM + 50 策略） | 约 80 s | ≈1.24 集/s |

sweep/最终集墙钟由产物文件时间戳差估算（`run-config.json`→`dev-v2-sweep.json`→`final-holdout-v2.json`），非独立计时器；期间同机只跑了轻量验证步骤。这些数字只代表本机本轮条件。

## 7. 限制与诚实边界

- **特权状态**：11 维观测含仿真真值块坐标（`observation.privileged_state=true`、`block_coordinates_are_simulator_ground_truth=true`）。本模型不是真机可部署策略，也不是传感器/视觉策略。
- **单个训练 RNG seed**：只训练了 1 次（SB3 seed 与首次 reset seed = 20260925）。结论只覆盖这一次运行；要声称跨随机初始化稳定有效，需要多次独立训练（本轮未做，属 Out of Scope）。
- **失败样本全部保留**：`no_score_block`、掉台、歧义归因、fault 都在 `dev-v2-sweep.json` / `final-holdout-v2.json` 的逐 seed 记录里，没有被剔除。
- **盲验集已消耗**：6001–6050 已揭示且用于一次判定；不得再作为后续模型盲验。4001–4010 与 3001–3010 同样只能作历史对照。
- **未验证项**：奖励/观测再设计、超参搜索、SAC/MJX、跨 seed 稳定性、真机迁移；`fidelity.json` 未晋升。
- **判失败不等于代码失败**：本轮交付的是“按项目裁判指标诚实地判定”的能力，判定结果是候选在 6001–6050 上未达标。

## 8. 与上一轮 AC4 的关系

上一轮 `09-25-mujoco-score-rl-pilot` 的 AC4 在 4001–4010 上仍为 **未通过**（我方 `Drop` 8 vs 7）。本轮：

- 没有重新运行 4001–4010，也没有改写那份报告的结论或修订它的表格；
- 评测输出中 `ac4_claim_eligible` 恒为 `false`，并附说明“4001–4010 的 AC4 已关闭为失败，后续运行不能追认为该 AC4”；
- 本轮盲验结果单独记在 `new_round_blind_gate_passed`，本次为 `false`。

## 9. 可复核命令汇总

```powershell
$py = "$env:TEMP\score-block-rl-venv-11d\Scripts\python.exe"
$dotnet = "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe"
$out = "$env:TEMP\score-block-rl-v2-500k-20260926"

# 训练（本轮已执行，产物在 $out）
& $py -X utf8 controllers/score_block_rl/train.py --steps 500000 --dotnet $dotnet --out $out

# 开发集选模 + 冻结（本轮已执行）
& $py -X utf8 controllers/score_block_rl/evaluate.py --split development_v2 `
    --checkpoints-dir "$out\checkpoints" --select-candidate `
    --freeze "$out\candidate-freeze.json" --dotnet $dotnet --out "$out\dev-v2-sweep.json"

# 最终盲验（本轮已执行一次；再次运行会因 $out 已存在而拒绝）
& $py -X utf8 controllers/score_block_rl/evaluate.py --split final_holdout_v2 `
    --model "$out\checkpoints\rl_model_307200_steps.zip" `
    --require-freeze "$out\candidate-freeze.json" --dotnet $dotnet `
    --out "$out\final-holdout-v2.json"

# 定向验证 + Gymnasium + 产物核对
& $py -X utf8 controllers/score_block_rl/selftest.py --train-dir $out `
    --dev-sweep "$out\dev-v2-sweep.json" --freeze "$out\candidate-freeze.json" `
    --gym-check --dotnet $dotnet --out "$out\selftest-full.json"

# 回归
dotnet test RobotSimulator.sln -m:1 --no-restore
Get-ChildItem replays\*.json | ForEach-Object { & $dotnet src\Sim.Cli\bin\Debug\net8.0\Sim.Cli.dll replay-check $_.FullName }
& $dotnet src\Sim.Cli\bin\Debug\net8.0\Sim.Cli.dll replay-record --seed 42 `
    --scenario scenarios\wushu-ring-2026-mujoco.json --out "$out\replay-seed-42-mujoco.json"
& $dotnet src\Sim.Cli\bin\Debug\net8.0\Sim.Cli.dll replay-check "$out\replay-seed-42-mujoco.json"
```

原始产物：`$out\run-config.json`、`$out\progress.csv`、`$out\episodes.monitor.csv`、`$out\checkpoints\`、`$out\dev-v2-sweep.json`、`$out\candidate-freeze.json`、`$out\final-holdout-v2.json`、`$out\selftest-full.json`、`$out\replay-seed-42-mujoco.json`。

## 10. 验收状态

| AC | 内容 | 状态 | 证据 |
|---|---|---|---|
| AC1 | `progress.csv` + 9 checkpoint + 最终模型 + Monitor CSV + `run-config.json` 步数/哈希一致，未用 `EvalCallback` 选模 | 通过 | 第 1 节、`selftest` 产物检查 5/5 |
| AC2 | split 路由与互斥、`--final-holdout` 旧语义、seed 重叠拒绝 | 通过 | 第 5 节 6/6 + 11/11 + 5/5 |
| AC3 | 全部 checkpoint + 最终模型在 5001–5020 独立环境 `deterministic=True` 配对评测并选出唯一候选 | 通过 | 第 2 节（2 个合格，候选 307200） |
| AC4 | 冻结记录先于最终集；6001–6050 只运行一次；逐 seed 事件与门槛结论如实记录 | 通过（判定结果为**失败**） | 第 3、4 节；`new_round_blind_gate_passed=false` |
| AC5 | Sim.Tests、Gymnasium 检查、回放检查、`git diff --check` | 通过 | 第 5 节（382/382、101 帧一致、6+1 回放 PASS） |
| AC6 | 报告含训练诊断、逐 seed 指标、吞吐、失败原因、特权状态与单 seed 范围，并声明不追认上一轮 AC4 | 通过 | 本文件 |
