# 实施清单：SCORE_BLOCK PPO 下一轮

> 逐项记录可复核命令与原始结果。任何“通过”都必须有可重跑的命令和输出文件；回报不能代替裁判事件。

## 0. 准备

1. [x] `git status --porcelain` 快照；确认并保留工作区已有无关改动（`.learnings/*`、`.trellis/.template-hashes.json`、`godot/src/ArenaVisualizer.cs`、未跟踪的 `.dsh/`、`.zcode/`），以及上一轮 `report.md`/`references.md` 未提交的“下一轮规划”章节。
2. [x] 新建分支 `test/score-block-ppo-checkpoint-round`（若已在该分支则跳过）。
3. [x] 复用锁定依赖的虚拟环境 `%TEMP%\score-block-rl-venv-11d`（Python 3.12.10 / SB3 2.9.0 / Gymnasium 1.3.0 / Torch 2.13.0+cpu / NumPy 2.5.0，`pip check` 通过）。
4. [x] `dotnet build RobotSimulator.sln -m:1` 产出 `src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll` 供 `rl-env` 使用。

## 1. 代码实现（写入范围：`controllers/score_block_rl/`）

5. [x] 新增 `splits.py`：split v2 注册表（`legacy_development`/`legacy_final_holdout`/`development_v2`/`final_holdout_v2`/`exploratory`）、`SPLIT_VERSION`、训练与历史 seed 域、`resolve_selection()` 路由与互斥校验（`SplitError`）。
6. [x] 新增 `train_artifacts.py`：`sha256_file`、`package_version`、`parse_checkpoint_steps`、`discover_checkpoints`、`read_progress_csv`、`read_monitor_csv`。
7. [x] 改 `train.py`：保持单环境/默认 PPO/11 维/奖励 v2 不变；加 `CSVLogger` → `progress.csv`；加 `CheckpointCallback(save_freq=51200, save_path=<out>/checkpoints)` 并断言 `n_envs==1`；`run-config.json` 增加 `split_version`、checkpoint 注册表（步数+SHA-256）、`checkpoint_audit`、logger 说明、显式“未使用 EvalCallback 选模”。
8. [x] 改 `evaluate.py`：`--split` 五值路由 + `--final-holdout` 旧语义别名；`--model` 可重复 + `--checkpoints-dir`；FSM 基准一次、逐模型 `deterministic=True` 配对；`--select-candidate` 门槛与排序键；`--freeze/--require-freeze`；`--out` 存在即拒绝覆盖（除 `--force`）；输出 `split_version`、`paired_by`、`previous_round_ac4_claim_eligible=false`、`new_round_blind_gate_passed`。
9. [x] 新增 `selftest.py`：split 路由/互斥矩阵、checkpoint 步数解析与哈希核对、`progress.csv`/`episodes.monitor.csv` 可读性、最终单次盲验守卫。产物相关检查用 `--train-dir` 传入。

## 2. 定向验证 + 构建

10. [x] `python -m py_compile` 三个脚本；`python selftest.py`（纯逻辑部分）全绿。
11. [x] `dotnet build RobotSimulator.sln -m:1` 成功；记录 `Sim.Cli.dll` SHA-256。
12. [x] Gymnasium `check_env` + seed 42 两次 reset/固定 100 动作逐帧一致。

## 3. 训练（默认参数，单环境，seed 20260925）

13. [x] 后台运行：`<venv>\python.exe -X utf8 controllers/score_block_rl/train.py --steps 500000 --out "%TEMP%\score-block-rl-v2-<date>"`，不中断；记录吞吐、`progress.csv` 行数、checkpoint 数量。
14. [x] 训练后核对 `run-config.json`：`split_version`、每个 checkpoint 步数/SHA-256、`checkpoint_audit` 全部一致；`selftest.py --train-dir <out>` 通过。

## 4. 开发集选模（`development_v2` 5001–5020）

15. [x] 运行 sweep：`evaluate.py --split development_v2 --checkpoints-dir <out>/checkpoints --out <out>/dev-v2-sweep.json --select-candidate --freeze <out>/candidate-freeze.json`。
16. [x] 若 `candidate` 为 `null`：写失败结论并**停止**，不运行最终集（跳到第 8 步的回归与报告）。
17. [x] 若选出候选：核对冻结记录含路径/步数/SHA-256/场景/CLI 哈希与该模型的 dev 结果摘要。

## 5. 最终单次盲验（`final_holdout_v2` 6001–6050）

18. [x] 只运行一次：`evaluate.py --split final_holdout_v2 --model <candidate.zip> --require-freeze <out>/candidate-freeze.json --out <out>/final-holdout-v2.json`；不传 `--force`。
19. [x] 保留全部失败/歧义/`no_score_block` seed；如实记录门槛结论，不依据最终集换模型。

## 6. 回归

20. [x] `dotnet test RobotSimulator.sln -m:1 --no-restore`。
21. [x] 六份 `replays/*.json` `replay-check`；MuJoCo 官方 seed 42 新录制回放。
22. [x] `git diff --check` 干净。

## 7. 交付物

23. [x] 本任务 `report.md`：训练诊断、逐 seed 配对指标、吞吐、失败原因、单 seed 结论范围、特权状态限制、明确“不追认上一轮 AC4”。
24. [x] `controllers/score_block_rl/README.md` 更新新 split/checkpoint/选模/盲验命令。
25. [x] `.trellis/spec/sim/index.md` 增补 split v2 与 checkpoint 纪律（若判定无需更新则写明理由）。

## 8. 收尾

26. [ ] 按 Phase 3.4 先向用户展示提交计划，确认后再提交；不 amend、不 push。
