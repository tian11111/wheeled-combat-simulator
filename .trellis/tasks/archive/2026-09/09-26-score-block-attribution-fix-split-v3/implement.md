# Implement — 归属判定修复与 split v3 预注册

执行顺序固定；每步有验证命令与回退点。

## 0. 前置确认（只读）

```powershell
git status --porcelain        # 记录起始脏文件（.learnings/、.trellis/.template-hashes.json、godot/src/ArenaVisualizer.cs、.dsh/、.zcode/），勿混入提交
$dotnet = "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe"
```

## 1. 修 `Physics.FinalizeBlockContacts`（唯一行为改动）

`src/Sim.Core/Physics.cs`：按 max 接触时刻处的**不同角色数**判定（见 design §1）。

- 验证：`& $dotnet build RobotSimulator.sln -m:1`
- 回退：`git checkout -- src/Sim.Core/Physics.cs`

## 2. 新增 Sim.Core 测试

- 单元测试：design §2.1 的 6 个用例（直接调 `PhysicsWorld.FinalizeBlockContacts`）。
- 端到端测试：design §2.2（MuJoCo 推块出界 → 期望我方 `BlockScore`）。
- 验证：`& $dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 --no-restore --filter "FullyQualifiedName~FinalizeBlockContacts|FullyQualifiedName~BlockAttribution"`
- 回退：删除新增测试文件/用例。

## 3. `replays` 逐位回归（硬门槛）

```powershell
Get-ChildItem replays\*.json | ForEach-Object { & $dotnet src\Sim.Cli\bin\Debug\net8.0\Sim.Cli.dll replay-check $_.FullName }
```

- 判定：**6/6 PASS**。若 MuJoCo 回放不再逐位一致，**停止**并分析是否属于"归属结论修正"，
  如实记录，不得静默重录。

## 4. split v3 预注册

`controllers/score_block_rl/splits.py`：见 design §3（版本、两个新 split、`BLIND_SPLITS`、
`REVEALED_HOLDOUT_SPLITS`、`HISTORICAL_SPLIT_SEEDS`/`REVEALED_SEEDS`、`SPLIT_USAGE`、默认 split）。

- 验证：`& $py -c "import splits; splits.assert_registry_is_disjoint(); print(splits.SPLIT_VERSION)"`
- 回退：`git checkout -- controllers/score_block_rl/splits.py`

## 5. 评测入口作用域迁移 + 已揭示集守卫

`controllers/score_block_rl/evaluate.py`、`controllers/score_block_rl/train.py`：见 design §4/§5。

- 验证：`& $py controllers/score_block_rl/evaluate.py --help`；并手动确认
  `--split final_holdout_v2` 无 `--analysis-only` 被拒、有则输出 `gate_evidence_eligible=false`。
- 回退：`git checkout -- controllers/score_block_rl/evaluate.py controllers/score_block_rl/train.py`

## 6. `selftest.py` 期望值更新到 v3

见 design §5。注意探索性自定义 seed 示例必须避开新注册号段；"unknown split" 用例改用 `development_v4`。

- 验证：`& $py controllers/score_block_rl/selftest.py`
- 回退：`git checkout -- controllers/score_block_rl/selftest.py`

## 7. 修复真实增量验证（已揭示集，仅分析）

用两个冻结模型在已揭示集上重放，**必须带 `--analysis-only`**：

```powershell
& $py -X utf8 controllers/score_block_rl/evaluate.py --split final_holdout_v2 --analysis-only `
    --model "$env:TEMP\score-block-rl-v2-500k-20260926\checkpoints\rl_model_307200_steps.zip" `
    --dotnet $dotnet --out "$env:TEMP\score-block-attribution-fix\final-holdout-v2-after-fix.json"
& $py -X utf8 controllers/score_block_rl/evaluate.py --split legacy_final_holdout --analysis-only `
    --model "$env:TEMP\robot-simulator-rl-11d-50k-clean-20260926\ppo_score_block.zip" `
    --dotnet $dotnet --out "$env:TEMP\score-block-attribution-fix\final-holdout-after-fix.json"
```

- 判定（与诊断投影逐项对照，差异必须解释）：
  - 6001–6050 策略 `locked_target_us_block_scores` 5 → **9**；`unowned_block_offs` 10 → **0**；
  - 6001–6050 FSM 8 → **8**；`unowned_block_offs` 1 → **0**；
  - 4001–4010 策略 1 → **1**；4001–4010 FSM 0 → **1**；
  - `us_drops`、`policy_ticks`、`total_reward` **与归档值逐个不变**。
- 回退：`%TEMP%` 产物，不入库。

## 8. README 与报告

- `controllers/score_block_rl/README.md`：split 表加 v3、v2 标注已揭示、补 `--analysis-only`、
  补归属修复说明。
- `.trellis/tasks/09-26-score-block-attribution-fix-split-v3/report.md` + `evidence/`：
  AC1–AC8 逐项证据、修复前后对照表、回归结果、新 split 表、限制与诚实边界。

## 9. 收尾

- 全量回归：`& $dotnet build RobotSimulator.sln -m:1`；`& $dotnet test RobotSimulator.sln -m:1 --no-restore`；
  `replay-check` × 6；`selftest.py`。
- 提交（工作提交 → 归档 → journal 顺序），只提交本任务文件。

## 回退点汇总

| 步骤 | 回退 |
|---|---|
| 1 | `git checkout -- src/Sim.Core/Physics.cs` |
| 2 | 删除新增测试文件/用例 |
| 4 | `git checkout -- controllers/score_block_rl/splits.py` |
| 5 | `git checkout -- controllers/score_block_rl/evaluate.py controllers/score_block_rl/train.py` |
| 6 | `git checkout -- controllers/score_block_rl/selftest.py` |
| 7/8 | 删除 `%TEMP%\score-block-attribution-fix`（不入库） |

## 禁止事项（违反即任务失败）

- 除 `FinalizeBlockContacts` 之外再改任何 `Sim.Core` 物理/裁判/计分语义。
- 启动训练、跑新一轮盲验门槛判定、宣布门槛通过。
- 放宽/改评测门槛、改奖励或观测常量、改默认 FSM、触碰 `fidelity.json`。
- 把已揭示 seed 集（3001–3010、4001–4010、5001–5020、6001–6050）当作盲验证据。
- 为了让端到端测试通过而放宽归属判定或删除断言。
