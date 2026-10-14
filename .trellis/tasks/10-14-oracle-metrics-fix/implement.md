# Implement 执行记录 — 10-14-oracle-metrics-fix

## 执行者更正说明

原计划派 dsv4.1 实现子代理，两次派发均在运行中失败（无遗言、零进度：oracle.py/Js.cs/sensitivity 全未动）。
主会话核实渠道状态：`cline/cline-pass/deepseek-v4.1-flash` 连"回复一个词"的最小探针都失败；会话 provider 白名单锁死 cline（trae 被拒），故改用**主会话直接实现**。

## 步骤与结果

| 步 | 动作 | 结果 |
|---|---|---|
| 1 | 修 `src/Sim.Core/Js.cs` ToFixed | 三版迭代：①除回 scale（修 10× 放大）②`"R"` 格式解析（1.2345 仍错）③**G17 精确展开 + 绝对值域舍入 + 负零保留**（最终） |
| 2 | 新增 `src/Sim.Tests/JsToFixedTests.cs` | 22 例对照 Node 权威输出（含二进制真值舍入 1.2345→"1.234"、1.005→"1.00"、2.675→"2.67"；平手进位；负零 "-0"；负数）全过 |
| 3 | 修 `oracle.parse_match_output` us_drops 口径 | 只计 `[fsm]` 掉台转移行；`[score]` 计分行不另计。历史流实测 210 → **104**（[score]106+[fsm]104=210 双计确认） |
| 4 | 修 `oracle._event_t` 单位口径 | Js.ToFixed 修复后事件行 t= 即真秒，删除"0.1s 单位"换算与过时注释 |
| 5 | 更新 `oracle/selftest.py` | sample 改双行结构 + 秒单位；新增"去掉 fsm 行后 us_drops=0"分流断言；双编码容错断言保留。跑通 |
| 6 | 连锁面清理 | 全量 test 暴露 3 个 fixture 失配（事件指纹含 t 文本）→ 重录 `replays/seed-42.json`、覆盖 `fixtures/godot-parity-seed42.json`、删 `fixtures/restart-replay-seed42.json`（测试自生成）→ 全部 PASS |
| 7 | 敏感性重跑 | 旧 results 作废（备份 tmp/sensitivity-old/），全量 384 场重跑（~90s，0.21s/场）。报告头补失效率说明 + 新增"修复前后对比"段 |
| 8 | 08-28 附注更新 | 重写 `sensitivity-input.md`：首轮"整体降级"建议作废，改为 angDamping/latFrictionK **高优先级** |
| 9 | R4 历史口径标注 | `pollution-verdict.md` 追加"mount_t 是旧放大值、判定不受影响"补注 |
| 10 | 验收 | build 0 错；全量 test **839 通过 / 0 失败 / 1 跳过**；replay-check 三基线 PASS |

## 关键发现（对 08-28 有实质影响）

- **首轮敏感性"无参数显著"是假阴性**：缺陷引擎的双稳态噪声（us_drops 49.5±72.9）掩盖了真实效应。
- 修正后 **latFrictionK 3.02 / angDamping 4.54 双双显著**（S≥3），恰是 fidelity friction/collision 待标定量 → **强化 08-28 采集优先级**，而非首轮暗示的"降级"。
- Js.ToFixed 真 bug 修正：`1.2345.toFixed(3)` 曾给 "1.235"（JS 为 "1.234"）——decimal 精度陷阱，最终用 G17 精确展开解决。

## 未 commit（按铁律留主会话）
