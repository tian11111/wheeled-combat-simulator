# Implement — SCORE_BLOCK 失败原因诊断

执行顺序固定；每步有验证命令与回退点。**不修改 `Sim.Core`。**

## 0. 前置确认（只读）

```powershell
git status --porcelain                     # 记录起始脏文件，勿混入本次提交
python .trellis/scripts/task.py current --source
$dotnet = "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe"   # 本机临时 SDK 8.0.425
```

## 1. `rl-env` 可选 trace（C#）

- `src/Sim.Cli/RlEnvCommand.cs`
  - `EpisodeState` 增 `public bool Trace;`
  - `reset` 解析可选布尔 `trace`；写入 `state.Trace`。
  - 新增 `BuildTrace(engine, state, events)`，`Reset`/`Step` 末尾按 `state.Trace` 追加
    `info["trace"]`（默认不追加任何键）。
- 验证：
  ```powershell
  & $dotnet build RobotSimulator.sln -m:1
  ```
- 回退点：`git checkout -- src/Sim.Cli/RlEnvCommand.cs`

## 2. `gym_env` 可选开关（Python）

- `controllers/score_block_rl/gym_env.py`：`ScoreBlockEnv(..., trace: bool = False)`；
  为真时 `reset` 带 `"trace": true`。
- 验证：`py_compile` + 缺省路径请求体不变（代码审查 + 第 5 步对拍）。

## 3. 诊断脚本 `controllers/score_block_rl/diagnose.py`

- `collect`：逐集跑 trace，落 `<outdir>/traces/<mode>-<seed>.jsonl` + `episode` 汇总行。
- `analyze`：掉台分类 + 块出界归属分类 + 得分对照 → `<outdir>/diagnosis.json`。
- 复用 `splits.py` 的 seed 路由（不接受已揭示集当门槛，只当分析输入）。
- 验证：`py -3.12 -m py_compile controllers/score_block_rl/diagnose.py`

## 4. 采集轨迹

```powershell
$py = "$env:TEMP\score-block-rl-venv-11d\Scripts\python.exe"
$out = "$env:TEMP\score-block-rl-diagnose-20260926"
$model = "$env:TEMP\score-block-rl-v2-500k-20260926\checkpoints\rl_model_307200_steps.zip"

# 第二轮盲验集（已揭示，仅分析）：策略 + FSM 配对
& $py -X utf8 controllers/score_block_rl/diagnose.py collect --mode both --split final_holdout_v2 `
    --model $model --dotnet $dotnet --out $out
# 第一轮留出集（已揭示，跨轮对照）
& $py -X utf8 controllers/score_block_rl/diagnose.py collect --mode both --split legacy_final_holdout `
    --model "$env:TEMP\robot-simulator-rl-11d-50k-clean-20260926\ppo_score_block.zip" --out $out
```

- 验证：每个 split 的 episode 数与 `final-holdout-v2.json` / `final-holdout.json` 的逐 seed
  计数一致（得分、掉台、`no_score_block` 必须逐 seed 对上）。**对不上即停止并排查。**

## 5. 默认路径不回归（对拍）

```powershell
# 用同一 seed 分别以缺省与 trace 跑一次，比较"非 trace 字段"是否一致
& $dotnet test RobotSimulator.sln -m:1 --no-restore
Get-ChildItem replays\*.json | ForEach-Object { & $dotnet src\Sim.Cli\bin\Debug\net8.0\Sim.Cli.dll replay-check $_.FullName }
```

- 判定：`dotnet test` 全绿；6 份 legacy 回放逐位 PASS；缺省 `rl-env` 响应不含 `trace` 键。

## 6. 分析并写报告

- `diagnose.py analyze` → `diagnosis.json`；把聚合结果、逐 seed 清单、代表片段写入
  `.trellis/tasks/09-26-score-block-failure-diagnosis/report.md` 与 `evidence/`。
- 报告必须含：AC1–AC7 逐项证据、失败样本、限制、唯一候选修复及其验证方式、被否候选的反向证据、
  下一轮 seed 划分建议。
- 验证：报告结论中的每个数字都能从 `diagnosis.json` 或 traces 复算。

## 7. 收尾

- `task.py finish` → 提交（工作提交 → 归档 → journal 的顺序）。
- 若证据不足：报告写"证据不足"并列出最小补证实验，**不得**给出无证据的修复建议。

## 回退点汇总

| 步骤 | 回退 |
|---|---|
| 1 | `git checkout -- src/Sim.Cli/RlEnvCommand.cs` |
| 2 | `git checkout -- controllers/score_block_rl/gym_env.py` |
| 3 | 删除 `diagnose.py`（新增文件） |
| 4/6 | 删除 `$out`（`%TEMP%` 产物，不入库） |

## 禁止事项（违反即任务失败）

- 修改 `Sim.Core` 物理/裁判/计分、观测维度、奖励常量、评测门槛。
- 启动训练、用已揭示 seed 集重跑门槛判定、用 `--force` 覆盖既有评测结果。
- 把诊断阶段的任何改动接入默认 FSM 或写入 `fidelity.json`。
