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
