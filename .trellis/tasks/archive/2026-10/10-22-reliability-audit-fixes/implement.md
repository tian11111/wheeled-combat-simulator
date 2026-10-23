# Implement：可靠性审计修复执行计划

# 执行记录(工作流 10-22-reliability-audit-fixes)

## 批1 R1 回放时序
- 方向: 实测推翻了 design §0/§1 的推荐方向，按 implement.md 1.1 的'以实测为准'裁决改为引擎侧根因修复，未采用'三处回放侧同步事件帧'。取证（tmp/probe-r1/Program.cs，mode 矩阵写入 tmp/probe-r1/matrix.txt）：在 tmp/audit/mj-30.json 上 base/sync(design 推荐)/post/shift 四方向全部 FAIL，只有 strip(剥离 commands) PASS；10 种子对照 base 与 sync 结果完全一致（6 个 FAIL 种子在 sync 下仍 6 个 FAIL，且首个分歧点从 Restart 事件 tick 差 1 变成缺失 FSM'翻覆超时→自动重启'事件）——证明'一帧 tick 偏移'不是根因，sync 只是把分歧点后移。真正的根因有两条：(A) Fsm.cs:422 在 StepSimExt 内部触发的自动重启，经 MatchEngine.cs:495 RecordCommand 被录成 restart_robot:<role> 命令；回放时 Program.cs:259 在 Tick() 之前又 ApplyCommands 一次，导致同一个重启被施加两遍（录制侧一遍 + 回放侧一遍）且提前一帧 → 轨迹分叉。剥离 commands 后 10/10 PASS 直接印证。(B) 命令在 StepSimExt 外调用时 _events.Tick 是上一轮残留值（TickIndex），而 RecordReplayTick 记 TickIndex+1，事件戳与命令行差 1（legacy fixture 实测 299 vs 300）。修法：(A) 给 RestartRobot 增加 recordCommand 开关（public 签名不变，新增 private RestartRobotCore），仅 FSM 自动重启回调传 false——自动重启可由同一动作流重放自然重现，不需要也不应该作为外部命令录制；(B) 新增 StampCommandTick() 在两条外部命令路径（RestartPenalty / RestartRobot 且 recordCommand=true）的 Emit 之前把 _events.Tick 设为 TickIndex+1，使命令事件戳 == 命令记录 tick（design §1 的统一语义），StepSimExt 内调用时该赋值是 no-op。三处回放消费方（CLI Program.cs / godot MatchSession.cs / godot ParityCheck.cs）实测已同序且修复后全部 PASS，按'修改最小化'纪律未做无谓改动；这一点偏离了 implement.md 1.2 的字面要求，但符合其'按实测选方向'的前提，已在 blockers 披露。
- 自检: 1.1 取证（自跑）：tmp/probe-r1 探针在 tmp/audit/mj-30.json 复现 315 vs 316 分叉（base: replay 21|315 vs record 21|316），并跑 base/sync/post/shift/strip 五方向矩阵 → 仅 strip PASS（3:3, 46/46）。10 种子矩阵：base 与 sync 均为 6 FAIL/4 PASS，strip 10/10 PASS。完整输出落盘 tmp/probe-r1/matrix.txt。 / 1.2 方向裁决（自跑）：实测四方向全 FAIL 后按实测改为引擎侧修复（见 approach），未改三处回放消费方。 / 1.3 MuJoCo 10 种子（自跑）：replay-record --seed 1..10 --scenario scenarios/wushu-ring-2026-mujoco.json --duration 120 后逐个 replay-check → 10/10 PASS（原审计 FAIL 的 1/4/5/8/9/10 全转 PASS）。同时核对：重录后的 10 个种子与 tmp/audit/q-*.json 的 eventFingerprints/finalScores/ticks 逐位相同，差异仅在 header.ticks 里不再有伪命令行（审计 cmdrows 2/0/0/2/6/0/0/7/1/4 → 全部 0）。 / 1.4 legacy 影响（自跑）：replays/seed-42.json replay-check PASS（3:9, 211/211，逐位不变）；replays/godot-parity-seed42.json 与 src/Sim.Tests/fixtures/godot-parity-seed42.json replay-check PASS；重录 --seed 42（legacy 默认场景）与现 fixture 的 211 条指纹完全相同，仅 header.coreVersion（1.0.5→1.0.7）与 createdAt 不同，故未替换该 fixture（保持逐位不变）。restart-replay-seed42.json：删除后由 RestartRobotTests.EnsureFixture() 重生成，与 HEAD 逐条 diff 仅 2 条：#40 事件 tick 299→300、#74 事件 tick 599→600；header、scenario、ticks、finalScores、doneReason、91 条指纹数量全部不变。 / 1.5 新增回归（自跑）：src/Sim.Tests/ReplayCommandTickTests.cs 4 项全绿 —— MujocoAutoRestart_IsNotRecordedAsCommand_AndReplaysBitForBit（seed1，断言确有 Restart 事件且无命令行）、RestartPenalty_CommandTickEqualsEventTick_AndReplaysBitForBit（400/900 命令行 == 事件 tick）、ExternalRestartRobot_CommandTickEqualsEventTick_AndReplaysBitForBit、NoCommandBaseline_StillReplaysBitForBit。 / 1.6 桌面侧（自跑）：真实 exe C:/Users/Neco/AppData/Local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_.../Godot_v4.7.2-stable_mono_win64_console.exe --headless --path godot -- --parity-check，三个文件全部 PASS：replays/godot-parity-seed42.json（3:9, 2400/2400, 211/211）、src/Sim.Tests/fixtures/restart-replay-seed42.json（6:12, 2400/2400, 91/91）、tmp/probe-r1/seed1-after.json（MuJoCo 6:6, 2401/2401, 236/236）。Godot 先 dotnet build godot/GodotSim.csproj -c Debug 0 警 0 错。 / 1.7 全量（自跑）：dotnet build RobotSimulator.sln -c Release → 0 警告 0 错误；dotnet test src/Sim.Tests -c Release → 843 通过 / 1 跳过 / 0 失败（总 844；基线为 838 通过 +1 跳过 +1 已知并行抖动 TrainingResetPerformanceTests，本次该抖动未复现，新增 4 项测试计入 843）。 / 确定性复核（自跑）：同参数连续两次 replay-record seed1 的 MuJoCo 回放，除 header.createdAt 外逐位相同。 / 行尾/格式（自跑）：MatchEngine.cs 与新增测试文件 CRLF=0、无 BOM、以 LF 结尾；git diff --stat 仅 3 个文件 29+/5-，fixture 为单行 JSON 整体替换（与 HEAD 同为无结尾换行的 LF 单行），无 CRLF churn。 / 未跑：工作流统一门禁（world.run），按要求留给你方独立跑。
- 门禁: 全过
- build exit=0
- dotnet test exit=1 :: 失败!  - 失败:     1，通过:   842，已跳过:     1，总计:   844，持续时间: 33 s - Sim.Tests.dll (net8.0)
- 性能门隔离复跑 exit=0(并行抖动按项目既有口径处理)
- seed 1: exit=0 PASS
- seed 2: exit=0 PASS
- seed 3: exit=0 PASS
- seed 4: exit=0 PASS
- seed 5: exit=0 PASS
- seed 6: exit=0 PASS
- seed 7: exit=0 PASS
- seed 8: exit=0 PASS
- seed 9: exit=0 PASS
- seed 10: exit=0 PASS
- replays/seed-42.json exit=0 :: replay-check replays/seed-42.json: scores 3:9 (expected 3:9) events 211/211

