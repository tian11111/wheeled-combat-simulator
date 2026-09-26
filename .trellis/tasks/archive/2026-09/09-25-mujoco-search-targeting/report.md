# 报告:MuJoCo 模式 SEARCH 索敌闭环(2026-09-25 第一轮)

## 结论

AC1 ✅、AC2 ✅(选定补偿系数 4)、AC3 ✅(分类分流三场景)、AC5 ✅(CoreVersion
门禁 + 全量回归);**AC4 ❌(如实失败)**——官方 120 s 比分 1:1 非零,但按
任务书口径 ScoreClock/被动得分不计,全场无真实 BlockScore。任务保持
in_progress,SCORE 阶段掉台循环为首个卡点。

## AC1 ✅ 基线与逐 tick 定位

- 基线(HEAD e3865fc + 登台修复后):官方 mujoco 120 s → 1:1(读秒+对手掉台),
  legacy 120 s → 4:49;首轮 classify:mujoco 29.1 s vs legacy 3.1 s(慢 9 倍)。
- 逐 tick 证据(临时诊断,已删):首个发现窗口中 `dLB` 探针 **D=0.40 m**(目标
  块 (1.35,1.35),量程内;历史日志的 4.0 m 在当前版本不复现,属登台修复前的
  旧代码状态);turn 相位 W=−2.00 下实际偏航 0.06–0.09 rad/s(指令的 ~3%),
  3 s 窗口仅转 0.2 rad——**根因确认为原地旋转扭矩饥饿**,探针契约无问题。

## AC2 ✅ 受控转向对照,选定系数 4

受控场景(台面中心 (1.9,1.9)、唯一目标 3π/4 方位 0.8 m、其余出探针范围、
对手零动作),候选 1/2/4/6:

| 系数 | 发现→classify | 判定 |
|---|---|---|
| 1(基线) | ~29 s(全场首分类) | 超预算 |
| 2 | 9.45 s | 淘汰(>3 s) |
| 4 | **<3 s,窗口最小误差 0.032 rad** | **选定** |
| 6 | 过冲跳过对准窗口(单 tick 1.09 rad) | 淘汰 |

补偿实现:`MujocoPhysicsBackend.SetControls` 中对 `|CmdV|≤0.02 且 |CmdW|>0` 的
原地转向命令放大差速轮目标速度(轮速仍经 80 rad/s 截断);`SearchTurnCompensationTests`
锁定选定值并含登台回归检查。kv/摩擦/力上限/登台动作零改动。

## AC3 ✅ 分类分流三场景

- 增益块:发现 → classify → SCORE_BLOCK → 接触推块(位移 >0.05 m)→ BlockScore 事件 ✅
- 静止对手:发现 → classify → ATTACK ✅
- 负例:减益块不被路由到 SCORE_BLOCK ✅

## AC4 ❌ 官方全场未达真实对抗得分(如实失败)

官方 seed 42、120 s:比分 1:1(tick 1821 因"恢复次数超限 → 停车"提前结束)。
归属:0:1 为我方掉台(Drop,规则分),1:1 为我方登台读秒(ScoreClock)——
**按任务书口径两者均不算真实对抗得分;全场无 BlockScore,BlockOff 一次为
双方同时接触不计分**。首个卡点:SCORE_BLOCK 阶段驱动无避边保护,车辆带惯性
在台沿附近行进反复掉台 → RECOVER 循环 → 恢复超限。AC4 保持失败,下一轮需
设计 SCORE/行驶阶段的近沿行为(减速/绕行/倒车回台),或允许更长的恢复预算。

## AC5 ✅ 回放身份与全量回归

- `MatchEngine.CoreVersion` 0.1.0 → **1.0.1**;`CreateForReplay` MuJoCo 分支
  额外比较 CoreVersion(错误信息含两侧版本);legacy 回放不加门禁。
- 定向测试:当前 CoreVersion 的 MuJoCo 回放创建通过;篡改为 1.0.0 → 明确拒绝
  (双版本在错误信息中)。
- 旧 legacy 回放 6/6 逐位 PASS(rotated 为重录后的当前预期行为基线,处置记录
  见验收任务);旧 mujoco 回放(1.0.0)被拒——预期;新录制回放 CLI 逐位 PASS
  + 真实 Godot parity PASS;p1=p4 逐行一致;32×5s p32 → 32/32。
- 重启回放夹具 `restart-replay-seed42.json` 按再生路径更新(删除后由测试重建,
  coreVersion 1.0.1),非手改。
