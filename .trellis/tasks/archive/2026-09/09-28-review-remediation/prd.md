# 修复代码审查发现的六项缺陷

## Goal

落地 read-only 代码审查（review-agent 全分支 diff：572 文件 / +62,615 行）确认的 6 条代码缺陷 + 1 条规范记录项，分四批提交。审查结论：1×P1、2×P2、3×P3 可执行缺陷，安全面（SQL/注入/命令执行）无发现；本任务只修缺陷，不做重构，不触碰审查已认定"有意且有文档"的偏差（legacy 反僵局默认值、`MujocoContacts` ABI 探针、`TrainingResetPerformanceTests` 性能门阈值）。

## Requirements

- R1 **[P1] MBri 适配器跳帧后永久故障**：`controllers/mbri_adapter.py:160-165`（`_check_tick`）跳帧分支只记故障、不更新 `self._last_tick`，导致此后每帧都被判跳帧 → 全场比赛 `healthy=False` + 零动作 + 每帧刷一条 fault。修：跳帧分支在 `return True` 前把 `_last_tick = tick` 重新对齐；重复/倒退分支保持"不推进基准"；docstring 写明三分支语义。自测（`controllers/mbri_adapter_selftest.py`）新增两条：跳帧后下一帧恢复正常（无新故障、动作非零、healthy=True）、重复 tick 后下一帧正常。
- R2 **[P2] 桌面 F5 泄漏上一场引擎**：`godot/src/Main.cs:1083`（`ResetLiveSession`）替换 `_session` 时未释放旧 session，而同文件 94-96、1605-1607 两处都写了 `previousSession?.Dispose()`。MuJoCo 场景下每次 F5 泄漏一套原生 `mjModel`+`mjData`（裸 IntPtr、无终结器/SafeHandle）。修：抽私有 `ReplaceSession(Scenario)` 统一三处调用，杜绝再次遗漏。
- R3 **[P2] 补偿旋钮跨测试类污染**：`src/Sim.Mujoco/MujocoPhysicsBackend.cs:22` 的 `internal static double InPlaceTurnCompensation = 4.0` 被 `src/Sim.Tests/SearchTurnCompensationTests.cs`（反射 setter、两处 `finally` 还原成 1.0）改动；xUnit 默认跨类并行，另有 5 个未加 `[Collection]` 的测试类会构建 MuJoCo 引擎（`SearchClassificationFlowTests`、`MujocoIntegrationTests`、`TrainingResetPerformanceTests`、`MujocoScoreEdgeGuardTests`、`BlockAttributionTests`）。修：旋钮改为 backend 实例字段（`internal const DefaultInPlaceTurnCompensation = 4.0` 为默认），`MujocoPhysicsBackendFactory` 加可选构造参数注入，测试各自持有 factory；生产路径与训练 factory 保持默认 → 行为逐位不变。
- R4 **[P3] dotnet 兜底路径失效**：`controllers/score_block_rl/gym_env.py:26` 与 `tools/param-optimization/optimize.py:37` 兜底返回 `~/AppData/Local/Temp/robot-simulator-dotnet-sdk/dotnet.exe`，该目录已无 `dotnet.exe`（可用便携 SDK 在 `~/AppData/Local/Programs/robot-simulator-dotnet`）。修：候选 = 显式 override → `shutil.which` → 现存候选路径（新位置优先、旧位置兼容）→ 都不存在则抛 `FileNotFoundError` 并给出两条出路，不再返回不存在的路径。
- R5 **[P3] `rl-env --duration` 区域敏感且无保护**：`src/Sim.Cli/RlEnvCommand.cs:34` 用 CurrentCulture `double.Parse` 且位于 try 之外；逗号小数点区域下 `120.5` 会被静默解析成 `1205`，非法值则未捕获异常直接杀进程（训练端只看到 "bridge exited without a response"）。修：解析提取为 `internal static TryParseDuration`（InvariantCulture + 有限正数），非法值写一条 `{"type":"error"}` 到 stdout 并返回 2。定向测试两条（进程内）：非法值拒绝、de-DE 区域下 `120.5` 仍解析为 120.5。
- R6 **[P3] 训练 factory 重复生成 MJCF**：`src/Sim.Mujoco/MujocoTrainingPhysicsBackendFactory.cs:18-24` 每次 `Create` 调 `MujocoModel.Generate` 两次（外加 `MujocoPhysicsBackend.cs:65` 复用构造第三次），返回值两次被丢弃。修：`Create` 只生成一次并把 (xml, hash) 复用；复用构造函数接收 `modelSha256`。`CompileCount` 语义与"模型按内容哈希缓存"不变。
- R7 **[流程] 盲集索引失效面记录**：一次性盲集索引 `<repo>/.sim_runs/score-block-final-holdout-v4-run.json` 位于 `.gitignore:13` 忽略的目录（既有取舍：本地运行态不入库，位置由 `.trellis/spec/sim/rl-split-contract.md:27` + `controllers/score_block_rl/README.md:95` 固定）。换机/清理该目录后守卫不再阻止重跑。按用户决定：**不改代码**，把失效面与"跨机正式盲验前须核对仓库内既有 final_holdout_v4 揭示证据"写进 spec + README，并在 report 披露为残余风险。

## Acceptance Criteria

- [ ] R1：`py -3.12 controllers/mbri_adapter_selftest.py` 全绿（含 2 条新用例）；跳帧后第 2 帧 `_last_tick` 已对齐、`faults` 不再增长。
- [ ] R2：三处会话替换走同一 `ReplaceSession`；Godot 工程构建通过；无头 `--parity-check` PASS。
- [ ] R3：`InPlaceTurnCompensation` 静态字段消失（`grep` 无命中）；`SearchTurnCompensationTests` 无反射；`dotnet test` 全量全绿（含 5 个并行 MuJoCo 类）。
- [ ] R4：`resolve_dotnet_executable` 不再返回不存在的路径；`python -X utf8 controllers/score_block_rl/selftest.py` 全绿。
- [ ] R5：新测试断言非法 `--duration` 返回 2 且恰一条 error 行；de-DE 下 `120.5` ≡ 120.5；`dotnet test` 全绿。
- [ ] R6：`MujocoModel.Generate` 每 episode 只调用一次（代码审查 + `TrainingResetPerformanceTests` 两条通过，性能门 `hotP95*2 <= coldP95` 实测通过）。
- [ ] R7：spec §3/§4 旁新增失效面小节 + README 补一句；report 列为残余风险。
- [ ] 全量验证：`dotnet test`（Sim.Tests）+ `mbri_adapter_selftest` + `selftest.py` + `replay-check` + `git diff --check` 全绿；每批一个提交，完成后推送。

## Notes

- 审查报告（本任务的需求来源）为 review-agent 的一次性输出，证据行号在 prd 内逐条固化；不做"重构式修复"，每条只改必要面。
- Main.cs 依赖 Godot 节点、无法链接进 `Sim.Tests`：R2 的验证靠构建 + 无头 parity + 代码审查（三处统一），此测试缺口在 report 披露。
- R6 的性能门方向与修复方向相反（cold 变快 ⇒ 门更严），实测为准；若失败则停下报告数据，不为过门回退冗余生成。
