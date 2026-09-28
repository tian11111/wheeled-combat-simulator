# 设计：体检确认项修复

三批互相独立，每批一个提交。要点只记实现决策，逐条证据见体检确认记录（workflow 报告）。

## 批 1（rank 1/3/4/6/7/8/9/10）

1. **mbri_adapter**：`b.get("X")→get("x")`、`d.get("X")→get("x")`（含 Y 同理）；自测夹具同步小写；删除 `_car_sensor_inputs` 中 `mode == "smoke"` 的不可达分支（handle() 在 smoke 时已提前 return，mode 不可变）。
2. **evaluate.py**：把 `if args.freeze and not args.select_candidate: parser.error(...)` 移入 ：481-496 前置校验块（args/selection 在此均可用），删除 ：634 的迟到守卫。
3. **RobotModelLoader**：Apply() 中 `LoadInto` 失败分支 return 前补 `ShowPrimitive(robotRoot, true)`。
4. **requirements.txt**：追加 `psutil==7.2.2`。
5. **ArenaVisualizer**：新增字段 `_ringOnMaterial/_ringOffMaterial`（`??=` 懒创建，用现有 `MakeEmissive(RingOn, 0.8f)`/`MakeMatte(RingOff)`），`ApplyRobot` 的 `ring.MaterialOverride` 改为二选一指向；呼吸灯（:561-565）不动。两环共享材质安全：环的发光能量恒定、无逐实例状态（呼吸灯走的是 energy 倍率，不走材质替换）。
6. **卫生**：删 `nul`（Git Bash `rm ./nul`；失败则 `cmd //c "del \\\\.\D:\project\robot-simulator\nul"`）、删 `tmp38sgy5pc/`；`.gitignore` 追加 `.zcode/`、`.zcodeignore`、`.dsh/`。
7. **untrack**：`git rm --cached godot/robot-models.json` + `.gitignore` 追加 `godot/robot-models.json`。
8. **JsonOptions 单例**：`RlEnvCommand` 内 `JsonOptions()` 改为返回 `static readonly` 实例（选项内容逐字节不变：CamelCase + UnsafeRelaxedJsonEscaping），四处调用点不动。

## 批 2（rank 2 回退）

- `RlEnvCommand.Step()`：edge shaping 判据恢复 `if (!double.IsNaN(edgeAfter) && !double.IsNaN(edgeBefore))`。
- 删除 `TargetContacts`、`EdgeShapingApplies`；删除 `RlEnvCommandTests.EdgeShaping_AppliesOnlyWhenAllTargetContactsAreOurs`。
- 删除 `RuntimeState.EpisodeState.PrevEdgeDistance` 及 `RlEnvCommand.cs` 内三处赋值（确认零读取）。

## 批 3（rank 5 回放时钟）

- `MatchSession` 新增纯方法 `AdvanceReplayPlayback(double deltaSeconds)`：内部累加器 `_replayAccumulator += delta; while (_replayAccumulator >= TickSeconds) { ReplayStep(+1); _replayAccumulator -= TickSeconds; }`，并返回/维护插值推进量；`Main.cs` 回放分支改为调用该方法（删除每帧 +1 与固定 +0.02），保留 step 后 alpha 重置语义。
- `TickSeconds` 来源与 `ReplayStep` 现有语义以代码为准（实现时读 MatchSession.cs:184-197）。
- 单测：构造带 replay 的 MatchSession，断言 delta=0.5s（tickSeconds=0.05）恰好前进 10 tick、alpha 归零语义。

## 验证门

- 批 1：`py -3.12 controllers/mbri_adapter_selftest.py` 全绿；`dotnet test`（391）；`git diff --check`。
- 批 2：`dotnet test`（390）；`replay-check` PASS。
- 批 3：`dotnet test`（391）；`replay-check` PASS。
