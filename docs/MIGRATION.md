# 迁移说明：旧原型 → 新架构

旧原型：`D:/project/robocup/robot-simulator/wushu_ring_sim.html`（单文件、浏览器、Three.js 渲染 + 内嵌 JS 内核）。
新项目：`D:/project/robot-simulator`（.NET 8 确定性内核 + Godot 4 .NET 桌面壳 + Python 策略桥）。

## 为什么要迁移

- 旧版把规则、物理、传感器、渲染、UI 全部揉在一个 ~3000 行 HTML 里，无法单独回归规则、
  无法无头批量评测、控制器无法替换。
- 新版按 [ARCHITECTURE.md](ARCHITECTURE.md) 分层：规则内核可在无图形环境下逐位复现。

## 对应关系

| 旧（HTML/JS） | 新（.NET） | 说明 |
| --- | --- | --- |
| 内嵌 CORE（`stepSimExt` 等） | `src/Sim.Core` | 逐行移植，行为对齐 |
| 全局状态/日志 | `RuntimeState` + `EventBus` | 事件带结构化 kind |
| `window` 协议桥 | `Sim.Cli/PythonBridge.cs` | JSONL 进程，协议不变 |
| Three.js 渲染 | `godot/`（脚手架） | 渲染与规则彻底分离 |
| 浏览器入口 | 已废弃 | 新产品入口是 CLI / Godot 桌面端 |

**注意**：旧仓库只是行为/协议参考资料，不是新产品入口。不要从旧仓库构建或部署；
也不要将其生成的浏览器产物带入新项目。

## 行为差异

所有**有意**的行为差异与补充决策记录在 [`PORTING_NOTES.md`](PORTING_NOTES.md)
（EventKind 增量、cls 白名单、消息前缀、requestId 归属、手动模式消极判罚、观测 onPlatform 语义）。
未列出的行为均按遗留实现逐行对齐，并由固定种子回归测试守护。

## 验证迁移正确性

```bash
dotnet test                                                  # 规则/确定性/回放回归
dotnet run --project src/Sim.Cli -- match --seed 42          # 与旧原型同种子对照人工核验
```

`tools/` 目录保留旧原型的确定性基线脚本（`legacy-baseline.js`）用于交叉核对。
