# 执行计划

1. 批 1：mbri_adapter 两行 + 夹具 + 死分支 → evaluate.py 守卫上移 → RobotModelLoader 回退补丁 → requirements.psutil → ArenaVisualizer 环材质缓存 → nul/tmp38sgy5pc 清理 + .gitignore 三条目 → robot-models.json untrack → JsonOptions 单例。验证：mbri 自测全绿 + dotnet test + git diff --check。提交 `fix: 体检批次一（8 项确认修复）`。
2. 批 2：回退归因门控（Step 判据 + 函数 + 测试 + 死字段）。验证：dotnet test（390）+ replay-check。提交 `revert(rl): 移除归因门控，reward 恢复 v2`。
3. 批 3：MatchSession 回放累加器 + Main 接线 + 单测。验证：dotnet test（391）+ replay-check。提交 `fix(godot): 回放播放改固定步长时钟`。
4. 推送一次；report.md 收尾（含 tmp38sgy5pc 删除结果）；归档 + 期刊。

## 验证命令

```bash
py -3.12 controllers/mbri_adapter_selftest.py
DOTNET_ROOT=<portable-sdk> dotnet test src/Sim.Tests/Sim.Tests.csproj
DOTNET_ROOT=<portable-sdk> dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
git diff --check
```
