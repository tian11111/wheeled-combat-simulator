# Sim 层开发规范（.NET 确定性内核 + 桌面壳）

> 本项目实际是 Godot 4 .NET + .NET 8 仿真栈；`backend/`、`frontend/` 为模板占位，
> 写仿真相关代码前先读本目录。

## 铁律（违反即破坏验收标准）

1. **Sim.Core 不依赖引擎与 IO**：不得出现 Godot/文件系统/网络/进程/时钟调用。
   随机只能来自 `DeterministicRandom`（种子派生），时间只能来自固定步长累加。
2. **确定性契约**：同种子 + 同被接受动作序列 ⇒ 逐位一致的事件与比分。
   改任何物理/裁判/传感器逻辑后必须跑：
   ```bash
   dotnet test
   dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
   ```
3. **协议演进只加不改**：`Sim.Protocol` 现有字段的 JSON 形状（camelCase、枚举拼写）
   永远不变；新能力走新枚举成员/新字段/新 `ProtocolVersion`。EventKind 只允许增量追加。
4. **官方布局单一来源**：块坐标等布局常量用 `Sim.Protocol.OfficialLayout`，
   禁止在 CLI/桌面壳/测试里再抄一份数字。磁盘规范形态是 `scenarios/wushu-ring-2026.json`。
5. **渲染不复刻规则**：`godot/` 只消费 `Snapshot`（经 `SnapshotView` 投影）并发裁判指令；
   任何本地计分/物理判分都是缺陷。

## 层次与依赖方向

```
godot/ ─┐
Sim.Cli ─┼─→ Sim.Core ─→ Sim.Protocol
Sim.Tests(链接 godot/src/SnapshotView.cs 做无 Godot 回归)
```

- 无 Godot 环境的可测逻辑放纯文件（如 `godot/src/SnapshotView.cs`），
  用 `<Compile Include>` 链接进 `Sim.Tests`，不要为它新建工程。
- 外部控制器一律走 `Sim.Cli.PythonBridge`（JSONL、request-id 匹配、超时→零动作、计 fault）。

## 行为对齐参考

- 遗留原型 `D:/project/robocup/robot-simulator/wushu_ring_sim.html` 只读。
- 移植/对齐决策记录在 `docs/PORTING_NOTES.md`；新增有意差异必须追加条目。
- 基线再生成：`node tools/legacy-baseline.js` → `src/Sim.Tests/fixtures/`（禁止手改）。

## 保真度诚实性

对外声明保真度必须引用根 `fidelity.json`；未标定子系统（摩擦/碰撞/堵转/登台）
不得在文档或输出中暗示等同真机。
