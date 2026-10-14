# 基线 P0 证据（自评估报告提炼，2026-10-14，dsv4.1 子代理评估 + 主会话逐条复验）

评估背景：官方基线场景 scenarios/wushu-ring-2026.json，种子 1-8，120s（部分 300s），Release。原始事件流与对账脚本在 tmp/sim-effect-eval/（gitignore，不入库）。

## P0-1 台沿抖动死循环（送分引擎）——主会话实锤

- **同 t Mount+Drop**：seed1 事件流 t=147/156/166/1053 均出现三行连续 `Mount(fsm)→Drop(score)→Drop(fsm)`——同一 tick 判上台又判掉台。
- mount→drop 中位间隔 0.0–0.1s，周期 0.7–1.0s 死循环持续 91.5s（seed1）。
- 掉台方向固定「西」：seed1 104/105 次掉台方向为西。
- 送分规模：seed1 120s 对手 93 分中 90 分（96.8%）来自我方掉台送分；seed3 90.7%、seed4 82.4%、seed8 73.7%（对账 8/8 与 CLI 汇总一致）。seed1@300s 送 129 分（9:93→9:134），不自愈。
- 根因线索（评估指向，修复时须独立诊断确认）：OnStage 判据要求四角全部在台（src/Sim.Core/Physics.cs:168-180）；RECOVER spin 对位惯性越界；Rec.Count 每轮重置致 RecoverLimit=3 保护失效。
- Drop 事件是边沿触发（src/Sim.Core/MatchEngine.cs:801-846 `var usDrop = prevUs && !nowUs;`）。

## P0-2 ATTACK 无超时 → 永久僵持——主会话实锤

- src/Sim.Cli 或 godot 的 FSM：src/**/Fsm.cs:957-969 AttackTick 唯一退出路径是"对手不在台上"，无超时/无重定位/无避边。
- 判决性实验 seed2@300s：最后实质事件 t=352.00（35.2s），之后 **264.8s 静默**、比分冻结 10:4 直到超时；seed5 静默 157.5s。
- 8/8 种子场次 done=比赛时间结束，**从未分出胜负**，faults/penalties 恒 0。

## P1 度量口径（oracle-metrics-fix 任务修，此处仅记录影响）

- oracle.py:75 `" Drop " in line` 计数把 score+fsm 双行各计一次：seed1 212 条 Drop 行 ≈105 次真实掉台。
- oracle.py:94 注释自写 "t value in 0.1 s units" 但 _event_t 不换算：mount_t=13.0 实为 1.3s。
- sensitivity.py 复用 parse_match_output → 敏感性报告 us_drops/mount_t 两指标失真。
- 种子分裂：抖动型(1/3/4/7/8) vs 僵持型(2/5/6)，事件量 63→1024、比分 9:93↔12:3——敏感性基线 std 72.9 是双稳态跳变噪声，非参数敏感性信号。