- 全套 `dotnet test`:**369/369**(含受控转向、分类分流、CoreVersion 门禁新测试)。

## 遗留与建议

1. AC4 下一轮:SCORE/行驶阶段近沿行为设计(拟:mujoco 分支的避边减速或倒车
   回台),目标 = 官方 120 s 出现真实 BlockScore。
2. 恢复次数超限提前停车(91 s)是否适配新模式运动学,建议与规则口径一起复核。
3. `fidelity.json` 未晋升;legacy 逐位不变。

## 2026-09-25 接手更新：AC4 当前工作树候选通过，AC5 待补核

上面的 AC4 ❌ 是本轮修改前的历史结果。当前未提交工作树的官方 MuJoCo seed 42、默认 120 s 比赛跑满 2400 tick，比分我方 8:3、faults 0/0；事件中我方在 `t=236` 取得真实 `BlockScore`（+3），对手在 `t=235` 也取得 `BlockScore`。我方全场无 `Drop` 事件。新增 `OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops` 测试同时检查我方 `BlockScore`、增益块实际位移/出界和我方无掉台。该证据满足 AC4 的本轮候选门槛；请接手代理复查完整事件、位姿和接触链后再最终勾选 AC4。

当前 `dotnet test RobotSimulator.sln -m:1 --no-restore` 为 373/373；6 个 `replays/*.json` 的 legacy `replay-check` 全部 PASS。新 MuJoCo 回放保存在临时目录 `mujoco-score-edge-core-1.0.2.json`，`replay-check` 得分 8:3、事件 117/117 PASS。Godot headless 命令退出码为 0，但只输出引擎版本，没有明确的 parity PASS 行；并行 batch 门禁和 `git diff --check` 尚未在本轮重做。因此 AC5 暂不标最终通过。

改动说明、命令、文件边界和接手清单见 [handoff-2026-09-25.md](handoff-2026-09-25.md)。保留原有未提交文件，不要用整仓 restore/reset 清理。

## 2026-09-25 接手完成:AC4 ✅ / AC5 ✅(独立复核 + 补齐门禁)

接手对方候选(局部 MuJoCo FSM 控制:近沿回中守卫 0.27 m、对准分段、
MuJoCo 无进展限时 8 s/2 s 分级、score_retreat 得分后驶离台沿、CoreVersion 1.0.2),
独立复核与补齐结果:

- **审查**:Fsm.cs 阈值(0.27/0.45/0.65)与 score_retreat 退出条件结构合理、
  additivity 保持;`MujocoScoreEdgeGuardTests` 覆盖 近沿回中指令(V=−0.4/W=0)、
  清距离继续推块(0.35)、**legacy 行为不变 pin**、官方 seed 42 整场(真实
  BlockScore + 块出界 + 无我方 Drop + 2400 ticks)。
- **AC4 ✅**:官方 seed 42、120 s 跑满 2400 ticks,比分 **8:3**,faults 0/0;
  得分归属逐项核对:我方 **t=236 真实 BlockScore(+3,增益块被推下擂台)**、
  对手 t=235 BlockScore(+3)、对手掉台 ×3(对抗结果);**我方无 Drop 事件**;
  新增整场测试同时断言 BlockScore、块位移出界与无重复掉台。
- **AC5 ✅(补齐对方未完成项)**:重建 Godot 程序集后 `--parity-check` **PASS
  (8:3、2400/2400、117/117 事件)**——对方"无明确 PASS 行"是未重建 Godot
  程序集所致(已知坑);batch p1/p4 剔除 createdAt 后逐行 IDENTICAL;
  32×5s p32 → 32/32 completed、各 100 ticks;`git diff --check` 干净;
  CoreVersion 门禁测试(1.0.2 当前通过 / 1.0.1 拒绝)在 373 内通过。
- 全套 `dotnet test`:**373/373**(接手复核确认,含 MujocoScoreEdgeGuardTests、
  分类分流、CoreVersion 门禁)。
- RL 试点任务 `09-25-mujoco-score-rl-pilot` 保持 planning(前置条件 AC4 已满足;
  其 design.md 的 `Tick(action, null)` 会在 MatchEngine 中把我方切到 MANUAL,
  实施前须改为 reset 后用内置 FSM 推进至 SCORE_BLOCK 再接管——见交接记录)。

**任务五项 AC 全部达标,可关闭。** `fidelity.json` 未晋升;legacy 逐位不变。
