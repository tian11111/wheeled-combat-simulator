# 10-01 RL 桌面接入 — 桌面真窗口验收报告（acceptance-desktop）

- 任务：`.trellis/tasks/10-01-rl-desktop-controller/`（本报告补齐 prd 验收清单 D2「真窗口人工目检」的无人值守替代）
- 执行：桌面验收员（subagent），2026-10-01 14:58–15:2x；两轮：①计划验收 ②缺陷修复复验
- 方法：仓库自带 QA 通道（`--settings-smoke`/`--capture`/`--capture-frames`，godot/src/Main.cs:234-251,167-196,1566-1607）驱动**真实窗口**，全部截图用 Read 工具目检；设置经用户 JSON 种子注入，验收后还原
- 启动形态：`"<EXE>" --path godot -- <QA 参数>`；EXE=`C:/Users/Neco/AppData/Local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe`
- 证据目录：`godot/docs/qa-10-01/`（未跟踪；不含 git add/commit）
- 设置种子：`C:/Users/Neco/AppData/Roaming/Godot/app_userdata/WushuRingSim/wushu-ring-settings.json`，`usController={mode:external, timeoutMs:5000, command=绝对脚本+绝对 checkpoint}`；checkpoint=`.sim_runs/score-block-v4-independent-throughput-20260927/suite-01/seed-20260929/checkpoints/rl_model_204800_steps.zip`（sha256 `6d8256b242dbb45b6e9f7485d936ebcf5993a145fb43d0ddef08913dba0f12cd`，与 report.md:73 身份行一致）；场景=`scenarios/wushu-ring-2026-mujoco.json`（seed 42，matchDuration 120s）

## 1. 验收项与判定（复验后状态）

### 1.1 QA 通道自检 — **pass**
- 命令：`"$EXE" --path godot -- --settings-smoke`
- 证据：`godot/docs/qa-10-01/00-settings-smoke-no-capture.log`
- 依据（读文件）：exit 0；日志含 `[settings-smoke] 设置面板已打开; 使用 --capture <png> 可保存真实渲染截图` 与 `[settings-smoke] UI 构建冒烟通过`（Main.cs:234-251,1481-1489）。

### 1.2 设置页控制器区可见 + 种子值被加载 — **pass**
- 命令：`"$EXE" --path godot -- --settings-smoke --settings-tab 2 --capture "$OUT/01-settings-controller-seeded.png"`
- 证据：`01-settings-controller-seeded.png`、`01-settings-smoke.log`
- 依据（读图）：『小车控制器』页激活；我方/BLUE（RL 展演）区：来源=外部命令、超时=5000 ms、命令框=种子绝对命令（右侧被裁剪）；对手/RED=内置 FSM 100 ms。日志 `[settings] 已加载 …wushu-ring-settings.json: 1280x720 windowed, uiScale=1`。
- 注：该跑未传 `--scenario-path`，启动装配按 legacy 拒绝打红字（属预期，见 1.8）。

### 1.3 控制器设置持久化（保存/应用路径） — **inconclusive**
- 证据：`T1-settings-store.log`（`dotnet test … --filter "FullyQualifiedName~SettingsStore_RoundTrips"`：失败 0/通过 2）；载入侧=1.2 截图
- 依据：载入方向已实证（截图显示即磁盘 JSON 值）。**保存方向（点『应用设置』按钮落盘）无法无人值守执行**——settings-smoke 不注入输入、30 帧即退（Main.cs:1481-1489），Godot 侧无设置页输入注入通道；仅由 SettingsStore.Save 单测（godot/src/DesktopSettings.cs:454-482）与读码背书。验收期间应用从未改写种子文件（内容始终等于写入值）。

