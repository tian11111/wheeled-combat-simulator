# 执行清单 — 10-14-sim-physical-sensitivity

> 顺序执行；每步含验证命令与回滚点。全程不改 src/。

## 0. 前置确认（5 min）

- [ ] `dotnet build RobotSimulator.sln -c Release` = 0 错误（确认 Sim.Cli.dll Release 产物路径，记入执行记录）
- [ ] `git rev-parse HEAD` 记录（进报告配置头）
- **回滚点**：无改动。

## 1. 工具实现（tools/sensitivity/）

- [ ] `sensitivity.py`：variant 生成（parameters 级 + vehicles 双侧级 + clamp 域）/ OAT 循环 / 断点续跑（临时文件+rename 原子写）/ 聚合 S 计算 / markdown+json 报告输出（结构照 design.md §6）
- [ ] `selftest.py`：design.md §5 三项对拍
- [ ] **vehicle 字段 wire 名拼写实测钉死**：先跑一次 vehicles 覆盖变体验证（Validate 抛错即拼写错，从报错学正确拼写）
- 验证：`python tools/sensitivity/selftest.py` 三项全过
- **回滚点**：`rm -r tools/sensitivity/`（纯新增目录）。

## 2. 自测与口径对拍

- [ ] selftest §1：基线 s1 直跑 oracle vs sensitivity 路径 → 五指标逐字段相等
- [ ] selftest §2：latFrictionK=16 变体落盘读回正确 + match 正常退出
- [ ] selftest §3：删半 results 条目重跑 → 补齐且与全量一致（抽查 3 条）
- **回滚点**：同上（删目录）。

## 3. 全量扫描（1-1.5h 量级，可分批）

- [ ] 基线 8 种子 → 8 参数 × 4 水平 × 8 种子（或 method §6 缩减档：先 s1-s4 全表 → 强参数补 s5-s8；实际口径写进报告）
- [ ] 每场完成即增量落盘 `report/sensitivity-results.json`；中断可续
- 验证：results.json 条目数 = 预期场数（基线+扰动表）；无异常退出码
- **回滚点**：`rm -r tools/sensitivity/report/`。

## 4. 报告与交叉引用

- [ ] `report/sensitivity-report.md` 生成（配置头/阳性对照/逐参数表/相变/边界/08-28 建议）
- [ ] 阳性对照不敏感 → 整表作废声明并停下排查（不许静默继续）
- [ ] `.trellis/tasks/08-28-real-telemetry-capture-first-promotion/research/sensitivity-input.md`：一行交叉引用 + 采集优先级建议摘要（不改其 PRD）
- 验证：报告含全部 8 参数行；08-28 research 文件存在
- **回滚点**：删 report 产物 + 08-28 附注文件。

## 5. 收尾验证

- [ ] `dotnet build RobotSimulator.sln -c Release` 0 错误 + `dotnet test src/Sim.Tests -c Release` 全绿（证明零意外触碰；注意 TrainingResetPerformanceTests 已知并行抖动，隔离复跑判读）
- [ ] `git status --porcelain` 脏文件清单 = 预期集（tools/sensitivity/ 新增 + 08-28 附注 + .gitignore 一行）且全部属于本任务
- [ ] .gitignore 追加 `tools/sensitivity/report/variants/`
- **回滚点**：全任务 = 删 tools/sensitivity/ + 删 08-28 附注 + 还原 .gitignore。

## 6. Phase 3（另见 workflow.md）

- 3.3 spec update：若有可沉淀知识（如 vehicle wire 名拼写表、oracle 复用模式）写入 .trellis/spec/sim/
- 3.4 批量提交（分组建议）：
  1. `feat: 物理参数敏感性分析工具与报告`（tools/sensitivity/*.py + report/sensitivity-results.json + sensitivity-report.md + .gitignore）
  2. `docs: 08-28 采集优先级敏感性输入`（08-28 research 附注）
- 3.5 wrap-up

## 执行记录（实施时填写）

| 步 | 实际口径/偏差 | 结果 |
|---|---|---|
| 0 | build 0 警告 0 错误；git HEAD 1250bb871198ea0495fad5fb22529166aac4aab4；预存脏文件（非本任务）：10-06 task.json + 本任务目录 | ✅ |
| 1-2 | sensitivity.py+selftest.py 落盘；wire 名实测：**两条父代理环境事实不成立并修正**——①CLI match 子进程管道输出实测为 UTF-8 字节（首行 "[我方]" 字节 E6 88 91 E6 96 B9；RunMatch 无显式编码设置，Program.cs:42-43 显式 UTF-8 仅作用于 rl-env），oracle.run_match 的 gb18030 解码使归属全失效（us_drops 210→0），自写 run_match_utf8+复用 parse_match_output/write_scenario；②键名实为 classifyRate（camelCase），CLASSIFY_RATE 抛 Unknown。vehicles wire 名 latFrictionK/angDamping（正确拼写行为变化、错拼静默忽略==基线，探针 tmp/sens-probe/）；selftest 三项全过（③中发现 final_score tuple/list json round-trip 与 wall 波动两处断言口径问题，已归一化修复） | ✅ |
| 3 全量/缩减 | **全量未缩减+第二梯队纳入**：实测单场 wall≈0.23s（method §6 估计 1-1.5h 实际 2 分钟量级）→ 按 PRD R1"预算允许才做"纳入二梯队 4 参数，共 12 参数（核心 7+CR+对照+二梯队 4）。水平 ×{0.75,1.25,0.5,1.5}（PRD ±10% 与 4 水平计数矛盾，按 design §3 推广，偏差声明在报告配置头）；384 场=基线 8+47 值桶×8 种子，一次跑完无中断（断点续跑机制由 selftest §3 验证） | ✅ 384/384 |
| 4 | 报告+results 落盘；阳性对照敏感→方法学有效；08-28 附注已写（research/ 目录为新建） | ✅ |
| 阳性对照 | classifyRate=80：us_drops S=3.09 ≥3（rate50 S=1.06） | ✅ 敏感，方法学有效 |
| 5 收尾+质量检查 | build 0 警 0 错；test 812 通过/1 预期跳过（MujocoScoreEdgeGuard 官方种子）/0 失败 | ✅ |
| 检查修复（trellis-check） | ①**CR 数值段混比 bug 修复**：原 _level_agg 数值段只把 Δp/p 相对 0.75，但对照 mean 与噪声 std 仍用全局 null 基线，违背 PRD R1"分两条基线呈现，不混比"→ 修复为数值段对照桶与噪声尺度均用 0.75 段基线桶；重聚合后 CR S_max 1.32→0.53（0.5625 score_margin），裁决"弱/存疑"→"不敏感"、08-28 建议 CR 降为低优先级（混比虚高 18 倍效应）。②编码证据措辞统一为实证口径（Program.cs:43 不作用于 match，见 步1-2 修正）。③报告 CR 双段明细新增不混比说明行。检查过程：S 复算抽查吻合（阳性 3.0940 复现、latFrictionK 0.6548、STALL 逐位一致）、selftest 复跑 EXIT=0、dotnet build+test 全绿、git status 零 src/ 改动 | ✅ |
