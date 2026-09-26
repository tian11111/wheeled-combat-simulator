# SCORE_BLOCK PPO 试点

仅用于官方 MuJoCo 场景的离线试验。11 维观测含仿真真值块坐标（特权状态），模型不能直接部署到真机，也不会替换默认 FSM。旧 9 维观测模型与当前环境不兼容，须重新训练。

## Windows x64 复现

使用 Python 3.12、.NET 8 SDK 和项目锁定的 MuJoCo 原生 DLL。以下命令在仓库根目录运行，训练产物放在 Git 跟踪目录之外：

```powershell
py -3.12 -m venv "$env:TEMP\score-block-rl-venv"
& "$env:TEMP\score-block-rl-venv\Scripts\python.exe" -m pip install -r controllers/score_block_rl/requirements.txt
dotnet build RobotSimulator.sln -m:1
& "$env:TEMP\score-block-rl-venv\Scripts\python.exe" -X utf8 controllers/score_block_rl/train.py --steps 500000 --out "$env:TEMP\score-block-rl-train"
& "$env:TEMP\score-block-rl-venv\Scripts\python.exe" controllers/score_block_rl/evaluate.py --model "$env:TEMP\score-block-rl-train\ppo_score_block.zip" --out "$env:TEMP\score-block-rl-train\dev-evaluation.json"
```

`rl-env` 的 JSONL 输入/输出和逐集 CSV 固定为 UTF-8；Windows 训练命令须带 `-X utf8`，脚本会在编码不符时立即报错。脚本优先查找 PATH 中的 `dotnet`，也可给 `train.py`、`evaluate.py` 和 `benchmark.py` 传 `--dotnet <dotnet.exe 的绝对路径>`。训练 episode seed 池为 42、1000–1999；SB3 自身随机种子为 20260925，首次 reset 也会使用该 seed。3001–3010 是开发验证集，4001–4010 是预注册最终留出集。默认评测使用开发集；模型冻结后显式传 `--final-holdout` 才会运行最终留出集。自定义 `--seeds` 仅生成探索性结果，不能作为 AC4 证据；所有评测均拒绝训练 seed 重叠。

`train.py` 使用锁定 SB3 版本的 PPO 默认参数，并把实际参数、11 维观测定义及 seed split 写入 `run-config.json`。输出还包括模型与 UTF-8 `episodes.monitor.csv`。`evaluate.py` 输出 split 标签、逐 seed 指标及 AC4 门槛。只有锁定目标的我方真实 `BlockScore` 算策略推块成功；最终比分和回报不能代替该裁判事件。未进入阶段的 seed 保留为零成功样本。最终集命令示例：

```powershell
& "$env:TEMP\score-block-rl-venv\Scripts\python.exe" controllers/score_block_rl/evaluate.py --model "$env:TEMP\score-block-rl-train\ppo_score_block.zip" --final-holdout --out "$env:TEMP\score-block-rl-train\final-holdout.json"
```

性能测量：`dotnet test src/Sim.Tests/Sim.Tests.csproj --filter FullyQualifiedName~TrainingResetPerformanceTests` 比较 20 次冷建模与 100 次热建模；设置 `ROBOT_SIM_RL_PERF_OUTPUT` 为绝对 JSON 路径可保存原始样本。`python controllers/score_block_rl/benchmark.py --out <绝对 JSON 路径>` 记录 100 次完整预推进 reset 和 1000 次策略 step 的原始样本及 p50/p95。性能受机器、杀毒软件和原生 DLL 影响，比较时须记录运行环境。
