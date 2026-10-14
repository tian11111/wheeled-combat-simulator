# Sim 物理参数敏感性分析

## Goal

量化各物理参数对比赛结果指标的影响排序（OAT 局部敏感度），产出可复现证据，为 08-28 真机遥测采集任务给出采集优先级建议、为未来 MATLAB DoE 提供输入。不改变模拟器任何行为。

## 背景与动机

- fidelity.json 中 friction/collision/stall 三个子系统未标定、mount 手绘——但"哪个参数值得优先采真机数据"目前无量化依据。
- 取舍裁决（2026-10-14 会话）：敏感性分析是"元方案"——它决定其余一切保真度投入（采集优先级/DoE 设计/标定资源）值不值。不敏感参数直接从采集清单销账。
- 现成基建：场景 JSON 双通道参数覆盖（`parameters` 字典 + `vehicles` 字典）、`tools/param-optimization/oracle.py` 指标解析可复用——本任务纯增量，不碰 src/。

## Requirements

### R1. 参数集（扰动对象）

核心 7 + 阳性对照 1（详见 research/sensitivity-method.md §2）：

| # | 参数 | 通道 | fidelity 子系统 |
|---|---|---|---|
| 1 | `latFrictionK`（基线 8） | vehicles.us/them 同值 | friction |
| 2 | `BLOCK_MU_K`（0.5） | parameters | friction |
| 3 | `angDamping`（3） | vehicles.us/them 同值 | collision |
| 4 | `COLLISION_RESTITUTION`（null） | parameters | collision |
| 5 | `STALL_SPEED`（0.03） | parameters | stall |
| 6 | `MOUNT_V_MIN`（0.3） | parameters | mount |
| 7 | `MOUNT_ANGLE_MAX`（0.26） | parameters | mount |
| 8 | `CLASSIFY_RATE`（100） | parameters | 阳性对照（vision 桩） |

- COLLISION_RESTITUTION 基线 null（速度相关公式）与显式数值**分两条基线**呈现，不混比（null→数值是模型路径切换，不只是数值扰动）。
- 第二梯队（预算允许才做）：`BLOCK_STICK_SPEED`、`STALL_TIME`、`STALL_RELEASE`、`STALL_DISPLACEMENT`。
- FSM 决策参数（MOUNT_SPEED/IR_TRIGGER 等）**明确排除**——那是 param-optimization 的域，不是物理保真度问题。

### R2. 扰动设计

- OAT（one-at-a-time）：每参数相对基线乘 ×{0.5, 0.75, 1.25, 1.5}（即 −50%/−25%/+25%/+50%）共 4 水平 + 基线，有界参数 clamp 到 VehicleProfile/SimParameters 合法域。（原文"±10%/±25%/±50% 共 4 水平"三百分比与 4 水平计数矛盾——实现按 design.md §3 唯一口径 ×{0.5,0.75,1.25,1.5} 执行并在报告配置头声明。）
- 双侧对称（vehicle 级 us/them 同值）——参数是同型车/场地属性。
- 种子 s1-s8 全集；比赛时长 120s（与正式判读口径一致）。

### R3. 指标

- oracle 五指标：`us_block_scores`/`us_drops`/`mount_t`/`final_score`/`done_reason` + 派生 `score_margin`、`mount_fail`。
- 聚合：跨种子均值±std；敏感度 S = 归一化 |Δ指标| / (|Δp/p| × 基线种子噪声)，**S<1=淹没在种子噪声（不敏感），S≥3=显著**；相变（done_reason/mount 状态跨种子翻转 ≥2/8）单独标注。
- 响应面拿不到的信息如实写边界：OAT 只测局部敏感度，不外推大范围、不分解交互项。

### R4. 工具与复用

- 新增 `tools/sensitivity/`（Python，一次性分析工具；参照 tools/param-optimization 先例）；**import 复用 oracle.py 的 run_match/parse_match_output/write_scenario 模式**（含 GB18030 编码坑规避），不重写解析。
- vehicles 字典覆盖是 oracle.write_scenario 不具备的——自写变体生成（拷贝基础场景 + 深拷贝覆盖 vehicles.us/them 指定字段）。
- 扰动表、种子集写死在脚本中（可复现），报告含完整配置头。

### R5. 产出

- `tools/sensitivity/report/sensitivity-report.md`：每参数一行（参数|子系统|通道|基线|各水平 S|裁决|对 08-28 的采集优先级建议），全表不隐藏弱指标；阳性对照不敏感时整表作废声明。
- 机器可读 `sensitivity-results.json`（全量原始指标 + 聚合）。
- 采集优先级结论以一条交叉引用附注到 08-28 任务 research/（**不改其 PRD、不替其拍板**）。

### R6. 边界（不做）

- 不改 src/ 任何行为、不进运行时/CI。
- 不做 MuJoCo 交叉（10-02 独立任务）、不做全局敏感度方法（Sobol）、不做优化搜索（那是 param-optimization）。
- 合成数据不进任何标定/fidelity 晋升链（Calibrator 制度封死 synthetic）。

## Acceptance Criteria

- [ ] `tools/sensitivity/` 工具自测通过：基线场景 s1 跑出的五指标与直接用 oracle.py 口径对拍一致。
- [ ] 8 参数 × 4 水平 × 8 种子全表跑完（或按 method §6 缩减档执行并在报告记录实际口径），`sensitivity-report.md` + `sensitivity-results.json` 落盘。
- [ ] 阳性对照（CLASSIFY_RATE）呈显著敏感；否则报告明示方法失效。
- [ ] 报告含每参数裁决（不敏感/弱/敏感/相变）+ 08-28 采集优先级建议一节 + 方法边界声明。
- [ ] 08-28 任务 research/ 收到交叉引用附注。
- [ ] `dotnet build`/`dotnet test` 全绿（本任务理论零代码改动，防意外触碰）。

## Notes

- 技术设计见 design.md；执行清单见 implement.md；代码事实依据见 research/baseline-facts.md、research/sensitivity-method.md。
