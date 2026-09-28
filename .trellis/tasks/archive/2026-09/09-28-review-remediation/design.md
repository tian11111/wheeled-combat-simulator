# 技术设计：六项缺陷修复

## 总原则

- 只改缺陷面：每条修复的最小充分改动，不做顺带重构（唯一例外是 R2 抽出 `ReplaceSession`，因为它本身就是"防再次遗漏"的修复手段）。
- 生产行为不变性优先：R3、R6 都要求默认路径逐位不变（补偿默认 4.0、模型内容哈希缓存语义不变）。
- 安全面无发现，不引入新依赖、不改 `.gitignore`、不动已披露的有意偏差。

## 批 1（R1）：`_check_tick` 三分支语义

现状（`controllers/mbri_adapter.py:150-166`）：

| 分支 | 现状 | 目标 |
| --- | --- | --- |
| `tick <= _last_tick`（重复/倒退） | 记故障、返回 True、**不**推进基准 | 不变（重复帧不得推进时间基准） |
| `tick - _last_tick != 1`（跳帧） | 记故障、返回 True、**不**推进基准 | 记故障、返回 True、**推进基准到本帧 tick** |
| 正常 | 推进基准、返回 False | 不变 |

理由：跳帧的那一帧本身是"合法的新时间点"，只有它携带的运动决策不可信；不推进基准会让"一次丢帧"退化成"永久失联"。修后语义：丢帧的代价是那一帧 + 下一帧的零动作（下一帧因 `healthy` 恢复而正常决策），而不是整场零动作。

自测补两条（照 `mbri_adapter_selftest.py:173-185` 的 Stub/calibrated 构造）：
- `test_tick_jump_recovers_on_next_frame`：10 → 14（faults=1、零动作、healthy=False）→ 15（faults 不增、动作非零、healthy=True）。
- `test_duplicate_tick_then_next_frame_recovers`：10 → 10（faults=1）→ 11（正常）。

## 批 2（R3）：旋钮实例化

```csharp
// MujocoPhysicsBackend.cs
internal const double DefaultInPlaceTurnCompensation = 4.0;   // 候选 2/4/6 选定理由注释随迁
private readonly double _inPlaceTurnCompensation;
internal MujocoPhysicsBackend(PhysicsBackendContext context,
    double inPlaceTurnCompensation = DefaultInPlaceTurnCompensation)
internal MujocoPhysicsBackend(PhysicsBackendContext context, IntPtr externalModel,
    string modelSha256, double inPlaceTurnCompensation = DefaultInPlaceTurnCompensation)
```

- `SetControls` 用 `_inPlaceTurnCompensation`；删除静态可变字段。
- `MujocoPhysicsBackendFactory(double? inPlaceTurnCompensation = null)` → `Create` 透传（null ⇒ 默认）；`Instance` 单例不变。
- `MujocoTrainingPhysicsBackendFactory` 走默认值（训练路径不变）。
- `SearchTurnCompensationTests`：删 `SetCompensation` 反射与两处 `finally`，改为每用例 `new MujocoPhysicsBackendFactory(comp)` + `MatchEngineHost.Create(scenario, null, factory)`（该重载已 public）；`output` 静态改为随 `RunTurn` 返回值携带，消除并行串台。
- 逐位不变性论证：默认参数值 = 原静态字段初值 4.0；`SetControls` 读取点唯一。

## 批 3（R2）：会话替换统一

```csharp
private void ReplaceSession(Scenario scenario)
{
    var previous = _session;
    _session = new MatchSession(scenario);
    previous?.Dispose();
}
```

三处调用点：`_Ready`（94-96，`_session` 此时可能是 `null!`，故 `?.`）、`ApplyLayoutScenario`（1605-1607）、`ResetLiveSession`（1083）。释放顺序必须"先换新、再释放旧"之外的等价顺序均可（旧引擎不再被任何字段引用后释放）。注意 `_Ready` 里 `ApplyScenarioToShell` 依赖 `_session` 已就位，保持原顺序。

## 批 4（R4/R5/R6）

- R4：`resolve_dotnet_executable` 共享实现保持两份（跨目录无法互相 import），逻辑一致：override → `shutil.which` → 候选列表存在性检查 → `FileNotFoundError(消息含 --dotnet 与便携 SDK 路径)`。候选 = `Programs/robot-simulator-dotnet/dotnet.exe`、`Temp/robot-simulator-dotnet-sdk/dotnet.exe`。
- R5：`internal static bool TryParseDuration(string? raw, out double seconds, out string? error)`（`Sim.Cli` 已 `InternalsVisibleTo("Sim.Tests")`）；`Run` 开头解析，失败 `EmitError(...)` + `return 2`（与 batch preflight 退出码约定一致，Python 端 `_send` 读到 error 行会抛含 message 的 RuntimeError）。`--duration` 未给出 ⇒ 默认 120.0。
- R6：`Create` 单次 `var (xml, hash) = MujocoModel.Generate(context)`，缓存命中只用 hash、未命中用同一份 xml；复用构造接收 `modelSha256`（`ModelSha256 = modelSha256`），删除第三次生成。

## 验证门

| 修复 | 门 |
| --- | --- |
| R1 | `mbri_adapter_selftest.py` 全绿（14→16 用例） |
| R2 | Godot 工程构建 0 error；无头 parity PASS |
| R3 | `dotnet test` 全量全绿（348+ 用例含 6 个 MuJoCo 类）；`grep InPlaceTurnCompensation` 仅剩常量与本注释 |
| R4 | Python 直接调用返回现存路径；`selftest.py` 全绿 |
| R5 | 两条新测试通过；`dotnet test` 全绿 |
| R6 | `TrainingResetPerformanceTests` 两条通过（`CompileCount` 断言 1/2/coldRuns 不变；性能门实测） |
| 全局 | `replay-check replays/seed-42.json` PASS；`git diff --check` 干净 |

## 残余风险（本轮不修，写入 report + spec）

- 盲集索引为本地文件（R7）：换机/清理后守卫失效——spec 记录 + 跨机复核要求。
- Main.cs 会话生命周期无单测（Godot 节点），靠构建 + parity + 三处统一来兜。
- legacy 反僵局默认值、`MujocoContacts` ABI 探针：审查认定有文档的有意偏差，不动。
