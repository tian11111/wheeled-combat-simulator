# Godot 深色赛事控制台 UI 执行计划

## Ordered Checklist

1. 读取 Godot/Sim 规范和当前 HudPanel 状态流，建立现有快捷键与回调顺序基线。
2. 在 `HudPanel.cs` 内提取最小视觉样式辅助方法，重排状态卡、事件卡、帮助卡和编辑工具栏；保持 `UpdateFrame`/`UpdateEditor` 的数据语义。
3. 统一回放控制条按钮、时间轴、tick 文本和编辑按钮的间距、尺寸、禁用态与焦点/悬停反馈。
4. 用真实 renderer 生成实况、回放、编辑模式截图，检查 1152×648、1280×720、1920×1080 的遮挡和文本截断。
5. 运行 Godot edit-smoke、CLI replay-check、Godot parity 和全量 .NET 测试；若失败，按设计边界回退或修正 UI 层。

## Validation Commands

```powershell
dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 -p:UseSharedCompilation=false
dotnet run --project src/Sim.Cli --no-build -- replay-check replays/godot-parity-seed42.json
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json
godot --path godot --rendering-method gl_compatibility -- --edit-smoke --capture <out.png>
git diff --check
```

## Review Gates

- 不得改动 `Sim.Core`、`Sim.Protocol`、`Snapshot`、回放文件或布局格式。
- 视觉截图必须在真实 renderer 下生成；dummy renderer 失败只能记录能力边界。
- UI 动态文本变长、编辑栏显示/隐藏、回放栏显示/隐藏和应用按钮禁用态都要检查。

## Rollback Points

- 若只发生视觉问题，回滚 `HudPanel.cs` 的布局/样式改动。
- 若发现状态来源或回调顺序变化，停止并恢复到原有控件绑定，再重新拆分 UI 表现层。
