# 验收报告:MuJoCo 内置 FSM 倒车登台修复(独立复核)

> 2026-09-25。复核对象:父任务 `09-25-mujoco-fsm-reverse-mount` 修复提交 `fd84852`。
> 本报告全部证据为本轮新鲜重跑;工具:.NET SDK 8.0.425(临时目录安装)、真实
> Godot 4.7.2-stable Mono EXE;工作区与 `fd84852` 一致(仅任务文档差异)。

## AC1 / R1 官方出生点 FSM 登台 ✅

`match --seed 42 --duration 30 --scenario scenarios/wushu-ring-2026-mujoco.json --events`:

- 我方:tick 24(2.4 s)"登台信号: 后向灰度 216>150 → climbed" → **tick 64(3.2 s)
  "已上台 on_stage ✓ → SEARCH"**(即首次 FullOn 与首次 SEARCH 同 tick);Mount 事件
  (EventKind.Mount,非中立,我方)在事件流中;对手车同 tick 完成登台。
- **路径判定**:事件链为 姿态确认→摆正→倒车登台→climbed→已上台,**无**
  "倒车超时失败""换面重试""正冲备选"事件——直接倒车路径一次达成,未动用
  超时换面/正冲备选。远小于 600 ticks 上限。
- 自动化锁定:`NativeMode_FsmMountsFromOfficialSpawn`(官方出生位姿、`Arm()`、
  无参 `Tick()`),断言 FullOn + 进入 SEARCH,通过。

## AC2 / R2 物理连续性 ✅(画面项 ✅)

- 直接驱动(`NativeMode_MountsTheSixCentimetreStageContinuously`,强化版):
  120 ticks 中 **FullOn >10 ticks**、逐帧三维位移 <0.15 m 断言通过。
- FSM 路径:`NativeMode_FsmMountsFromOfficialSpawn` 逐 tick `Snapshot.Validate()`
  为空断言通过。
- Godot 真实动态画面(`--auto-arm --write-movie`,20 fps,0–5 s,1280×720):
  frame 55(2.75 s)双方车在各自台沿翻越中、姿态连续;frame 75(3.75 s)双方
  完整上台、状态 SEARCH。无穿透、无瞬移、无弹跳。临时截图未入库。

## AC3 / R3 全套测试与旧回放枚举 ✅(含如实例外)

- 全套 `dotnet test`:**362/362 通过**(含强化 mount 测试与 FSM E2E 新测试)。
- 旧回放逐文件枚举:

| 回放 | 结果 |
|---|---|
| arena-layout-audit-seed42.json | PASS(逐位) |
| godot-parity-seed42.json | PASS(逐位) |
| seed-42-pyus.json | PASS(逐位) |
| seed-42.json | PASS(逐位) |
| task-one-review-seed42.json | PASS(逐位) |
| rotated-seed42.json | **FAIL(继承例外,非本修复引入)** |

  `rotated-seed42.json` 的失败与修复前一致:已在 09-24 验证报告 §2 定位为分支
  陈旧基线(反僵局提交 4d53942 后未随 5bbcd3e 再生;main 逐位 PASS;注入
  `antiStallBladeAmp=0` 后逐位 PASS)。**据此,任何"旧回放全量 PASS"的表述
  均不成立,应表述为"5/6 PASS + 1 个继承例外"**;父任务报告原文使用的是
  "旧回放 5/6 PASS(rotated …)"表述,与事实一致。
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

**通过**。父任务 `09-25-mujoco-fsm-reverse-mount` 的五项 AC 在独立复跑下全部
成立;"旧回放全量 PASS"类表述按上述口径修正为含继承例外的准确表述。
