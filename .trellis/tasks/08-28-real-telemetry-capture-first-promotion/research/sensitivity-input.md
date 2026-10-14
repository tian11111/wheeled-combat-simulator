# 敏感性分析输入（交叉引用，10-14 任务附注）

> 证据来源：`.trellis/tasks/10-14-sim-physical-sensitivity`（OAT 局部敏感度）。**本文件已于 2026-10-14 更新为「引擎 P0 修复 + 度量口径修正后」的重跑结果**（原首轮报告的数字已失效，见报告头的失效率说明）。完整数据见其 `tools/sensitivity/report/sensitivity-results.json` 与 `sensitivity-report.md`。本文件仅为采集优先级建议摘要（证据行），**不替 08-28 拍板、不改其 PRD**。

## 采集优先级建议摘要（按 S_max 降序，修复后重跑）

| 参数 | 子系统 | S_max | 裁决 | 对 08-28 的建议 |
|---|---|---|---|---|
| `angDamping` | collision | **4.54** | 敏感 | **高优先级**：OAT 邻域内显著改变比赛指标（角阻尼→撞击后旋转衰减，直接进 `vehicle.angDamping`）。对应受控实验：撞墙/偏心对冲 before-after 角速度遥测 |
| `latFrictionK` | friction | **3.02** | 敏感 | **高优先级**：横向滑移衰减显著影响比赛形态（直接进 `vehicle.latFrictionK`）。对应受控实验：横向滑移衰减（lateral_coast）多初速覆盖 |
| `BLOCK_MU_K` | friction | 1.87 | 弱/存疑 | 中优先级：效应在种子噪声边缘，建议纳入采集并先以更大扰动范围复测 |
| `classifyRate` | vision（阳性对照） | 3.02 | 敏感（方法学自检通过） | 非采集对象本身，但印证"感知质量显著影响比赛"——真机视觉识别质量值得单列 |
| `COLLISION_RESTITUTION` | collision | 见报告双段明细 | 不敏感/弱 | 低优先级：null（速度相关公式）与数值双基线均不显著；恢复系数采集可暂缓 |
| `MOUNT_V_MIN` / `MOUNT_ANGLE_MAX` | mount | 0.82 / 0.82 | 不敏感 | 低优先级：±50% 邻域内淹没于种子噪声，建议暂缓（不排除大范围非线性） |
| `BLOCK_STICK_SPEED` / `STALL_SPEED` / `STALL_TIME` / `STALL_RELEASE` / `STALL_DISPLACEMENT` | friction / stall | 0.00 | 不敏感 | 低优先级：逐位一致（真零效应），采集可暂缓 |

## 关键变化（相对首轮，务必注意）

- **首轮的"整体降级"建议作废**：首轮报告在缺陷引擎上跑出（掉台判据几何误判 → 台沿抖动死循环送分 + ATTACK 僵持），且 `us_drops`/`mount_t` 两个指标口径有误（双计 / 10× 放大）。其"除阳性对照外无参数显著"是**被双稳态噪声掩盖的假阴性**。
- 修正后基线噪声大幅下降（us_drops 49.50±72.92 → **0.12±0.33**；score_margin −11.00±37.25 → **2.25±4.55**），真实效应显形：**latFrictionK 与 angDamping 双双达到显著（S≥3）**——恰是 fidelity.json 中 friction/collision 两子系统的待标定量。**这强化而非削弱了 08-28 对这两项的采集优先级。**
- 两个敏感性最高项（angDamping / latFrictionK）分别对应 `vehicle.angDamping` 与 `vehicle.latFrictionK`——08-28 的 `angular_coast` 与 `lateral_coast` 实验类型正是这两项的直接数据来源，建议按高优先级安排 fit + holdout 覆盖。

## 注意事项

- OAT 只测局部敏感度（±25%/±50% 邻域），S<1 不排除大范围非线性或交互效应。
- 合成数据不入标定/fidelity 晋升链（Calibrator 制度封死 synthetic）；本报告仅为采集优先级提供证据行。
- 首轮旧报告留档于 `tmp/sensitivity-old/report-old-engine.md`（本地，不入库）。
