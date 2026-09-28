# 体检确认项修复（10 条）— 验收报告

结论：**10/10 全部落地并验证**，三批提交、已推送（`667e5fe` / `c16f90c` / `2b21c64`）。来源为「仓库优化机会体检」工作流的 10 条 confirmed 发现（每条含独立复核实跑证据）。

## 1. 逐条落地

| rank | 修复 | 验证 |
| --- | --- | --- |
| 1 | `mbri_adapter.py` 视觉坐标改读 camelCase `x/y`；自测夹具同步；删不可达 smoke 分支 | `mbri_adapter_selftest.py` **ALL PASSED** |
| 3 | `evaluate.py` 的 `--freeze` 前置守卫上移 | 实跑：**1.4 s 拒绝**（原 97 s 烧完 sweep 才拒）、无文件产出 |
| 4 | `RobotModelLoader` 换路径失败分支补 `ShowPrimitive(true)` | Sim.Tests 全绿（静态路径，当前调用模式不可达） |
| 6 | `requirements.txt` 补 `psutil==7.2.2` | 文件核对（本机两环境实测在用版本） |
| 7 | `ArenaVisualizer` 登台环材质懒缓存（`_ringMaterialOn/Off`） | Sim.Tests 全绿；呼吸灯语义未动 |
| 8 | 删 `nul`、`tmp38sgy5pc/`；`.gitignore` 补 `.zcode/`、`.zcodeignore`、`.dsh/` | 两文件已消失；`git status` 不再显示这三个条目 |
| 9 | `godot/robot-models.json` untrack（本机保留） | `git rm --cached` 已入库；`.gitignore` 已补 |
| 10 | `JsonOptions()` 改 static readonly 单例（选项内容不变） | Sim.Tests 全绿；输出字节不变 |
| 2 | 回退归因门控：Step 判据恢复 v2、删 `EdgeShapingApplies`/`TargetContacts`/测试/死字段 `PrevEdgeDistance`（4 处写入点） | Sim.Tests **389** 全绿（-1 门控测试）；`replay-check` 逐位 PASS |
| 5 | 回放播放改固定步长时钟：`MatchSession.AdvanceReplayPlayback(delta)` + `ResetReplayClock()`，Main 接线，暂停插值按 delta 折算（1.2/s ≈ 原 0.02/帧@60fps） | 新增 `ReplayPlaybackTests`（2 个用例：0.5s⇒10 tick 与帧数无关、零碎时间不推进、停播、手动 seek 重置）；Sim.Tests **391** 全绿 |

## 2. 验证汇总

- `Sim.Tests`：390（批 1）→ 389（批 2 移除门控测试）→ **391**（批 3 新增 2 个回放用例），最终全绿。
- `replay-check replays/seed-42.json`：逐位 PASS（批 2/批 3 后各跑一次）。
- `git diff --check`：干净。
- mbri 自测、evaluate 守卫实跑均通过（见上表）。

## 3. 环境披露

- 本会话临时目录里的便携 .NET SDK 与 Python venv 被系统清理（Temp 自动回收），SDK 重装到持久位置 `C:\Users\Neco\AppData\Local\Programs\robot-simulator-dotnet`（用户级、无管理员、可整目录删除）；NuGet 缓存未受影响。
- Python 侧验证改用系统 `py -3.12`（确认装有 gymnasium/stable_baselines3）。

## 4. 未覆盖

- 回放播放速度的**视觉手动确认**未做（时钟数学有单测；桌面端观感待你下次打开回放时顺带确认）。
- `.dsh/`、`.zcode/` 仅加入忽略、未删除（工具仍在会话中使用）。
- 体检报告的 `notCovered` 三项（性能基准、依赖安全审计、.trellis 归档逐字审）仍不覆盖。
