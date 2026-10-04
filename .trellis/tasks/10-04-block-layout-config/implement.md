# Implement — 能量块布局可配置

## 顺序

1. [ ] 协议：ObjectSet.Debuffs + PhysicsPoses.Debuffs + Scenario.Validate 数量上限（src/Sim.Protocol）
2. [ ] 内核：MatchEngine.BuildObjectSet 收集 debuffs（src/Sim.Core）
3. [ ] MuJoCo：BuildPhysicsPoses 填 Debuffs（src/Sim.Mujoco）
4. [ ] 桌面：LayoutDraft AddBlock/RemoveBlock/ToggleBlockKind + LayoutEditor 按键 + project.godot 动作 + Main/HudPanel 提示 + SnapshotView 多减益渲染
5. [ ] 测试：
   - ScenarioTests/BlockLayoutTests：0 块合法、13 块报错、官方 2+1 校验不变
   - MatchEngineTests：3 减益布局 → Debuffs.Count==3、Debuff==first、每个减益上台独立 +6
   - LayoutDraftTests：AddBlock 空位/重叠规避、Remove、ToggleKind、undo/redo、保存往返
   - SnapshotViewTests：多减益帧块数
6. [ ] 门禁：`dotnet test`（注意 RL 性能门并行抖动，失败先隔离复跑）+ godot-parity-seed42 fixture 校验
7. [ ] 桌面冒烟：无人值守编辑器流程 加块→K 切类型→Delete 删块→保存（--capture 截图目检）

## 验证命令

```bash
DOTNET="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
"$DOTNET" test src/Sim.Tests -c Release
godot --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json   # 真实 exe 路径, 见记忆
```

## 行尾纪律

仓库 blob 为 LF（MujocoVehicleMeshTests.cs 除外）；提交前 `git diff --stat` 行数须与实际改动一致，
超出行数 = CRLF churn，按文件 HEAD 原状归一（python 二进制替换）。

## 回滚点

- 每个大步骤后可独立提交；步骤 4（桌面）独立成一个 commit。
