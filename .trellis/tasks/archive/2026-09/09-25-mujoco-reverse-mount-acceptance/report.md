# 验收报告:MuJoCo 内置 FSM 倒车登台修复(独立复核)

> **2026-09-25 最终处置更新**:本报告初版复核时 `rotated-seed42.json` 尚未处置,
> AC3 曾标 ⚠️ 未通过。随后该回放经定性为**陈旧基线**(反僵局铲刃微调默认值变更
> 未再生;当前 HEAD 上注入 `antiStallBladeAmp=0` 后逐位 PASS 的反证),并按当前
> 预期行为重录(旋转位姿 x=0.4,y=−0.3,th=30°,seed 42,120 s → 278 事件,
> replay-check 逐位 PASS;旧文件备份于 `telemetry/data/rotated-seed42.pre-antistall.bak.json`)。
> 重录后六份旧回放 **6/6 逐位 PASS**、全套 362/362 —— AC3 达到原定标准后勾选。
> 下文保留初版失败记录作过程证据;本轮在 HEAD `7efc0d7` 上复核。
> 工具:.NET SDK 8.0.425(临时目录安装)、真实 Godot 4.7.2-stable Mono EXE。
> `replays/*` 被 `.gitignore` 排除,故 6/6 结论针对当前工作区本地回放文件;
> 新检出目录须取得同一批回放后才能复核,仓库提交本身不携带重录文件。
> 本地 `rotated-seed42.json` SHA-256:
> `79eddaa23daf8fb7f37ac2d93917d9335b8b5ef9a3d8a4d219a493cddc650f20`;
> 旧备份 SHA-256:`3f3bd141fbc885b6fb28c596fc1f019ea7110a44d1fcd97505efbd99a736c02`。

## AC1 / R1 官方出生点 FSM 登台 ✅

`match --seed 42 --duration 30 --scenario scenarios/wushu-ring-2026-mujoco.json --events`:

- 我方事件流依次出现“摆正完成 → 倒车登台”、`climbed`、
  “已上台 on_stage ✓ → SEARCH”；后两者为我方非中立 Mount 事件。
  本轮 CLI 重跑也出现相同顺序且退出码为 0；首次 FullOn 与 SEARCH 的 tick
  由自动化测试判定,不把 CLI 事件时间字段误写成 tick 编号。
- **路径判定**:事件链为 姿态确认→摆正→倒车登台→climbed→已上台,**无**
  "倒车超时失败""换面重试""正冲备选"事件——直接倒车路径一次达成,未动用
  超时换面/正冲备选。远小于 600 ticks 上限。
- 自动化锁定:`NativeMode_FsmMountsFromOfficialSpawn`(官方出生位姿、`Arm()`、
  无参 `Tick()`),现断言 FullOn 不晚于 SEARCH、倒车阶段先于 Mount、无我方绕路
  事件及逐帧三维位移 <0.15 m;本轮定向测试 2/2 通过。
  初版现场记录首次 FullOn 与 SEARCH 均在第 64 次迭代;测试上限为 600 ticks。

## AC2 / R2 物理连续性 ✅(画面项 ✅)

- 直接驱动(`NativeMode_MountsTheSixCentimetreStageContinuously`,强化版):
  120 ticks 中 **FullOn >10 ticks**、逐帧三维位移 <0.15 m 断言通过。
- FSM 路径:`NativeMode_FsmMountsFromOfficialSpawn` 逐 tick `Snapshot.Validate()`
  为空且逐帧三维位移 <0.15 m,断言通过。
- Godot 真实动态画面(`--auto-arm --write-movie`,20 fps,0–5 s,1280×720):
  frame 55(2.75 s)双方车在各自台沿翻越中、姿态连续;frame 75(3.75 s)双方
  完整上台、状态 SEARCH。无穿透、无瞬移、无弹跳。临时截图未入库。

## AC3 / R3 全套测试与旧回放枚举 ✅(初版 ⚠️,rotated 处置后转绿,见头部更新)

- 本轮复核全套 `dotnet test`:**362/362 通过**(含强化 mount 测试与 FSM E2E 新测试)。
- 旧回放逐文件枚举:

| 回放 | 结果 |
|---|---|
| arena-layout-audit-seed42.json | PASS(逐位) |
| godot-parity-seed42.json | PASS(逐位) |
| seed-42-pyus.json | PASS(逐位) |
| seed-42.json | PASS(逐位) |
| task-one-review-seed42.json | PASS(逐位) |
| rotated-seed42.json | **PASS(陈旧基线重录后,本轮复核)** |

  **初版处置前记录**:`rotated-seed42.json` 曾失败,09-24 验证报告 §2 将其
  定位为分支陈旧基线(反僵局提交 4d53942 后未随 5bbcd3e 再生;注入
  `antiStallBladeAmp=0` 后逐位 PASS)。当时只能报告 5/6,不能豁免 AC3。
  当前文件按预期行为重录后,本轮逐文件重跑为 6/6 PASS,所以 AC3 现在转绿。
- 旧登台/重启 pin:`MountGateParameterTests`、`MatchEngineTests.Arm_MountsPlatform_AndEntersSearch`、
  `RestartRobotTests`、`TransformedFieldTests` 全部在 362 中通过,legacy 行为逐位不变。

## AC4 / R3 复现、并行与隔离 ✅

- 新模型哈希下重录回放(CLI):`replay-check` PASS(逐位)。
- 真实 Godot Mono `--parity-check`:PASS(600/600 ticks、事件指纹一致)。
- batch p1 vs p4(seeds 1-4,5 s,剔除 createdAt):逐行 IDENTICAL。
- 32×5 s、并行度 32:**32/32 completed**、每行 100 ticks、exit 0,无伪成功输出。
- 模型哈希变化导致修复前的旧 mujoco 回放被身份校验拒绝——预期行为,重录即可。

## AC5 / R4 报告与文档对齐 ✅

- 本报告落任务目录;每项判定附可复现命令(`match --events`、`dotnet test`、
  `replay-check`、`--parity-check`、`batch`、`--write-movie`)。
- `docs/CLI.md` 过时的"新模式下可能无法完成倒车登台"说明已修正为修复后行为
  (提交 a362a00),并如实标注 SEARCH 索敌不收敛为已知后续边界。
- 测试注释一致性:无与当前参数不符的残留(唯一"卡沿"字样位于强化 mount 测试
  的禁止性断言说明中,语义正确)。
- **SEARCH→ATTACK 收敛问题不属于本次登台结论**:登台修复验收不受其影响;
  已另立任务 `09-25-mujoco-search-targeting` 跟踪。`fidelity.json` 未晋升。

## 验收结论

**当前工作区登台修复验收通过。** AC1、AC2、AC4、AC5 沿用前述验收证据;
AC3 在陈旧回放重录后达到本地 6/6 逐位 PASS,本轮全套 362/362。验收任务已
归档。本轮没有重跑 Godot 动态捕获,AC2 的画面结论沿用原验收报告。

## 本轮复核命令

- `%TEMP%\robot-simulator-dotnet-sdk\dotnet.exe test src/Sim.Tests/Sim.Tests.csproj -m:1 --no-restore --filter <上述两个登台测试>`：2/2 通过。
- 同一 SDK 执行 `test RobotSimulator.sln -m:1 --no-restore`：362/362 通过。
- 对 `replays/*.json` 各执行 `run --no-build --project src/Sim.Cli -- replay-check <文件>`：当前本地 6/6 PASS。
- `match --seed 42 --duration 30 --scenario scenarios/wushu-ring-2026-mujoco.json --events`：退出码 0，倒车、climbed、on_stage→SEARCH 事件依序出现。
