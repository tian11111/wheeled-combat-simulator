# 验收报告：代码审查六项缺陷修复

## 结论

审查确认的 6 条代码缺陷全部落地（1×P1、2×P2、3×P3），第 7 条盲集索引按决定只做规范记录 + 报告披露。四个逻辑批次提交：`3b67612`（mbri 跳帧）、`df46726`（补偿旋钮实例化）、`20a1980`（桌面会话替换）、`accf487`（三条 P3）。全量质量门通过，未发现修复引入的回归。

## 逐条落地

| # | 发现 | 修复 | 验证 |
| --- | --- | --- | --- |
| R1 P1 | `mbri_adapter._check_tick` 跳帧只记故障不推进基准 → 一次丢帧后整场零动作 + 每帧刷 fault | 跳帧分支在报故障的同时把 `_last_tick` 对齐到本帧；重复/倒退分支不变；docstring 写明三分支语义 | `mbri_adapter_selftest.py` 16/16 绿（新增"跳帧后下一帧恢复""重复 tick 后下一帧恢复"两条，均断言 `faults` 不再增长、动作为非零、`healthy=True`） |
| R2 P2 | `Main.ResetLiveSession` 换 session 不释放旧引擎（MuJoCo 场景每次 F5 泄漏 mjModel+mjData） | 抽 `ReplaceSession(scenario)`，`_Ready`/`ApplyLayoutScenario`/`ResetLiveSession` 三处统一调用 | `dotnet build godot/GodotSim.csproj` 0 错误 0 警告；无头 `--parity-check` PASS（2400/2400 ticks、752/752 事件指纹、比分 4:49） |
| R3 P2 | `InPlaceTurnCompensation` 进程级可写静态被测试改成 1.0 且还原成非默认值，与 5 个并行 MuJoCo 测试类互相污染 | 改 `internal const DefaultInPlaceTurnCompensation = 4.0` + 实例字段；`MujocoPhysicsBackendFactory` 可选构造参数注入；测试各持 factory，去反射、`output` 改随返回值携带 | `dotnet test` 全量 393/393；`grep InPlaceTurnCompensation` 只剩常量与默认参数；`SearchTurnCompensationTests` 3/3（comp=1 基线、comp=4 门、登台回归） |
| R4 P3 | `resolve_dotnet_executable` 兜底返回已不存在的 Temp SDK 路径 | 候选存在性检查（`Programs/robot-simulator-dotnet` 优先、Temp 兼容），缺失抛 `FileNotFoundError` 并给出两条出路；`optimize.py::default_dotnet` 同步候选顺序 | 探针实测三分支：PATH 命中系统 dotnet；PATH 屏蔽 → 回退便携 SDK 现存路径；候选全缺 → 明确异常 |
| R5 P3 | `rl-env --duration` CurrentCulture 解析（逗号小数点静默 120.5→1205）且不在 try 内 | 提取 `internal TryParseDuration`（InvariantCulture + 有限正数），非法值应答 `{"type":"error"}` 并返回 2 | 新增 2 条测试通过：非法值拒绝（返回码 2 + 恰一条 error 行）、de-DE 区域下 `120.5 ≡ 120.5`；`RlEnvCommandTests` 10/10 |
| R6 P3 | 训练 factory 每集 `MujocoModel.Generate` 调用 3 次（2 次丢弃） | `Create` 单次生成并把 (xml, hash) 复用；复用构造接收 `modelSha256` | `TrainingResetPerformanceTests` 2/2（`CompileCount` 断言 1/2/coldRuns 不变）；隔离跑性能门 ratio 0.148（门 ≤0.5） |
| R7 流程 | 盲集一次性索引位于被忽略的 `.sim_runs/`，换机/清理后可重开已消费盲集 | 代码不动：`rl-split-contract.md` §3 补"索引失效面 + 盲验前核对既有揭示证据"、§4 补一行错误矩阵；`score_block_rl/README.md` 补同义说明 | 文档审查（本报告"残余风险"同步披露） |

## 验证汇总

```text
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1     393 passed / 0 failed
py -3.12 controllers/mbri_adapter_selftest.py       16 passed（原 14 + 新 2）
python -X utf8 controllers/score_block_rl/selftest.py  44 passed / 0 failed / 8 skipped（缺可选资源）
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json  PASS（逐位复现）
dotnet build godot/GodotSim.csproj                  0 错误 0 警告
Godot --headless --parity-check replays/godot-parity-seed42.json  PASS
git diff --check                                    干净
```

## 过程观察（非回归）

全量首跑出现 1 例 `TrainingResetPerformanceTests.HotResetP95_IsAtMostHalfOfColdCompileP95` 失败（hot p95 4.00 ms vs cold p95 7.48 ms，门要求 hot×2 ≤ cold）。判定为**既有并行抖动**而非本次改动引入：

- 隔离运行同一测试 ratio = 0.148（cold p95 1.57 ms、hot p95 0.23 ms），原始样本中 cold 含 6.77 ms、hot 含 2.89 ms 的离群值；
- 复跑全量 2 次均 391/391 绿；
- 改动只新增一个 readonly double 字段，不改变冷/热 reset 的耗时结构。

按计划未修改该测试的门槛，也未回退冗余生成（修复 6 的方向相反：它让 cold 更快）。

## 未覆盖项与残余风险

- **Main.cs 无单测**：`ReplaceSession` 依赖 Godot 节点类型，无法链接进 `Sim.Tests`（项目里链接的是纯适配器文件）。验证靠构建 + 无头 parity + 三处调用统一的结构约束，代码审查确认无其它 `new MatchSession` 直接赋值点。
- **盲集索引是本地守卫**（R7）：`.sim_runs/` 被 `.gitignore` 忽略，换机/清理后失效已写入 spec §3/§4 与 README；正式 v4 盲验前必须人工核对既有揭示证据。
- **legacy 反僵局微调**（`AntiStallBladeAmp` 默认 0.006）与 **`MujocoContacts` 手写 ABI 偏移**：审查认定属有文档的有意偏差 / 已有 DLL 哈希与版本门保护，本轮未动。
- **`.sim_runs/` 与 `tmp/` 下的运行态产物**（`profile-quick-*`、`perf-batch2-isolated.json` 等）为不入库的本地证据，未清理。

## 环境披露

便携 SDK `C:\Users\Neco\AppData\Local\Programs\robot-simulator-dotnet`（`DOTNET_ROOT` + `NUGET_PACKAGES=C:/Users/Neco/.nuget/packages`）；Godot `Godot_v4.7.2-stable_mono_win64_console.exe`（真实 exe 路径）；Python 用系统 `py -3.12`。所有命令在 `D:\project\robot-simulator`（分支 `test/score-block-ppo-checkpoint-round`）执行。