### 基线变化
- 1.3 MuJoCo 10 种子（自跑）：replay-record --seed 1..10 --scenario scenarios/wushu-ring-2026-mujoco.json --duration 120 后逐个 replay-check → 10/10 PASS（原审计 FAIL 的 1/4/5/8/9/10 全转 PASS）。同时核对：重录后的 10 个种子与 tmp/audit/q-*.json 的 eventFingerprints/finalScores/ticks 逐位相同，差异仅在 header.ticks 里不再有伪命令行（审计 cmdrows 2/0/0/2/6/0/0/7/1/4 → 全部 0）。
- 1.4 legacy 影响（自跑）：replays/seed-42.json replay-check PASS（3:9, 211/211，逐位不变）；replays/godot-parity-seed42.json 与 src/Sim.Tests/fixtures/godot-parity-seed42.json replay-check PASS；重录 --seed 42（legacy 默认场景）与现 fixture 的 211 条指纹完全相同，仅 header.coreVersion（1.0.5→1.0.7）与 createdAt 不同，故未替换该 fixture（保持逐位不变）。restart-replay-seed42.json：删除后由 RestartRobotTests.EnsureFixture() 重生成，与 HEAD 逐条 diff 仅 2 条：#40 事件 tick 299→300、#74 事件 tick 599→600；header、scenario、ticks、finalScores、doneReason、91 条指纹数量全部不变。
- 1.6 桌面侧（自跑）：真实 exe C:/Users/Neco/AppData/Local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_.../Godot_v4.7.2-stable_mono_win64_console.exe --headless --path godot -- --parity-check，三个文件全部 PASS：replays/godot-parity-seed42.json（3:9, 2400/2400, 211/211）、src/Sim.Tests/fixtures/restart-replay-seed42.json（6:12, 2400/2400, 91/91）、tmp/probe-r1/seed1-after.json（MuJoCo 6:6, 2401/2401, 236/236）。Godot 先 dotnet build godot/GodotSim.csproj -c Debug 0 警 0 错。
- 行尾/格式（自跑）：MatchEngine.cs 与新增测试文件 CRLF=0、无 BOM、以 LF 结尾；git diff --stat 仅 3 个文件 29+/5-，fixture 为单行 JSON 整体替换（与 HEAD 同为无结尾换行的 LF 单行），无 CRLF churn。

## 批2 R2 写超时
- 方向: 按 design §2 R2 裁决: Decide() 的观测写从同步 StandardInput.WriteLine 改为『专用常驻写线程 + 单在途 WriteJob + TaskCompletionSource 超时等待』; 写不进去(--timeout-ms 内未完成) → Fault("controller stdin write timeout") + RobotAction.Zero, 不杀进程; 上一帧写仍阻塞时新 Decide 立即按写超时记 fault, 不排队第二个写。写与应答各自计满一个 TimeoutMs 截止(慢写不挤占既有应答预算), 非正 timeout 按 0 处理避免 Task.Wait 异常; Dispose 用 1s 有界 join 而非无限等待。读循环/ExitDrainGrace/requestId 匹配/垃圾行丢弃逐字节未动。回归用 EchoController 新增 stall/stallonce 模式(Windows 可用、无需 python、可可靠 Kill 整棵进程树), 单测不依赖 tmp/。
- 自检: build: `C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe build RobotSimulator.sln -c Release --nologo -v q` → 已成功生成, 0 警告 0 错误 / 新增回归单类: `dotnet test src/Sim.Tests -c Release --filter FullyQualifiedName~ExternalControllerBridgeStallTests` → 5/5 通过; 连跑 3 次均 5/5 / 桥相关测试类: `--filter 'ExternalControllerBridgeStallTests|RlControllerBridgeTests|ControllerPreflightTests|BatchCommandTests|DesktopLiveDriverTests'` → 56 通过 0 失败 / 全量: `dotnet test src/Sim.Tests -c Release --nologo` 连跑 6 次 → 3 次 848 通过/1 跳过/0 失败, 3 次 847/1 跳过/1 失败(TrainingResetPerformanceTests.HotResetP95_IsAtMostHalfOfColdCompileP95); 该类隔离复跑 2/2 通过 / 对照: 剔除本批新增测试类后连跑 5 次全量 → 3 绿 2 红(同一 perf 测试), 证明抖动与本批改动无关, 是 implement.md 前置已记载的并行抖动 / 手工复验 stall.py: `timeout 60 dotnet Sim.Cli.dll match --seed 42 --duration 20 --controller-us "python tmp/audit/ctl/stall.py"` → 0.69s 完成, ticks=400, faults(us/them)=395/0, done=比赛时间结束(手动模式), exit 0(修复前整场挂死) / 手工复验 slow.py: 同型命令 → 0.59s 完成, ticks=400, faults=400/0, exit 0 / 降级路径不变: crash/nan/garbage 三脚本各跑 match --duration 20 → 均 ticks=400, faults=400/0, exit 0(400 fault + 比赛完成) / 正常路径不变: `match --controller-us "python controllers/example_controller.py"` → ticks=400 score 3:3, faults(us/them)=0/0, exit 0 / batch 停读控制器: `batch --seeds 1,2,3,4 --duration 20 --parallelism 1 --controller-us stall.py` → 1.81s 完成 4 场, 每场 faults.us=395, 全部 completed(Dispose 不挂) / 孤儿进程: `powershell Get-Process EchoController` 与 `Get-CimInstance python.exe (CommandLine like *audit*)` → 0 残留 / 行尾: python 逐字节统计 4 个文件 crlf=0, 全 LF; diff numstat 桥 125+/3-、fixture 18+、文档 6+、新测试 181 行, 与实际改动一致
- 门禁: 全过
- build exit=0
- dotnet test exit=0 :: 已通过! - 失败:     0，通过:   848，已跳过:     1，总计:   849，持续时间: 34 s - Sim.Tests.dll (net8.0)
- stall.py 5s 场有限时间完成: exit=0 :: seed=1 ticks=100 score 我方 0 : 0 对手 done=比赛时间结束(手动模式) faults(us/them)=95/0 penalties=0/0
- example_controller 正常路径: exit=0 :: seed=42 ticks=600 score 我方 3 : 3 对手 done=比赛时间结束(手动模式) faults(us/them)=0/0 penalties=0/0

