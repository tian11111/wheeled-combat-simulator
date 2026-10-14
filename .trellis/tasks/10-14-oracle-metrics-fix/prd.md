# PRD：oracle 度量口径修正与敏感性重跑

## 背景

仿真效果评估发现 oracle.py 两个度量 bug（10-14-baseline-p0-fix/research/evidence.md P1 节）：
- us_drops 双计：oracle.py:75 `" Drop " in line` 把同一次掉台的 score 行与 fsm 行各计一次（seed1 212 行 ≈105 次真实掉台）；
- mount_t 单位不换算：oracle.py:94 注释自写 "t value in 0.1 s units"，_event_t 直接返回显示值（13.0 实为 1.3s）。
sensitivity.py 复用 parse_match_output → 敏感性报告两指标失真；且敏感性基线是抖动/僵持双稳态噪声（P0 所致），**本任务依赖 10-14-baseline-p0-fix 完成后重跑才有意义**。

## R1 us_drops 单计修复

- 语义钉死：Drop 事件边沿触发（MatchEngine.cs:801-846），一次掉台一条 [fsm] 转移行 + 一条 [score] 送分行。us_drops = 真实掉台次数 = 只计 [fsm] 行（或 [score] 行，二选一后在代码注释钉死口径依据）。
- 验收：对 seed1 修复前后事件流，us_drops 从 210 降到 ≈105（±1 边缘可容，注明）。

## R2 mount_t 单位换算

- _event_t 返回值换算为秒（t 显示值 /10），或 parse 层换算；口径在注释钉死。
- 验收：seed1 mount_t ≈1.3s（非 13.0）。

## R3 敏感性重跑（依赖 baseline-p0-fix 完成后的引擎）

- 修复后引擎上重跑 384 场（12 参数×4 水平×8 种子 + 基线 8），更新 tools/sensitivity/report/sensitivity-report.md 与 sensitivity-results.json；报告头注明"引擎行为修复后重跑，旧报告数字失效"（旧结果文件保留 git 历史可溯）。
- 验收：新报告含修复前后基线指标对比段（us_drops/mount_t 口径修正 + P0 修复效果）；阳性对照 classifyRate 仍敏感（方法学自检）。

## R4 历史数据口径标注（不改判定）

- tools/param-optimization/study/trials.csv 与 10-14-oracle-encoding-fix/research/pollution-verdict.md 里的 mount_t 数字是 0.1s 单位显示值——只在文档口径处加一行注明，不改历史判定结论（污染判定本身不受单位影响）。

## 边界

- 只改 tools/param-optimization/oracle.py + tools/sensitivity/（selftest 断言更新 + 重跑报告）；不改 src/；不动 10-14-baseline-p0-fix 的范围。

## 验收清单

- [ ] R1/R2 修复 + selftest 断言更新（mount_t 秒单位、us_drops 单计、双编码容错断言保留全过）；
- [ ] R3 重跑完成、报告更新含对比段、阳性对照仍敏感；
- [ ] R4 口径注明落盘；
- [ ] build 0 错、全量测试绿（tools 改动不碰测试，跑一遍兜底）；
- [ ] git 提交分组：oracle 度量修复 + 敏感性重跑报告。
