# 执行计划

1. 批 1（R1）：`_check_tick` 跳帧分支推进基准 + docstring；`mbri_adapter_selftest.py` 加两条恢复用例。验证：`py -3.12 controllers/mbri_adapter_selftest.py`。提交 `fix(controllers): 跳帧后重置 tick 基准，避免永久故障`。
2. 批 2（R3）：`MujocoPhysicsBackend` 旋钮实例化（常量 + 实例字段 + 两个构造可选参数）+ `MujocoPhysicsBackendFactory` 可选注入 + `SearchTurnCompensationTests` 去反射、改 factory 注入、`output` 随返回值携带。验证：`dotnet test`（重点 6 个 MuJoCo 类）。提交 `fix(mujoco): 原地转向补偿改实例级注入，消除跨测试类静态污染`。
3. 批 3（R2）：`Main.ReplaceSession` 抽取 + 三处调用统一。验证：构建 Godot 工程 + 无头 parity + `dotnet test`。提交 `fix(godot): 统一会话替换入口并释放旧引擎`。
4. 批 4（R4/R5/R6）：dotnet 解析候选化（gym_env + optimize）→ `TryParseDuration` + `RlEnvCommand` 启动校验 + 两条测试 → 训练 factory 单次生成 + 复用构造接收 hash。验证：`dotnet test` + `mbri_adapter_selftest` + `selftest.py`。提交 `fix: 审查 P3 三项（rl-env 时长解析、dotnet 解析、MJCF 单次生成）`。
5. 批 5（R7）：`rl-split-contract.md` + `controllers/score_block_rl/README.md` 记录盲集索引失效面；`report.md` 收尾（逐条落地表 + 验证汇总 + 残余风险）；归档 + 期刊。
6. 推送一次（用户确认后）。

## 验证命令

```bash
export DOTNET_ROOT="$HOME/AppData/Local/Programs/robot-simulator-dotnet"
export NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"

# C# 全量
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1
# 定向（批 2/4 期间快速反馈）
"$DOTNET_ROOT/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1 --no-restore \
  --filter "FullyQualifiedName~SearchTurnCompensationTests|FullyQualifiedName~TrainingResetPerformanceTests|FullyQualifiedName~RlEnvCommandTests"

# Godot 工程构建（批 3；csproj 名以 godot/ 目录为准）
"$DOTNET_ROOT/dotnet.exe" build godot/<工程>.csproj
<Godot 真实 exe> --headless --path godot -- --parity-check ../replays/<parity 文件>

# Python
py -3.12 controllers/mbri_adapter_selftest.py
python -X utf8 controllers/score_block_rl/selftest.py

# 确定性 + 卫生
"$DOTNET_ROOT/dotnet.exe" run --project src/Sim.Cli -- replay-check replays/seed-42.json
git diff --check
```

## 注意

- 批 2 与批 4 都要跑 `TrainingResetPerformanceTests`：xUnit 跨类并行会放大 CPU 争用，性能门偶发抖动时先看 `ROBOT_SIM_RL_PERF_OUTPUT` 的 ratio 原始数据再判断，不直接改门。
- 提交规范：先 `git status --porcelain` + 学 `git log --oneline -5` 风格；禁止 `--amend`、禁止 push。