## 批3 R3 duration 校验
- 方向: R3 在 CLI 层做前置校验：match/replay-record 的 ParseOptions 用 InvariantCulture 解析 --duration 并要求有限正数（失败即 exit 1），BuildScenario 再按场景 tickSeconds 把秒换算成 ticks 与 MatchRunner.MaxTicks 比较（超限即 exit 1）；batch 走同一套判定但抛 BatchUsageException 落到既有预检约定 exit 2。上限判定只在 CLI 层，Sim.Core 的 MatchRunner 循环与内核 API 未收紧；非法/超限都不再静默回退 120s 或静默截断。
- 自检: build: `dotnet build RobotSimulator.sln -c Release --nologo -v q` → 0 警告 0 错误（自跑，未跑工作流门禁） / acceptance matrix match（自跑，`dotnet src/Sim.Cli/bin/Release/net8.0/Sim.Cli.dll match --seed 42 --duration <v>`）：abc/12x/""/-5/0/NaN/Infinity/1e400/-0 → exit=1 + `error: invalid --duration '<raw>' (expected a finite positive number of seconds)`；1e9/600 → exit=1 + `error: --duration '600' exceeds the 10000-tick replay/match cap (500s at 0.05s ticks); shorten the duration`；60/60.5/1e2/500 → exit=0 / 逐位不变（自跑，对照 HEAD 29dfc9a 干净 worktree 构建的同参数输出）：match 60/60.5/1e2/500 输出行与 HEAD 完全一致（ticks=1200/1210/2000/10000，score 我方 3 : 9 对手，done=比赛时间结束，penalties=0/0）；batch 四个值的 resultFingerprint 与 HEAD 逐一相同（60→f7c6a162…、60.5→d90a1561…、1e2→7c5b6fbf…、500→e5772e24…） / 修复前基线复现（自跑，同一 HEAD worktree）：match --duration abc → ticks=2400 exit=0（静默回退 120s）；match --duration 600 → ticks=10000 done=(未结束) exit=0（静默截断）；batch --duration 600 → exit=0 doneReason=(未结束) —— 与审计描述一致 / batch acceptance matrix（自跑）：abc/12x/""/-5/0/1e9/600 → exit=2 且 stdout 0 字节（零 JSONL）；60/60.5/1e2/500 → exit=0 且输出合法 JSONL 行 / 多 seed 不产部分输出（自跑）：match --seeds 1,2 --duration 600 → exit=1 stdout 空；batch --seeds 1,2 --duration 600 → exit=2 stdout 0 字节 / 上限按场景 tickSeconds 换算（自跑，临时场景 tmp/dur-tick01.json 已删除）：tickSeconds=0.1 时 duration 1000（10000 ticks）→ exit=0、1001（10010 ticks）→ exit=1 且错误文案为 `(1000s at 0.1s ticks)` / replay-record 同受校验且失败零产出（自跑）：--duration 600 → exit=1 且未生成 --out 文件；--duration 60 → 录制成功且 replay-check PASS（119/119 events） / 3.3 存量 grep（自跑）：`grep -rn -- "--duration" tools/ controllers/` 全仓库命中 8 处 —— tools/param-optimization/oracle.py:46（值来自 optimize.py:61 default SCREEN_DURATION=60）、tools/param-optimization/optimize.py:61、tools/sensitivity/selftest.py:64（"120"）、tools/sensitivity/sensitivity.py:150（值 DURATION=120.0，sensitivity.py:69）、controllers/score_block_rl/profile.py:339（"120"）、controllers/score_block_rl/diagnose.py:849 与 :868（default 120.0）、controllers/score_block_rl/gym_env.py:90（值来自 duration 参数，全部调用点 train.py:205/270、evaluate.py:564、profile.py:419、selftest.py:889/899 均为 120.0；diagnose.py:229/661 透传其 --duration default 120.0）。全部 ≤120s，无一超过 500s 上限；其中 oracle.py/sensitivity.py/selftest.py 走 `match`，gym_env.py/profile.py 走 `rl-env`（独立解析路径，本任务不动），diagnose.py 的 --duration 为脚本自身参数 / replay-check 三基线（自跑）：replays/seed-42.json PASS（scores 3:9，events 211/211）、replays/godot-parity-seed42.json PASS（3:9，211/211）、src/Sim.Tests/fixtures/restart-replay-seed42.json PASS（6:12，91/91），均 exit 0 / godot --parity-check（自跑，真实 exe：C:/Users/Neco/AppData/Local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64.exe --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json）：PASS，scores 3:9，ticks 2400/2400，events 211/211，exit 0。注：跑前先 `dotnet build godot/GodotSim.csproj -c Debug`（0 警告 0 错误）确保加载最新 Sim.Core / dotnet test 全量（自跑）：`dotnet test src/Sim.Tests -c Release --nologo` → 848 通过 / 1 跳过 / 0 失败（849 总计，32s）；第二次全量复跑出现 1 失败 TrainingResetPerformanceTests.HotResetP95_IsAtMostHalfOfColdCompileP95（implement.md 记载的已知并行抖动），隔离复跑 `--filter FullyQualifiedName~TrainingResetPerformanceTests` → 2/2 通过；同一抖动在干净 HEAD 29dfc9a worktree 上同样复现（838/840，1 失败 1 跳过），故与本批改动无关 / 行尾（自跑）：`grep -q $'\r'` 检查 Program.cs / BatchCommand.cs / MatchRunner.cs / docs/CLI.md 全部 LF；`git ls-files --eol` 显示 i/lf w/lf；`git diff --stat` 为 4 文件 +82/-12，与实际编辑一致，无 CRLF churn
- 门禁: 全过
- build exit=0
- 非法 duration 全部拒绝: --duration 'abc': exit=1, --duration '12x': exit=1, --duration '': exit=1, --duration '-5': exit=1, --duration '0': exit=1, --duration '1e9': exit=1, --duration '600': exit=1
- 合法 60s: exit=0 :: seed=1 ticks=1200 score 我方 9 : 3 对手 done=比赛时间结束 faults(us/them)=0/0 penalties=0/0
- batch 非法 duration exit=2(期望 2)

## 终检
- build exit=0
- 非法 duration 全部拒绝: --duration 'abc': exit=1, --duration '12x': exit=1, --duration '': exit=1, --duration '-5': exit=1, --duration '0': exit=1, --duration '1e9': exit=1, --duration '600': exit=1
- 合法 60s: exit=0 :: seed=1 ticks=1200 score 我方 9 : 3 对手 done=比赛时间结束 faults(us/them)=0/0 penalties=0/0
- batch 非法 duration exit=2(期望 2)
- 终检 dotnet test exit=1 :: 失败!  - 失败:     1，通过:   847，已跳过:     1，总计:   849，持续时间: 32 s - Sim.Tests.dll (net8.0)
- 性能门隔离复跑 exit=0
- replays/seed-42.json exit=0
- fixtures/godot-parity-seed42.json exit=0
- fixtures/restart-replay-seed42.json exit=0
- godot --parity-check exit=0 :: PASS: Godot shell reproduces the CLI-recorded match (score, done reason, final tick, event fingerprints).

