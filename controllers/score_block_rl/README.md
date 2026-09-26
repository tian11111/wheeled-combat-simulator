# SCORE_BLOCK PPO 试点

仅用于官方 MuJoCo 场景的离线试验。11 维观测含仿真真值块坐标（特权状态），模型不能直接部署到真机，也不会替换默认 FSM。旧 9 维观测模型与当前环境不兼容，须重新训练。

本页描述的是 **split v2 / checkpoint 轮**（任务 `09-26-score-block-ppo-checkpoint-round`）的训练与评测入口。上一轮（4001–4010）的 AC4 已判定失败，其结论不因任何后续结果改变。

## Windows x64 复现

使用 Python 3.12、.NET 8 SDK 和项目锁定的 MuJoCo 原生 DLL。以下命令在仓库根目录运行，训练产物放在 Git 跟踪目录之外：

```powershell
py -3.12 -m venv "$env:TEMP\score-block-rl-venv"
& "$env:TEMP\score-block-rl-venv\Scripts\python.exe" -m pip install -r controllers/score_block_rl/requirements.txt
dotnet build RobotSimulator.sln -m:1
& "$env:TEMP\score-block-rl-venv\Scripts\python.exe" -X utf8 controllers/score_block_rl/train.py --steps 500000 --out "$env:TEMP\score-block-rl-train"
```

`rl-env` 的 JSONL 输入/输出和逐集 CSV 固定为 UTF-8；Windows 训练命令须带 `-X utf8`，脚本会在编码不符时立即报错。脚本优先查找 PATH 中的 `dotnet`，也可给 `train.py`、`evaluate.py` 和 `benchmark.py` 传 `--dotnet <dotnet.exe 的绝对路径>`。

## 数据划分（split v2）

`controllers/score_block_rl/splits.py` 是划分的唯一来源，`train.py`、`evaluate.py`、`selftest.py` 共用，避免三处各写一份 seed 列表。

| split | seeds | 用途 |
|---|---|---|
| `legacy_development` | 3001–3010 | 历史开发集（已揭示），仅对照 |
| `legacy_final_holdout` | 4001–4010 | 历史最终留出集（已揭示，AC4 失败）；`--final-holdout` **只**表示它 |
| `development_v2` | 5001–5020 | 本轮选模开发集（默认 split） |
| `final_holdout_v2` | 6001–6050 | 本轮唯一盲验集，需冻结记录 |
| `exploratory` | 自定义 `--seeds` | 探索，永不作门槛证据 |

训练 episode 池仍为 42、1000–1999，SB3 RNG 与首次 reset seed 为 20260925；所有命名 split 与训练池、历史 split 互斥。评测入口拒绝 split 混用（`--final-holdout` + `--split`、`--split` + `--seeds`）与任何 seed 重叠，并拒绝覆盖已存在的结果文件（除非 `--force`）。

## 训练诊断与 checkpoint

`train.py` 使用锁定 SB3 版本的 PPO 默认参数和单环境，并把实际参数、11 维观测定义、split 版本与 seed 池写入 `run-config.json`。此外：

- `progress.csv`：显式 `CSVLogger`（SB3 在 `verbose=0` 且无 `tensorboard_log` 时**不会**默认写 CSV），保留 `train/approx_kl`、`train/clip_fraction`、`train/explained_variance`、`train/value_loss` 等优化诊断；
- `episodes.monitor.csv`：Monitor 逐集裁判信息（`INFO_LOG_FIELDS`）；
- `checkpoints/rl_model_<steps>_steps.zip`：每 51,200 个单环境 step 一个 `CheckpointCallback` 快照；
- `ppo_score_block.zip`：最终模型；
- `run-config.json`：`split_version`、每个 checkpoint 的训练步数与 SHA-256、`checkpoint_audit`、场景/CLI/依赖版本与哈希。

`CheckpointCallback.save_freq` 的计数单位是 `env.step()` 调用次数；本任务固定 `n_envs=1`，因此 51,200 直接等于 51,200 个单环境 step（不做 `// n_envs`，`train.py` 会断言单环境）。**不使用** `EvalCallback` 的平均回报选模，选模只看裁判事件。

## 评测与选模

默认 split 现在是 `development_v2`；旧 3001–3010 用 `--split legacy_development`。

```powershell
# 1) 逐个 checkpoint + 最终模型在开发集上评测，选出并冻结唯一候选
& $py -X utf8 controllers/score_block_rl/evaluate.py `
    --split development_v2 `
    --checkpoints-dir "$env:TEMP\score-block-rl-train\checkpoints" `
    --select-candidate --freeze "$env:TEMP\score-block-rl-train\candidate-freeze.json" `
    --out "$env:TEMP\score-block-rl-train\dev-v2-sweep.json"

# 2) 冻结后只运行一次盲验（缺冻结记录、或不匹配都会被拒绝）
& $py -X utf8 controllers/score_block_rl/evaluate.py `
    --split final_holdout_v2 `
    --model "$env:TEMP\score-block-rl-train\checkpoints\rl_model_XXXXXX_steps.zip" `
    --require-freeze "$env:TEMP\score-block-rl-train\candidate-freeze.json" `
    --out "$env:TEMP\score-block-rl-train\final-holdout-v2.json"

# 历史对照（已揭示的 4001–4010，不再作为新模型盲验）
& $py -X utf8 controllers/score_block_rl/evaluate.py --model <模型> --final-holdout `
    --out "$env:TEMP\score-block-rl-train\legacy-final-4001-4010.json"
