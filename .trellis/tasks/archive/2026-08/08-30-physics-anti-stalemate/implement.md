# Implementation Plan — 物理反僵局 (铲刃微调)

1. **SimParameters 加性参数**: `antiStallBladeAmp` (0.006)、`antiStallBladePeriodUs`
   (2.1)、`antiStallBladePeriodThem` (2.7) — 可空 record 属性 + FromDictionary
   解析 + 文档注释。既有参数解析回归测试。
2. **PhysicsWorld 相位注入**: 构造函数追加 `phaseUs`/`phaseThem` (弧度, 默认 0);
   `MatchEngine` 构造处用 `DeterministicRandom.HashString32($"anti-stall:{seed}:{role}")`
   /uint.MaxValue·2π 派生并传入。确认无 rng 流消费。
3. **楔入分支微调**: `ResolveRobotPair` 楔入段按 design 公式叠加有效铲刃高度;
   保持原 0.004 阈值与 WedgedFront 下游; 仅 facing > 0.6π 分支内。
4. **测试**: `src/Sim.Tests/AntiStallmateTests.cs` 四条 (design.md 测试设计);
   顶牛相关既有断言随基线更新。
5. **基线再生成 (按序, 全部正规流程)**:
   1. `node tools/legacy-baseline.js` → legacy-fsm-seed21 / legacy-pushoff-seed42
   2. 删除 `restart-replay-seed42.json` → 测试首跑重生成
   3. CLI `replay-record` 重录 `godot-parity-seed42.json` 与 `replays/seed-42.json`
   4. `PORTING_NOTES.md` 追加有意偏差条目; `docs/ARCHITECTURE.md`/`godot/README.md`
      参数表更新
6. **质量门禁**: `dotnet build` + `dotnet test` 全绿; CLI `replay-check` 对全部
   再生成基线 PASS; Godot `--parity-check godot-parity-seed42.json`、
   `--edit-smoke`、`--camera-smoke` 全绿; `git diff --check`。
7. **文档与证据**: README/参数表、PORTING_NOTES、僵持单测的对比数据
   (修改前 60 s 不解 vs 修改后 10 s 内破局) 记录到任务目录。

## Risky files

- `src/Sim.Core/Physics.cs` (楔入分支 — 唯一行为改动点)
- `src/Sim.Core/SimParameters.cs` / `MatchEngine.cs` (参数 + 相位注入)
- 全部物理相关基线 fixture (正规流程再生成)

## Rollback

- 运行时回滚: `antiStallBladeAmp=0` 场景即恢复旧行为。
- 代码回滚: revert 楔入分支单提交 + 基线再生成一次。