## Blockers
- 【需裁决·方向偏离】implement.md 1.2 字面要求改三处回放消费方（CLI Program.cs / godot MatchSession.cs / godot ParityCheck.cs），但 1.1 实测证明该方向（sync）在 6 个审计失败种子上仍全部 FAIL，故按'实测推翻推荐方向时以实测为准'改为引擎侧修复，三处消费方未改动。若你方仍要求三处代码上有可见改动（哪怕行为等价），请裁决；当前行为一致性已由 3 个 --parity-check 实测 PASS + 全量 843 测试覆盖。
- 【须知·协议语义】修复后 MuJoCo 回放的 header.ticks 不再包含 FSM 自动重启产生的 restart_robot:<role> 行（审计 10 种子共 22 行伪命令消失）。这符合'自动重启由动作流自然重现'的语义，但意味着：修复前录制的含伪命令的 MuJoCo 回放（如 tmp/audit/q-*.json）在修复后仍会 FAIL（实测 6 个全 FAIL，分歧点为缺失'翻覆超时→自动重启'事件）——这些文件不入库、属缺陷期产物，应作废而非兼容。若你方要求兼容旧 MuJoCo 回放，需要另一条迁移路径，请裁决。
- 【须知·未做】CoreVersion 未升阶（仍 sim-core-1.0.7）。MuJoCo 回放身份校验会比较 CoreVersion，而本次改动改变了 MuJoCo 回放的录制内容（不再录自动重启命令），按项目既有惯例（09-28/09-29 任务均因行为变更升 CoreVersion）本应升到 1.0.8。本批清单未列此项，我未擅自改；若升阶，需同步 MujocoIntegrationTests 的门禁样本与重启 fixture 的 header（fixture 重生成会自带新版本号）。
- 【须知·范围外发现】godot/src/DesktopLiveDriver.cs:299 的 live 驱动在 DrainCommands() 里直接调 _engine.RestartRobot(command.Role)，同样走 recordCommand=true；桌面 live 场次若按 R/T 再叠加 FSM 自动重启，录下的回放仍会含外部命令（这是正确的，因为是真人裁判动作）。但该路径录的回放在自动重启处同样会重现——本次引擎修复已覆盖（自动重启不再录命令），无需改桌面代码；仅披露该调用点未逐一复验（桌面 live 回放不在本批验收范围）。
- 全量 dotnet test 无法保证每次 0 失败: TrainingResetPerformanceTests.HotResetP95_IsAtMostHalfOfColdCompileP95 在并行全量下约 50% 抖动失败, 剔除本批新增测试后同样复现, 隔离复跑绿。implement.md 前置已将其列为已知并行抖动(rl-perf-gate-parallel-flaky), 但本批未改任何相关代码, 也无权把它改成必绿——如实上抛, 请主会话按既有口径判定门禁。
- 未跑 godot `--parity-check`: 批 2 清单 2.1-2.5 不含该门禁(它在批 1.6/批 3.5), 且桌面端经 ProjectReference 共享同一 Sim.Controller 桥; 本批未执行, 如实标注为未跑。
- 【范围外披露，非本批阻塞】`replay-record` 与 `match` 共用 ParseOptions，故 replay-record 的 --duration 也一并获得校验（600 → exit 1、abc → exit 1）。这是同一解析处的自然结果，且与「录制整场动作流」语义一致；但 PRD R4 只点名 match/batch，若主会话要求 replay-record 保持旧行为需另行裁决。
- 【范围外披露，非本批阻塞】场景 JSON 内嵌的 `field.matchDuration` 不受 --duration 校验约束：`match --scenario <matchDuration=600 的场景>`（不带 --duration）仍会静默截到 10000 ticks 并输出 done=(未结束)、exit 0（已实测复现）。本批按 R3 只校验 --duration 显式入参，未动场景校验路径；如需覆盖，属新增需求。
- 【门禁口径提示】全量 dotnet test 在我自跑时第一次 848/849 全绿，第二次复跑出现 TrainingResetPerformanceTests 并行抖动（隔离复跑绿、干净 HEAD 同样抖动）。工作流统一跑门禁时若该测试红，按 implement.md 记载的已知抖动隔离复跑即可，不是本批回归。
- 【未跑】工作流统一门禁（world.run）未由我执行；以上全部为自跑结果。另外未跑 sensitivity.py / oracle.py / param-optimization 等工具的实际端到端执行——3.3 只按要求做了调用点 grep 与取值核对，未实跑这些工具。

