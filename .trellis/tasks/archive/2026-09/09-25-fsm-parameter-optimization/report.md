# 报告:FSM 参数自动寻优(2026-09-25)

## 结论

Optuna TPE 150 trials(60 s 筛选,seeds 1-4)完成;holdout(seeds 5-8 × 120 s)
上最优参数**不劣于默认,四个 holdout 种子的汇总目标值改善 26**(−58 → −32;掉台 19→13、恢复超限
4→2),seed 6 出现 11:3 胜局;60 s smoke **5:3**。产出
`scenarios/wushu-ring-2026-mujoco-tuned.json`。全套 373/373、官方场景与六份旧
回放逐位不变、`git diff --check` 干净。AC1-AC5 全部达成。

## 最优参数(150 trials, TPE seed 42)

| 参数 | 默认 | 最优 | 重要性 |
|---|---|---|---|
| MOUNT_SPEED | 780 | **710** | **0.806**(主导) |
| FALL_THRESHOLD | 150 | 270 | 0.074 |
| RECOVER_LIMIT | 3 | 5 | 0.069 |
| IR_TRIGGER | 0.35 | 0.196 | 0.028 |
| EDGE_THRESHOLD | 350 | 700 | 0.023 |

关键发现:**MOUNT_SPEED 为主导维度(Optuna 重要性 0.806),710 ≈ 0.53 m/s 的慢速倒车登台在 150-trial 筛选中一致优于 780**——
与登台修复的物理分析一致(快 approach 在台沿产生冲击弹跳);放宽 FALL_THRESHOLD
到 270 让爬沿信号更鲁棒;提高 RECOVER_LIMIT 到 5 减少提前停车。

## holdout 对比(seeds 5-8 × 120 s)

| 指标 | 默认 | tuned |
|---|---|---|
| 目标值 | −58 | **−32** |
| 我方掉台 | 19 | **13** |
| 恢复超限结束 | 4/4 | **2/4** |
| 我方 BlockScore | 1 | 1 |
| 比分 | 多为败局 | seed6 11:3 胜局 |

## 诚实边界

- 对称参数(双方同参)——优化的是共享 FSM 的绝对能力,非"打赢特定对手"。
- 仿真自博弈工程寻优;参数未标定,不宣称真机保真;fidelity.json 未晋升。
- 掉台总数仍高(13 次/4 场):SEARCH/SCORE 的行驶避边是行为层问题,参数只能
  缓解——行为层改进是后续任务(与 09-25-mujoco-search-targeting 的剩余边界同源)。
- 训练/holdout 种子分离已防确定性种子过拟合;更多种子与更长赛程可进一步验证。

## AC 证据索引(复现命令与产物路径)

| AC | 证据 | 复现命令 |
|---|---|---|
| AC1 | 逐 trial:`study/trial-00xx/scenario.json`(150 份)+ `study/trials.csv`(逐 trial 参数/目标值/指标)+ `study/optimize-log.txt`(Optuna 完整日志) | `python tools/param-optimization/optimize.py --n-trials 150 --out-dir tools/param-optimization/study`(sqlite 续跑) |
| AC2 | `study/baseline-60s.json`(训练/holdout 基线聚合)、`study/importances.txt`(参数重要性)、本报告 §holdout 对比表 | 同上;基线 = 空 parameters 覆盖 |
| AC3 | `evidence/holdout-120s-per-seed.json`(tuned vs default 逐 seed:BlockScore/Drop/mount_t/doneReason/final_score) | `oracle.write_scenario + run_match`,seeds 5-8 × 120 s(脚本见报告 §holdout) |
| AC4 | `scenarios/wushu-ring-2026-mujoco-tuned.json`;`evidence/official-120s-tuned.txt`(5:3 跑满 2400 ticks);官方场景 `scenarios/wushu-ring-2026-mujoco.json` **零改动** | `match --seed 42 --scenario scenarios/wushu-ring-2026-mujoco-tuned.json --duration 120` |
| AC5 | `evidence/full-test.log`(373/373)、`evidence/replays-check.txt`(6/6 逐位)、`git diff --check` 干净 | `dotnet test RobotSimulator.sln -m:1`;`dotnet exec Sim.Cli.dll replay-check <replay>` |

构建标识:HEAD = 提交时的 `feat/mujoco-dual-physics-validation` 分支头(修复与工具提交之后);
工具:.NET SDK 8.0.425(临时目录)、Python 3.12.10 + optuna 5.0.0。

## 产物

- `scenarios/wushu-ring-2026-mujoco-tuned.json` — tuned 参数场景
- `tools/param-optimization/` — oracle/objective/optimize + requirements + selftest
- `tools/param-optimization/study/` — trials.csv、基线/holdout JSON、importances、
  optimize-log(study.db 为本地续跑存储,不入库)