```

- `deterministic=True` 在独立评测环境里逐 seed 运行；同一 seed 的内置 FSM 基线从**同一个首次 `SCORE_BLOCK` 入口**开始配对（FSM 路径不消费策略，故每个 seed 只跑一次即可作为所有模型的共同基准，输出里标注 `fsm_baseline_is_model_independent`）。
- 合格条件只用真实裁判事件：锁定目标我方真实 `BlockScore` ≥ 1、总数不低于 FSM、我方 `Drop` 不高于 FSM，且所有计分 seed 都有位姿/事件交叉核验。合格者按目标得分多 → 掉台少 → 训练步数多选唯一候选；没有合格模型时记录 `stop_reason="no_qualified_model"` 并停止，不打开最终集。
- 输出保留全部 `no_score_block`、掉台、归因歧义与 fault 样本；`ac4_claim_eligible` 恒为 `false`（上一轮 AC4 已关闭），新盲验结果记在 `new_round_blind_gate_passed`，不能追认为上一轮 AC4。
- 最终比分和 episode 回报只作参考，不能替代锁定目标 `BlockScore`。

## 失败诊断（`diagnose.py`）

只诊断、不修复：`diagnose.py` 消费 `rl-env` 的**可选、显式开启**逐 tick 轨迹，对已揭示的 seed 集
（仅作分析输入，永不作为门槛证据）分类掉台与块出界归属。默认关闭轨迹时 `rl-env` 的响应与之前逐位一致。

```powershell
$py = "$env:TEMP\score-block-rl-venv-11d\Scripts\python.exe"
$out = "$env:TEMP\score-block-rl-diagnose"
$model = "$env:TEMP\score-block-rl-v2-500k-20260926\checkpoints\rl_model_307200_steps.zip"

# 采集：逐 tick 轨迹落到 <out>/traces/<tag>-<mode>-<seed>.jsonl.gz（大体积，不入 Git）
& $py -X utf8 controllers/score_block_rl/diagnose.py collect --mode both `
    --split final_holdout_v2 --model $model --dotnet <dotnet.exe> --out $out --force

# 分析：逐 episode 分类 + 与既有评测结果逐 seed 复现核对（不一致会告警）
& $py -X utf8 controllers/score_block_rl/diagnose.py analyze --out $out --tag final_holdout_v2 `
    --compare-recorded <final-holdout-v2.json>

# 回归：证明轨迹开关不改变默认路径的观测/奖励/info
& $py -X utf8 controllers/score_block_rl/diagnose.py default-off-check --seed 6001 --ticks 300 `
    --dotnet <dotnet.exe> --out $out

# 把决定性原始轨迹摘录成 markdown
& $py -X utf8 controllers/score_block_rl/diagnose.py excerpts --out $out
```

结论（任务 `09-26-score-block-failure-diagnosis`）：归属缺陷是 `Physics.FinalizeBlockContacts`
用**接触记录条数**而非**不同角色数**判定 `"simultaneous"`，导致单台机器人多点接触出界不计分
（两轮 14 次 `BlockOff` 全部如此，真双方争抢 0 次）；掉台缺口来自接近台沿时的速度与转角
（速度 > 0.4 m/s 的掉台占 82% vs FSM 14%）。报告见
`.trellis/tasks/09-26-score-block-failure-diagnosis/report.md`。

## 定向验证

```powershell
& $py -X utf8 controllers/score_block_rl/selftest.py `
    --train-dir "$env:TEMP\score-block-rl-train" `
    --dev-sweep "$env:TEMP\score-block-rl-train\dev-v2-sweep.json" `
    --freeze "$env:TEMP\score-block-rl-train\candidate-freeze.json"
```

覆盖 split 路由与互斥矩阵、checkpoint 步数解析与 SHA-256、`run-config.json` 与磁盘一致性、`progress.csv` / `episodes.monitor.csv` 可读性、以及最终盲验守卫（缺冻结、split 混用、重复写入）。不带 `--train-dir` 时产物相关检查标为 `skipped`，不会假装通过。

## 性能测量

`dotnet test src/Sim.Tests/Sim.Tests.csproj --filter FullyQualifiedName~TrainingResetPerformanceTests` 比较 20 次冷建模与 100 次热建模；设置 `ROBOT_SIM_RL_PERF_OUTPUT` 为绝对 JSON 路径可保存原始样本。`python controllers/score_block_rl/benchmark.py --out <绝对 JSON 路径>` 记录 100 次完整预推进 reset 和 1000 次策略 step 的原始样本及 p50/p95。性能受机器、杀毒软件和原生 DLL 影响，比较时须记录运行环境。