## 未提交(按纪律留给主会话)
- .mimosa/finding-ledger/v1/events/batch-posttooluse-71065b4fbc2ac89a966860eb74fc4f3f.json
- .mimosa/finding-ledger/v1/events/batch-pretooluse-2e2f436916663db95f5b08203289fad1.json
- .mimosa/finding-ledger/v1/events/batch-stop-20261022T115038273Z-43860-e81cb98d4d0a.json
- .mimosa/finding-ledger/v1/events/batch-stop-20261022T131405006Z-140324-6326dd9fac6b.json
- .mimosa/finding-ledger/v1/events/batch-stop-20261022T144243174Z-164564-2377576a8025.json
- .mimosa/history/run-20261022T115038273Z-43860-e81cb98d4d0a.json
- .mimosa/history/run-20261022T131405006Z-140324-6326dd9fac6b.json
- .mimosa/history/run-20261022T144243174Z-164564-2377576a8025.json
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.continue.json
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.json
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/00037b890b5f45a6b14ac1f3cbe7ff83ec144bccf79864b1acf5296b7c4e8999.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/005f113bc182fcc69e9d2eb483d3a2f296ebb7d6b53e0b1229837e87a7a28c6e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/008cd09ead64d782c80e2ad7ea7011ffe7852971993491a12e658e80e83a2b42.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/00d5f53f496d4eaadb0014c2e39b18ee9541e2d71603303a44f5df30e880a871.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/01d6104a214bc037255abb9bb26592f14e6e15a92e867256435559021bf84797.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/01e09a072fb1087d1ae522a9840d131dbb0bc54dca397f5bd9e4eac8c0feefca.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/01e33a9ed794edbf1db7a7dea59025dceeaa54d86684a073c6ea5169ac15f670.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/027684adfad2e584d7a6efbab0d5d71b230ca8c2fb532f14adf2c627d827e744.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0287d5ea62a64ff0db2105680652d04547c555b805d8922966e6c3e149868210.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/03fabc4a4dec12aa43936a3b491c0b52acc4e6b36b6527b9d10cc6653b647c45.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0513691bd0eff11eb7f3ea467deb20aac6de29b520b340751644c429bd8b8842.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0517198932ac7cebc0d5eaa1861272c54d0f4ef8dd2946eb912063123af99b4b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0523dc558f42bf7a91f774ef27657b35b97365a01d4ebe6ab36e64603bab7f32.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0528bba046ee5e39a2d6324034eb63bc275e5f80d1802488265858be1a6f2a8d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0572f7c0771d93f8932c306fab2c63316fc6d80ea5ee18649e38c579f5609e9c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/07d353651fcd3107945db9eb0d662f774cbfc77babd0275441ee2dd0a8ca883a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/08ff3686ae6fcb5dfceb14c3676cdf2da12ca79d7c9ca37e6200b581201d881b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/09e5eedc2c227f8c1bafa40f18a8219316c85e6584caa2d661d81b113f780af3.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/09f0ba0700c88729d7760121e3ac864f60311bbde4b5e03d7cfeaa37b13b917d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0a7acd342a60910e2296d0bbae99dee766ba0e89c52a009505d50907d907fa49.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0a8b5c2589eda3aec7bf39db2991e4ddf3d2108c98f121bfd7da38a044a74d98.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0a9070740ec0b0c8529c83ddd1ea2dd06a05cd5ad92dd67be8514ebbbe1a5e6a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0ade101def49f9cfa003c18287e7c5df9f7a798b18c3362ebbcdfa1019b0dc25.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0ae2135a50e04da503968d97adbb160aeffeb6467d97abe51dff566214bccfe3.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0af52d051b23d1df9451eb9e46edc95254be5e12a53396b3420c073a6c4e8b2a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0b2ccd84c706ba13cc155510106aa589f5347c0063cbc5a044cbfbea5eaae51c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0b91d33fcd8c7da345eb6d5c36f3c05c114313154717ad0cc5f0b377d299762e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0ba0e220de22857f8f2290f8d5652e6ec52b3e8e6460ab08a0d0fd12ddb7469d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0bbc682c84b433104df3e5723347c8a9dd5f8bc012572f243769438a4b741d92.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0bc70d0223c2250ff577ec1453d852f7533fd2897ed0cd0a85475c7f21aa6f8e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0c7a330be308a1cba3f00121539021a65ec599a8f6fd430e7433baf39dcd5712.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0d3d57fe834acf374d4359d020390056c4a45659cf6d5889a3e98c7f1f94cd2c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0e065d333ca4b26a2deab1ba7b7e5a075121f4183396c84e1b63ad8aeb514d9e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0ecdf2027c61fb37aa3b597fb2087c1bf42d79053f028b1235e141bf3f56898a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0f4141550000cf9a725be878728d5758ac6a4b708e10c7a261bf705ceb2a93fd.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0f99a2a9e5b4f4d4ae8e500d94eec56fc8066b734a54c2a4776b2ed1da7cbb06.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/0fa825ede75d37bd820f46de01cfff92938f0d3dfe6dfae32cef1f638c27a102.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1014583569f89051a73b8bf183ada0da6112d11e54667c719c44b8816ad0a17f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/10221e5737061912bdc287b5a7f45ff4f90affd7a556dc5764e861859c751a8b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/115c765cc624be70805443e73a6c0c7561c21a362df16d37239d684cf11f9af0.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/11a5f681a6443d3c14d6e23627fd43acbf98e2e96a91e41d9f4e1ba5815ee5d5.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/129bd77abc4fb1b3c94fe2edb92e06fc3e893217188402c30c6b190f31f9c570.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1401ca0a7b9828cfda889b3b7d93879c0dcf854efb7a07eee2bb563874ef0856.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/14fee7fe15d100650b5b34e8a626a664616761d5e18aa61032aecbe6e316cdf2.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1585773313554ad5dc3b320c0318a56b1d76d2b8bc26fe6d6bc078558fa37a59.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/159d01ac68e878a7b6b3b8b271f8660fac03d4b391cb25ab4859998157a54c7d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1667d6f117a0470080e917dc5732b8c55b796255f0ba9b7fde506ad48d1783c7.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/166be95ad8f32462cd90ffd5e4db81416b0bf82e6156c9eb20a3012ba8fa54cc.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/17356773d2e9c8761aa1e2531ee4885ce1b238ac45f9ce3437dbdd8e72cca768.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/179d3227a38b6eb5494a0546e21f5221c9c4aaa61cea1943d22342bdf71a8a38.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/17bc35f551c077b45b8f70a925acbf8ddbb7000968efbbf4f365de7e8988e06b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/17fb29fb97db31b7eea85c8c58fe4a661afe86d36671674885f04736faaf4f0b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/180d8e233074b8c26b8af079a8b3fa8cd26c92b93f13631b9779792046e4b54c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/181632ee843c51ba29af98b4e0171e94957cd87e6c95c808096c40959da9b230.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/18769e82ca57f16e6564f6db82854dd63816afdec2a491c7f07f220b945d0126.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1988d46dd8f6755a04373a5920be903559b48b6f42ec74156e42f4f1243ae0ab.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/19b20e6efb6b8714f44bdf178b5eb6eb803ac2a93ed0ec59175b283f97596ff0.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1acc60ab79cca9a1d8186d337e5e0663454423dbc775d45a8a45e23e35854752.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1b4f9ab93988d0ad01ed77d8cda7c3bf5a307169855a7638c221471d08c03806.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1be8d2a056061955f55c042bf633f1854c8d3d09bbb7c46bee30559bf7dc9fd7.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1bf01d20b3b71874e923f8be1b2544e9c9d4a50628d9f67a06c970686bb9bd36.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1d26bbad582bb8fdc177a632162e7c94fe3fd4f8457d2e5a6660c2b2e9a50d95.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1d36c5279e1964e326ee39964a5c07f9488ecd8158c6920a58758657139e00fd.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1d4484a79552f94cf1c94e60e98b7cc583e8ea2af5d3580737fb0618b02f526c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1daee776d02f5a7da4600063988c76509b2d18e0a24ef779f2e5caee94cbaa9f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1dbf8c04419900cc16075e006d818f7b7e3597915d87b1a59dbb5ccfa3d244ee.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1de4e857f73acf54707e92cd2c6b0777a68b724b74924fb1671dee164869d45e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1e1dc912354ab610b3464fc8908785a787cd59610a65e15a7876d7a0c1d65f9a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1e1e37a3879281a4b67bfa4020a8c7cd8554101e9edd217b65b351ae606053db.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1e5ce8bee71dd5208c726291cbcd1d2aeeda0ad713d73c10c249af9093067b44.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1e8117f6a05d90ef500daf2281fd7b0574aeeb92db45047ae5b518ad5d9fc169.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/1fe69b1e0827f535a29d2b02fcf46109da85d0d6c5828c517db1e2a805d5753e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/20809a9484c01cf7c466d05699114da2afcf0832d4bd0a46a5d234ca0a10e4a1.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/210ba8e0cbbee0280e2a9976ec1b1780b02eb30a6375e5c531cdedc889721861.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2149b802df3659a15d1758f16ed97ff40059ad596f80e48fcca4f106bad60a0f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/224c1606ed46e323dc69f3227bea41a9182a6f36f6068e0de130d92eca642deb.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2254d43deb9530ba1315cbad49518f55ea6841aeba1267c03fa15e34265c2b5a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/22b32271ea0c90afe8e4f4536e034a3ff33407e95747c95d282effc9de30aff1.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/22ca9ddbb5dcc7d08778595d2138a7ae93429b4a04c6b9dc24219d75751e76fe.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/24bea2b64f180f92a541002b6946fc5d967d62ab890d432ced48298165d503e5.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/24f18eae1c1abd1ae58ad30b508d4473dd11b66161e2b7ddfefb3f41cd14477a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2559d6f89949016df69ecd200cf85c543b494081c0357dc009b46d295cd79131.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/255b1052204651b52e4b67068ddb2cc2f33d515cba8dfa96878a13deb586b4cd.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/260adc425f92df39343835240d7bb7bd78f21b559fc3b1c9aa33def233249967.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/266668b8f87d8e39d1c8674af04d869a8800c7d6142b0f0a480d5f70142e919b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/26a9d056e9da9ac53e6881418a9606445e1a40f7cd45bfcffd915052f30ef9b6.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/270c3c57700c236d68e62e74e79f9b62adecaa053405a86eb79ad60f6b661b5f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2798cd0e8c713ceadb7184542d731d55111db3f947f76f67c9ba364fd92350cf.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/27f4b7946d02800b3213bfddfca10b79a1a15f80e595fd5e576feab433155714.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/28570b2db50d1a134387f8ff124d14c377d6e9d78d174419e0e646bb05104ae8.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/28ada0f3bc6f03b56ec368f0032b880cae02180061e3e23ce68a424f8cc1748b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/29b999c90c69b103b5b6071ae8e24f1e5756b0cfc4f43646cbc3aee73ccca63c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/29d53dede4a7809fd66359827979335617d76837af9c110f9f5bf0465ad23032.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2a2c337996292bdb769830ad18ffd904270e649cb1b6c5c3ef9404846bfe2558.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2a53a1f866bb7af988c60449710ed1d48176efbfae991148c69a1cdb61ae52c5.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2acd280e465b89cad7f81810cca4dc5c8de85662c89a4c41d0a12be33536834b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2aeb47e9335c640fc56dbfd7b05f7f9dc1e4c9cb517a8fc853522d0f6693d3ee.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2b70b4b7d31e1b8557bad2a11f6dbbf8959ec00e7c4eceae4bde44f6e6d7e879.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2b9a639bf81ce366b0131fe399d967be782f9b83432a5d6d8610dd104ada847f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2c09e3ee89d57504c6dcddf34d09bd7e8b6eb3c40daaf149763e41859ad62bc5.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2cfe05664013dba774239af7ada81784e02e51e864ed57b5288532c73c241583.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2d0cd591c02a316c8f6678b7a0b48ccae6915f3a78881c0f8d65e84b4c02580c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2d20adc5f4f67f7fd5f0d3a2b06c6ad7db6fbd46695c09011e50b92851652559.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2d3910301e3d906e6670408f8e46e7ec04648f88711ab18bd49d73a6129214c1.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2d52564475e9ef563c7612631ffbdfe2d06bec4895a31e373362b143eb6c89fd.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2de5ccec35446cfd1c3c5f9345a1bb9c2dab682edf83f85104b963ee907bce85.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2e3dc05fc02262bb309f094bf9200e70ec1f230c7e3260b5e185ee6f5cebb364.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2e50e18e38e1760e9f59e2e624526b85e8d2b7d0003a556822df49c698de12d9.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2e536e474e60b7a10519e976cf9d1200435697b965d00c0b7b56fa960e63e28d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2e8c99d365b4b61237ac6e3f5dde5311b2b3d85b4106d57d46b56570b1255d95.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/2fd48c3217d772aceb44c0982f1dcb9d97b22b315215d6166136127ccea60d72.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/31854509ad164c1ce400c18f70a40aff550c4247072e8a70af5ee2fd03be9e4a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/322c732ea9612ffc3150327c4ae889bd74a2f242e1e15223c0c62a9c9cc0ac8e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/324968d765420947706196af91263c11ee88ee808c9742084b5e5dc2039eef1e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/324f81ba8bab5af0102553721634cabf6c9897d23e719c0af3865dd13ca087a2.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/32a5abdede6198bab6b1f78cd7eeb9e9ff07ce29751dd20d5f93c3c765f40fe5.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/341d2c04076ed372e2644e3da5aa6972f2139a966fe5ec658bf11a09a282bba4.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/342ecb2aea9776c6c0aad5b382e7df7abe88a6d76c834116c8e566806c4d0971.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/346e155265a1a4fbeed5537d1ff0fdcd192b818400b286c6acbbb1503e6f7631.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/348b90935636a6f9191d505037370f86a3d4d2df477a5fa5915e5dd0145b93e5.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/351062c07d5acf494d33809da315e765af0ab248cfc93a2780db8ad2d592988b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/35544726c79d00dc8825efab23657595b80b40eb9084dbdad05a33adb767f972.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/356c1488d02bf75d7ab37e2a50667ad6d53811596034f0de2d3ba9c8ee30e5be.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/35986086200a82eb813dd71cb5632df74f5555b1bdaaa8db8482a0d4a4b69b62.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/36b1dc12f430585455deb33afb0119f5d64c8cf865d00840ef9d31f4d8b84084.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/370fcf09fe0d8ee792f0368c8cc99f5e079490c180e7156c314bc6080db89e98.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3730913d9f9882d9eadaa01541a5630aa0f9527b6041d740c19c83d4fdd38fbf.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/379009f4e5d6bf876bae38ecac7776d96991a91549253279a6e2752ccfbbef41.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/37c649633243238351184efce19286a53e1fba827decd150fa1826a7fcfc571f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/37ca47e79bfbaf88180094096d34350a4a71cb0b5a8d450a98520bb026fac892.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/37ec6475cdf0e15b3e615f848b1d75c166a74f301f25f9de850a65192f82cc21.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/391dd5ebe613f3886b12b69df563cfeeddb59d3b792aa006349c71ee5a1c049d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3941d911b44c8e856e07050fcbd1ef9a317b4ee4b570c0090cc1a41eeae06df0.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/39422b38d2a8b47ba36f0413b9a603f209d3bf2d5f23f2255a4283967fa5bdc3.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/39efbefc6c7f2ffeeef947d01b65c29e1b95673f8dfeb0e7dae55b7f5450e18c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3a410260749a90e6d66a76537ff66f4532ba328ee5d71bd980f588fd40e4201b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3a60a27e58e9cd8ae1641cf8bd8ecce50a4209675e223d6819e17481f41d3cff.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3b0889726651bc7c16d24fa3431031b573d0d3fa5c104cfcf1985aa7cc10e572.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3c30d0cb8699d6574863ba225efabef1190a08e053258e4fb6d2352a61f1daa3.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3c4ded0444ffaa6aa00e16a7b7d8a60adefd9c9a24aa3f41e0ea2de9fc855bb6.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3c68d632a6699f1a80f2e075419aba09bc77526850cb5dcee9daf51002c1e483.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3c6eb320c4f9618510be8351b014e4e4cd98d34af0be51ffbfd8285db4509226.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3d09821bb5c1c18cabb1681715a500416ebe6863a450309421a6d7bc664bada4.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3d5e6bea271bd25de0714bd0908bf2feb9dae40df2babc44df1042c8a7e9cb3c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3dadfba727128cc84de657a9b0452098cb96df792873f3500cb0d8501cd9a63c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3e6d72b6a4e7284559636442c01aaafc7a4b64bcbb60a2c774f0ac9d21946954.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3ee122044009b6f31a3504a768898fbac076af9b69a0811ecb64cc5175ac7017.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3efe7f6221257967d8002e30033644526186d47d9c2440659b0ded8329109d9b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3f73e1f1dd569c00f7d4a19387d5cb8eb65801cc1e0e5819443efa81b689266b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3f8997e22b321df1da084c5b85277fda4d6ac214a11846250f1ccd4cdc8bc7ba.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/3f8bc411a5a464700d32d54572ec9613a05b19e4ae360dd1a3c1fa0aa92631e9.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/401ecba1e829417f9efcba43b42649120c9d211a14fc19bdba167127bab85397.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/404607e00296648f2d93f6a37e822804580d91fc52f65640178facc80d59be60.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/405d07ff635380b02b49327565d7222b551eb53c35b7136219979a74980f646c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/416d1b651393900d0230caa4ceae92637195b0524124f7b31ed94da76a208d3f.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/41d56f9d535e10a24828a139f4c6ed2905c092d5a0d3dda2dcae97e099e45910.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/41ee0d4de4a4c07e383f4bdc2d6feba5715b62b0dc481709ab2e9665c4429f73.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4215b8b002643f721c33c72894360bba7bad98e0e75e62d916ac56dcaffe269c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4347223756a0f1071fa9760d089111e8be4a834eaf143e99e8ab984d4ec66a7a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/43f141a8dc4038f5c3ac891155592bf29d7f34a52be793df7a31e38685f0bf87.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/440bc65ede7fb6d0493e0f081719890ba7afbe19fa700b4edf85f5f8d645aea7.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4421a776463faa59b5fa61af229c9bce7eb6ca02009bc57c64d147f8c2e7ed95.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4452851d4ecb01ad1c2fc97dd0c2c82bb747e448a95446ddee895dca9d7eca60.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/44ee2b0263720865469aa0ed8db2f37b46effcde91143c76fb0ddaeac70a3bf8.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4596d4c98cb915e1473bc55b3d8bfc586fbf094fd0b5582cce438d0a67a94687.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/45bae09a30f33051391ba1a69fe7b85f2e727ea0379a249b01175f720884b228.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/472ff3503b4d5a2b9d1b4637bd7a8efe84303c24a4f8aa2e300a648cacaa62e1.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/47861ab82100d3cd4ba45939311f9611fa2dfce997262a225aee5ec911b768cd.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/47a7a6fde2d9433931c50ce73d2978198ce5dd2a63ba5a2120919f34d3c176c0.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/48f8a67c8e38c6bf446e53bfc4260677ed542aa630c5485399d5a73671cbb673.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/496598857df38963475e7ad92ec4d557f429086b05083ba0ef4cb9f38caef7ce.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4a57485f024cb19150e7070dff5c558272a7c3237f3fdd4c58bfccaeeb72fdd7.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4b1ca5e8fa7784d9db9b6ac5cb2f4558f6aa1b56a7d8160aa523e0f186f8677c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4b97362e16bee66f1bba4f26f2c57fc0013c312f7fb0ec3b826eacbaaf945df0.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4b9e58449db99ec63add256f884c96eb4e7ff23a65b204c905be8c40862faca4.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4ccc439cae6286ef2d751ec593d76f0a2f375b5b33cad7b1e7337dfc14c0f575.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4d39a68f59925bf4e67b2603f5d88414bb4385b781b7b04c926a784fdc370df9.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4ea5612582388e10843d466561ea2f7d2a6e70e08bcf3c2c3a4839ceb645eb7a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4eade037d65144c230ebd168bc0120c3fd8b147066c82613dee4118a428877cb.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4eebfd4f5706fbbe4f4a709716941354bb10416e47cc200237a876a4b31089de.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4f593caafe3f0091321266cac48ec132648d29578f9aeae7dca4c7f4119c65e7.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4f8b7d91739fdd082589f9be0715f476a2711c1d7f816be27aec998b2d76bb69.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4fbec625c71ca9c7732f6f7d2c1be3563fc698b5a3cd9e43f2accb502413bfda.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/4fd2bb7f583e652f2b695371452949b898d6ca4945edf779bb80f1554628c9c9.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/508d044069d90efe434664623d7db1a5f1292a82f405487f87522cb6a4cee87d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/50d346266700fd96a922795b40f904722bd9a4cbba7674d9cb968c68276f2233.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/514baa66c957850d032fd8b2fc74625fdd710c3ca9253b7773997cdf6e03bbe6.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/515613df591a84f12673edb82b35d88b927ca9400bd59979cbc5533f873e4d90.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/52356626850298356a9bcf04a2f8d31197b6bffc615bd09717da39844b5cd49b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/525a5a28ddcff3a2d2040daad3d4fc4591a3002b6f2de8604f2fe133f508b7fc.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/52d01cc5bb9a437b531e111fb8d96cd66bdff3fd49ef05a6136969d8c27665eb.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/52ed3552727b628077e9c3c0603f3130cdbd0f1762e1480730a88e8ea5616e2a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/531984657325fb871405e364fdca8764f58e35cd105e19ad09aea453e9224c9b.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/53bb86e7d887916362220e99791e387101d960070488b30ac4dd3b5b8bfadf9c.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/53f0b3daf423ba44bb7fec465c975c3066e6f4467c4f68dbfb6e063101fe0b22.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/543b21fd84dceef85773e50abddd553b2f26bb4115c40eb21666027b708ba4fa.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5464ed3f736ac6927056969b9148310ad29d608ef6fcadf7ec1f17134846465e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/54b8b7850aa04dbf741255ec3b7aa7330b84e641e2279c4627a5cf3e95f08c3e.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/554f0a7039058be986cfde79f439088375690501e641b9a7959b6b4b6190f265.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/558fcea8faaa277559b105cdb5cec340bdceac1a02ee394b4268d5a223e912d7.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/55e0ece41ae6df2d1a993ac559fbe9bf73c65acf8aaf7bae15089aa641a47f63.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/55ee7b616f3c8f47bd91f0d5ffd9a6518cebbdc7ca86eff5b705da6b4edd8b39.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/562ac90c6a4594017f3168b5af416120f4f665fb99b504d44fa930d6df7fb0fb.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/564447b6db50f32dccd593bd729f88005644689e5d63a317937dd747aa6858d6.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/56a3b5c128af4ff48cc118dfa9ccf4e197c34b0d1e0e498c0645b81d9ff4f659.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/56b7c94fee440d8d390d6bf9ba13335b696bd251e29b95a818b3f8342f08f4af.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/56d6a2791f0dd37cb2ee467cd235c77b930ace988fa0c8ac1fb0c954c4d0c9d9.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/577c67a61a616ff407fc1d4e3698e0d2cf829882b5fc8e7fdbf2a44d853bf998.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/57b5c25dde98879257bd8bcdf3e21022d042a8312b448838f7c2f114fda93b84.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/58579edba74c360c4f690d7a6d3f9167a98845857b136d63394666918d645354.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/59129df1878b0d8fc93ce3fb00ef4125807b6111c62df6fa167a343eb6c4e53a.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/59bcef67134ae9ef64f95aa195fd2ae6cf49a2b087c88a5dc193dd7449e95aea.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5b97a3c4b17945fe3c5144983218504824bd23aedb0ffbc38770303b1c0c6542.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5bc005ea25f127f1ecc519b4ce2a6a6ecf72e2248ac32437f4486f790b994991.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5d17264f91bb6f5e5efd22c9a35d36ed1e525ee62efc590f589f5a60d44d4b53.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5d4bdb6c225acdc57f9ccf35742be3e9ffa335baf136ba16162af1f54e972836.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5e5aef5f5bee26b4dd1c8486cb816a8ab8015dd60218f8b364a8c40fe48f01d6.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5ed03e73c3864e87bf1cb730694f1242313fcbab1de2550c6f23e5a9124c636d.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5ee37fba8142d1a252f1b69d83c276147446b6196357b2aa64fe82360cb1d6e2.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5ef68e1f0c1fa1244892ca556df13f37eb9cdda42d128b792c2d888f134dd032.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/5fa2406684ac0aaa83ee07ba7746f079140c80a70267385c05fddb98031eb2a6.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/612d64057d8cb483d9253f5671462ef2b615b3833dd74116d8183999a9391ee2.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/61ad892b1ac9853dc0d9d87b1f2c404bedbaf362bacd9fc7440662a7aa3c3aa3.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/62255f61acfbe91f98e025488dd2d2e16004e6e36216f26ac2fe4162f532d544.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/627aac5fea88690e536d2b659f821e93a7fb4b0b98631dd38799c4408731ebb2.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/62aa34418b224d08c8e689fbb1b7720ed6c3f8b70c5c598dbd064606cfdbc490.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/639095bb9dc8ef1a42948b5aea253fb639abcb29191ccfb8a6016933d5b8b8fa.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/64c996be9c2bb6e6811e3535d4fb3d2a3a72f078d6ec2ac4c88f9c43b83254bc.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/651170bddc4292eb11389d77b740f6170e554d82aa3ba7bf8a9e0529f0281211.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-419e-a948-8c5bb64039a6-actor_1_1.mvjngpqv-46880-d2ab3b9869.baseline/65b4c0240508e414345757bb26da618bac90e75404cb0c06dba9da4f2eb9d304.source
- .mimosa/hook-state/sess_dwf-dwfrun-2bf0a804-c4cc-

