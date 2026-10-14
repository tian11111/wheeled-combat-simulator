# oracle.py 编码 bug 修复与历史污染排查

## Goal

修复 `tools/param-optimization/oracle.py` 的 GB18030 解码 bug（实证成立），并排查 param-optimization 历史结论是否被该 bug 污染。

## 背景与实证（来自 10-14-sim-physical-sensitivity 质量复查，已钉死）

- `oracle.py:41` `encoding="gb18030", errors="replace"` 解码 match 子进程 stdout；实测 CLI 管道输出是 **UTF-8**（`[我方]` 首两字字节 = `E6 88 91 E6 96 B9`）。
- 后果：gb18030 解码下 `[我方]/[对手]` 归属全失效（us_drops 210→0、final_score 解析失败）；UTF-8 下正常（us_drops=210, final_score=(9.0,93.0)）。
- 证据链：10-14 任务 `tools/sensitivity/selftest.py` KNOWN-DEVIATION 实证 + 复查子代理字节级复验（tmp/chk-encode/，本地已清）。
- 10-14 任务的 `sensitivity.py` 已自写 `run_match_utf8` 绕开此 bug（不动 oracle.py——本任务职责）。

## Requirements

### R1. 修复

- `oracle.py:41` 解码改为 UTF-8（`encoding="utf-8", errors="replace"`）。
- 修复后 `oracle.parse_match_output` / `run_match` / `write_scenario` 行为回归：用基线场景 wushu-ring-2026.json + seed 1 跑一场 match，UTF-8 解析得 us_drops=210、final_score=(9.0, 93.0)（与 10-14 实证口径逐字段一致）。
- `tools/param-optimization/selftest.py`（若存在且覆盖解析路径）复跑通过。

### R2. 历史污染排查

- 读 `tools/param-optimization/optimize.py`：确认它评分是否经 `oracle.run_match`（gb18030 路径）还是别的路径。
- 检查 `study/*.json`（baseline/holdout）历史数据特征：若分数/掉台指标存在"全 0 或 None"等失效签名 → 结论可能基于失效解析，标注污染并给出复跑建议；若数据正常（非零 us_drops/有效 final_score）→ 历史结论不受此 bug 影响，记录证据即可。
- **本任务不重跑历史优化**（成本高），只判定污染与否 + 建议。

### R3. 边界

- 只改 `oracle.py` 一行（+必要的 docstring 更新）；不动 `sensitivity.py`（它已自带 UTF-8 路径且已提交）；不改 src/。

## Acceptance Criteria

- [ ] `oracle.py:41` UTF-8 修复落盘；基线 seed 1 回归跑通过（us_drops=210 / final_score 字段一致）。
- [ ] optimize.py 评分路径依赖关系查明并记录。
- [ ] study/*.json 污染判定完成（每文件一行结论 + 证据）。
- [ ] `dotnet build`/`dotnet test` 全绿（零 src/ 改动防意外）。
- [ ] 提交（一组：oracle.py + 本任务工件；污染判定结论写进任务 research 或执行记录）。