### 1.4 坏命令预检响亮失败（不静默） — **pass**（修复复验后更优）
- 命令（两轮）：`"$EXE" --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json --capture <png> --capture-frames 90`；命令种分别=不存在可执行文件 / 旧占位符相对路径 `tools/rl-bridge/…`
- 证据：`02-bad-command.png`+`.log`（修复前）、`09a-old-placeholder-recheck.png`+`.log`（修复后，旧形式）、`T2-preflight.log`
- 依据（读图+读日志）：HUD 红『控制器 我方 内置 FSM（外部控制器被拒绝）』+ 红提示；修复前日志 `外部控制器预检失败…An error occurred trying to start process '…definitely-missing-controller.exe' with working directory 'D:\project\robot-simulator\godot'. 系统找不到指定的文件。`——**顺带用系统报错原文坐实子进程 CWD=godot/**；修复后旧占位符形式 <5s 即报 `controller process exited without a response (command failed to start or crashed)`。T2：ControllerPreflightTests 失败 0/通过 10。

### 1.5 RL 控制器真窗口驱动我方（核心） — **pass**
- 命令：`"$EXE" --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json --auto-arm --capture <png> --capture-frames 240/1800/5400`；修复复验再跑 `--capture-frames 1800`（`10-rl-live-regression-f1800.*`）
- 证据：`03a-rl-f240.png`、`03b-rl-f1800.png`、`03c-rl-f5400.png`、`10-rl-live-regression-f1800.png` 及同名日志
- 依据（读图+读日志）：
  - HUD 绿字：`控制器 我方 外部进程 · rl_desktop_runner.py`；`策略 我方 外部·在线 / 对手 内置 FSM · 展演交接 tick=281 目标=增益块`；全程无红色 fault；
  - 左卡我方 `[MANUAL]` 且外部策略持续给动作（240 帧 v=-1.00 w=-39.00；1800 帧 v=0.00 w=7.00；5400 帧 v=-1.00 w=2.00），三张场景车辆/积木位姿均不同（非空转）；1800 帧=剩余 66.9s、比分 3:0、我方推增益 3；5400 帧=终局 3:1、蓝方胜·比赛时间结束(手动模式)；事件栏只有对手 FSM 事件+我方 FINISHED（交接后我方无 FSM 事件）；
  - 日志（每跑）：`[rl-runner] mode=real checkpoint=… sha256=6d8256b2… deterministic=True`、`[controller] 我方 外部进程 · rl_desktop_runner.py（SCORE_BLOCK 展演）…`、`[controller] 已启动桌面后台 driver`、`[shell] --auto-arm: 已发令进入 RUNNING`；live runner 段无 zero-action/fault 行（唯一 zero-action 为预检残帧 frame=1，设计内）。
  - 修复复验跑（10 号）与第一轮（03b）一致：比分 3:0 @ 67.1s vs 66.9s（桌面按墙钟，允许此级差异）。
  - 注：runner 退出摘要（`exit reason=eof … faults=0`）不会出现——桥 Dispose 直接 `Kill(entireProcessTree)`（src/Sim.Controller/ExternalControllerBridge.cs:160-180），faults 判据以 HUD 绿字 + runner stderr 无 fault 行为准。

### 1.6 HUD 启动期假红『启动中』 — **inconclusive**（未真实可见，不记 defect）
- 命令：同 1.5 但去 `--auto-arm`，`--capture-frames 1/2/3/6/15/45/90`（第一轮）+ `2/15`（修复复验）
- 证据：`04-startup-f1.png`（全黑）、`04-startup-f2/f3/f6/f15/f45/f90.png`、`11-startup-recheck-f2/f15.png`
- 依据（读图）：9 个采样点中 frame=1 为全黑（首帧视口纹理未就绪），其余全部已是绿字『外部·在线 / 对手 内置 FSM』，从未渲染出红『外部·启动中』。代码路径存在（godot/src/DesktopLiveDriver.cs:495-498 构造期 Running=false/LastFault=启动中；godot/src/HudPanel.cs:259-263 Configured&&!Running 判红），但本机热缓存下该窗口 <1 可截帧；无法证实『头几百毫秒假红』，也无法排除冷缓存/慢机 1-2 帧闪红。候选缺陷不成立。

### 1.7 placeholder 与说明口径一致性 — 初判 **fail**（真实缺陷），修复后复验 **pass**
- 初判证据：`01-settings-controller-seeded.png`（占位符原文 `…tools/rl-bridge/rl_desktop_runner.py…`，godot/src/SettingsPanel.cs:317 改前）+ `06a-placeholder-command.log`（`python.exe: can't open file 'D:\project\robot-simulator\godot\tools\rl-bridge\rl_desktop_runner.py': [Errno 2]` + 预检失败红 HUD）+ `06b-note-command.log`（`../tools/…` 形式成功、runner mode=real）与同页说明 :328 矛盾。
- 复验证据：`08-settings-placeholder-fixed.png`（空命令字段显示新占位符 `例如：py -3.12 -X utf8 ../tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip>`，与 :328 及 docs/CONTROLLER_PROTOCOL.md:154-156、godot/README.md:179-187 同口径）；`09a-old-placeholder-recheck.log`（旧形式快速失败+新文案）；`09b-note-form-recheck.log`（`../tools/…`+真 checkpoint：预检通过、driver 启动、[rl-runner] mode=real）。

### 1.8 （顺带）legacy 场景拒绝外部控制器 — **pass**
- 命令：`"$EXE" --path godot -- --capture "$OUT/07-legacy-reject.png" --capture-frames 90`（种子 external，不传 `--scenario-path`）
- 证据：`07-legacy-reject.png`+`.log`
- 依据（读图+读日志）：HUD 红『控制器 我方 内置 FSM（外部控制器被拒绝）』+『展演需要 mujoco 场景（physics.backend 未写 = legacy，训练线为 mujoco）；已回退内置 FSM』（ControllerWiring.cs:46-47，Main.cs:1126）；无 `[rl-runner]` 行（该路径不启动子进程）。T3：ControllerWiringTests 失败 0/通过 16。

### 1.9 修复复验测试 — **pass**
- `T4-fix-targeted.log`：`dotnet test … --filter "FullyQualifiedName~ControllerPreflightTests|FullyQualifiedName~RlControllerBridgeTests"` → 失败 0/通过 19（含新增：当场退出 launch 且 <3s、新前缀归类、死进程不烧满超时）。
- `T5-full-suite.log`：`dotnet test src/Sim.Tests/Sim.Tests.csproj -m:1 --nologo` → exit 0；失败 0/通过 567/跳过 1/总计 568（17 s）；与修复方声明一致，较报告基线 565+2 新增。

## 2. 缺陷与修复对照

| # | 位置 | 问题 | 严重度 | 修复 | 复验 |
| --- | --- | --- | --- | --- | --- |
| D1 | godot/src/SettingsPanel.cs:317 | 占位符示例为 repo-root 相对 `tools/rl-bridge/…`，与子进程 CWD=godot/ 及同页说明矛盾，照填必失败 | low | 改为 `py -3.12 -X utf8 ../tools/rl-bridge/rl_desktop_runner.py --checkpoint <zip>` | `08-…png` 截图占位符=新文本；`09b` 该形式真 checkpoint 预检通过；`09a` 旧形式仍（正确地）快速失败 |
| D2 | src/Sim.Controller/ExternalControllerBridge.cs:100-122 + godot/src/ControllerPreflight.cs:49-66,97 | 命令当场退出（脚本不存在）时空等满 TimeoutMs（5000ms），且回退文案误用 `controller response timeout for requestId=1` | low | Decide 等待循环轮询 `HasExited` + `ExitDrainGrace=100ms`（:20）排空在途应答；仍无应答即 `controller process exited without a response (command failed to start or crashed)`；ClassifyFault 新前缀归 launch | `09a-old-placeholder-recheck.log` 第 7-8 行=新文案且整跑 5.0s（旧版仅预检就 5s）；T4 19/19 |

未记 defect：HUD 启动假红（1.6，未真实可见，inconclusive）。

## 3. 复验命令索引（本会话实跑）

```text
EXE="C:/Users/Neco/AppData/Local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
OUT="D:/project/robot-simulator/godot/docs/qa-10-01"
# 08 占位符（空命令字段→占位符可见）
"$EXE" --path godot -- --settings-smoke --settings-tab 2 --capture "$OUT/08-settings-placeholder-fixed.png"
# 09a 旧占位符形式（修复后应快速失败+新文案）
"$EXE" --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json --capture "$OUT/09a-old-placeholder-recheck.png" --capture-frames 90
# 09b 正确相对形式 + 真 checkpoint（应预检通过）
"$EXE" --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json --capture "$OUT/09b-note-form-recheck.png" --capture-frames 90
# 10 RL 真驱动回归（复验）
"$EXE" --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json --auto-arm --capture "$OUT/10-rl-live-regression-f1800.png" --capture-frames 1800
# 11 启动表现复验
"$EXE" --path godot -- --scenario-path scenarios/wushu-ring-2026-mujoco.json --capture "$OUT/11-startup-recheck-f2.png" --capture-frames 2
# 测试
"C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1 --nologo --filter "FullyQualifiedName~ControllerPreflightTests|FullyQualifiedName~RlControllerBridgeTests"
"C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe" test src/Sim.Tests/Sim.Tests.csproj -m:1 --nologo
```

## 4. 残留风险 / inconclusive

1. **『应用设置』按钮点击落盘**（1.3）未无人值守执行：settings-smoke 不注入输入、30 帧即退；由单测与读码背书。真 UI 点击属人工残留。
2. **HUD 启动假红**（1.6）在热缓存下 <1 可截帧、frame=1 黑帧，未能证实也未能排除；若关心，需冷缓存/慢机人工观察。
3. **桌面实时驱动按墙钟、不可位对位复现**（spec index.md:46-48）：复跑比分 3:0 @66.9→67.1s 的微差属预期，不作为回归判据。
4. **runner 退出摘要不出现**：桥 Dispose 走 `Kill(entireProcessTree)`；「无 fault」判据=HUD 绿字 + runner stderr 无 fault 行（本会话成立）。
5. T5 全量唯一 skip 为既有 `MujocoScoreEdgeGuardTests`（与本改动无关）。
6. 「非空转」以位姿差异+比分（推增益 3）+MANUAL 状态为证据，无逐 tick 数值级证明（桌面通道不产数值日志）。
7. 证据目录 `godot/docs/qa-10-01/` 为未跟踪文件；如需入库须显式 `git add`（本轮按要求未执行）。

## 5. 设置还原声明

- 验收开始前核实：`wushu-ring-settings.json` **不存在**（ls/find 均无；无 .bak 必要）。
- 两轮验收各以 `godot/tmp/qa-10-01-seed.py` 写入 external 种子（变体：绝对命令 / 不存在命令 / 旧占位符形式 / 正确相对形式）。
- 第一轮结束与复验结束后均 `rm` 种子文件；复核：**文件不存在**、无 `.qa-10-01.bak`；用户目录其余内容未动。
- 全程未 `git add`/`commit`（`git status` 仅见修复方改动 + 未跟踪证据目录）。