## 前置

- `$dotnet = C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe`（固定路径，
  见记忆 dotnet-sdk-relocated）
- 基线核验：build 0 错 + `dotnet test` 838/840（1 跳过 + 1 已知并行抖动
  `TrainingResetPerformanceTests`，隔离复跑绿才算过，见记忆 rl-perf-gate-parallel-flaky）+
  `replay-check replays/seed-42.json` PASS。
- 审计证据已入 `tmp/audit/`（不入库）：10 种子 MuJoCo 回放（mjs-*.json/q-*.json）、
  挂死控制器脚本（tmp/audit/ctl/*.py）、剥离命令对照（mj-30-nocmd.json）。

## 批 1：R1 MuJoCo 回放自复现

- [ ] 1.1 取证钉分叉：写临时探针（tmp/ 下，不入库）在回放循环内打印 tick 循环变量、
      `TickIndex`、`_events.Tick`、ApplyCommands 前后的事件序，对 `tmp/audit/mj-30.json`
      复现 315 vs 316 分叉，与 design §0 模型对齐；若不符，**以实测为准更新 design §0**。
- [ ] 1.2 按实测选方向（design 推荐：三处回放侧 ApplyCommands 前同步事件帧；
      备选：命令挪 Tick() 后 + 录制侧改帧）改 `src/Sim.Cli/Program.cs`、
      `godot/src/MatchSession.cs`、`godot/src/ParityCheck.cs`（三处 diff 逐行同构）。
      `MatchEngine` 需要的内部同步方法走 `InternalsVisibleTo` 先例。
- [ ] 1.3 MuJoCo 10 种子复验：重录 + replay-check，10/10 PASS（对照审计 6 FAIL）。
- [ ] 1.4 legacy 影响核对：`replays/seed-42.json`（无命令）必须逐位 PASS 不变；
      `restart-replay-seed42.json` fixture：`BuildRestartReplay` 录制序改统一语义后删除
      旧文件由 `EnsureFixture()` 重生成，diff 逐条列明（预期仅 Restart 事件 tick 299→300 类）。
- [ ] 1.5 新增 `src/Sim.Tests/ReplayCommandTickTests.cs`（MuJoCo 自录自校 /
      RestartPenalty 路径 / 命令 tick==事件 tick / 无命令锚）。
- [ ] 1.6 桌面侧 `--parity-check`（godot headless，真实 exe 路径见记忆
      godot-run-needs-real-exe-path；先 build -c Debug 再冒烟见记忆
      godot-qa-smoke-capture-channel 同源教训）。
- [ ] 1.7 全量 `dotnet test` + build 0 警 0 错。

## 批 2：R2 控制器写超时

- [ ] 2.1 `ExternalControllerBridge.Decide()` 写路径加超时（design §2 R2：单在途写 +
      超时 fault 零动作，不杀进程，不堆叠写任务）。
- [ ] 2.2 新增假进程回归测试（永不读 stdin 的子进程）：Decide ≤ timeout 量级返回 Zero、
      Faults 递增、连续调用耗时线性（无任务堆叠）。子进程选型实现时定（Windows CI 可用）。
- [ ] 2.3 手工复验审计两用例：`--controller-us python tmp/audit/ctl/stall.py`（45s 内应
      跑完 20s 场并给 fault 诊断）与 `slow.py`；`crash/nan/garbage.py` 三用例行为不变
      （400 fault + 比赛完成）。`example_controller.py` 正常路径 faults=0 不变。
- [ ] 2.4 `docs/CONTROLLER_PROTOCOL.md`：写超时语义补一句（契约与实现对齐）。
- [ ] 2.5 全量 `dotnet test`。

## 批 3：R3 duration 校验 + 文档

- [ ] 3.1 `Program.cs` match：`--duration` 非法/超上限（>10000 ticks = 500s）报错 exit 1；
      `BatchCommand.cs`：同口径 exit 2。
- [ ] 3.2 复验：`--duration abc|12x|""|-5|0|1e9|600` 全部显式报错；`60|60.5|1e2|500`
      合法行为逐位不变（比分与无校验基线一致）。
- [ ] 3.3 存量 grep：`tools/ controllers/` 内 `--duration` 调用点全部 ≤120s，输出贴执行记录。
- [ ] 3.4 `docs/CLI.md` 补 `--duration` 契约行。
- [ ] 3.5 全量门禁：build 0 警 0 错、`dotnet test` 全绿、replay-check 三基线
      （seed-42 / godot-parity fixture / restart fixture）、godot `--parity-check` PASS。

## 验证命令速查

```bash
DOTNET="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
CLI="src/Sim.Cli/bin/Release/net8.0/Sim.Cli.dll"
"$DOTNET" build RobotSimulator.sln -c Release --nologo -v q
"$DOTNET" test src/Sim.Tests -c Release --nologo
"$DOTNET" "$CLI" replay-check replays/seed-42.json
"$DOTNET" "$CLI" replay-record --seed 1 --scenario scenarios/wushu-ring-2026-mujoco.json --duration 120 --out tmp/audit/r1.json
"$DOTNET" "$CLI" replay-check tmp/audit/r1.json
# 桌面 parity(真实 exe, 非 PATH 符号链接):
#   <Godot 真实路径> --headless --path godot -- --parity-check ../replays/godot-parity-seed42.json
```

## Stop Conditions

- 1.1 实测与 design §0 模型矛盾且三方向全部实测失败 → 停，回主会话披露分叉证据再定向
  （兜底 = design §4 的"区分语义+如实披露"，须用户拍板）。
- 2.1 写超时方案与既有 `ExitDrainGrace`/读循环语义冲突（测试暴露行为回退）→ 停下复核
  design §2 R2 的"单在途写"裁决。
- 任一批全量门禁红且 30 分钟内无明确根因 → 停，记录现场交主会话。

## 提交分组（每批一组，独立可读，不 push）

1. `fix(sim): 回放命令时序统一——MuJoCo 自动重启回放自复现`（核心 + 三处消费方 +
   fixture 重录 + 新回归）
2. `fix(controller): 观测写超时——策略停读 stdin 不再挂死整场`（桥 + 回归 + 协议文档）
3. `fix(cli): --duration 非法/超上限显式报错，不再静默回退或截断`（CLI + CLI.md）

## 行尾纪律

仓库 blob 为 LF；提交前 `git diff --stat` 行数须与实际改动一致，超出 = CRLF churn，
按文件 HEAD 原状归一（python 二进制替换；见记忆 git-bash-crlf-illusion-and-edit-tool-eol）。
