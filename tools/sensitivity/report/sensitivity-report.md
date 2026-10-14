# Sim 物理参数敏感性报告（OAT 局部敏感度）

> 生成时间：2026-10-14 15:31:42

## 配置头

- git HEAD：`1250bb871198ea0495fad5fb22529166aac4aab4`
- dotnet：`C:\Users\Neco\AppData\Local\Programs\robot-simulator-dotnet\dotnet.exe`
- Sim.Cli.dll：`D:\project\robot-simulator\src\Sim.Cli\bin\Release\net8.0\Sim.Cli.dll`
- 基础场景：`D:\project\robot-simulator\scenarios\wushu-ring-2026.json`（vehicles/parameters 均为默认基线）
- 种子：s1–s8 全集（8 个）；比赛时长 120s（正式判读口径）
- 水平：相对基线 ×{0.75, 1.25, 0.5, 1.5}（−25%/+25%/−50%/+50%），有界参数 clamp 到校验域；**口径偏差**：PRD R2 写 “±10%/±25%/±50% 共 4 水平”，±10% 与 4 水平计数矛盾，按 design §3 唯一具体水平集（0.5×/0.75×/1.25×/1.5×）推广执行
- 阳性对照 classifyRate 用显式水平 80%/50%（method §2 指定；wire 名实测为 `classifyRate`，`CLASSIFY_RATE` 抛 Unknown simulation parameter）
- 第二梯队（BLOCK_STICK_SPEED/STALL_TIME/STALL_RELEASE/STALL_DISPLACEMENT）：纳入。依据：实测单场 wall ≈ 0.23s，预算远低于 method §6 估计（1–1.5h），符合 PRD R1 “预算允许才做”
- 编码口径：实测 Sim.Cli match 子进程管道输出为 UTF-8 字节（首行 "[我方]" 字节 E6 88 91 E6 96 B9；match 输出路径无显式编码设置，Program.cs:42-43 的显式 UTF-8 仅作用于 rl-env 命令）；oracle.run_match 的 gb18030 解码实测使 [我方]/[对手] 归属全失效（baseline s1 us_drops 210→0），故本工具自写 UTF-8 解码、指标解析复用 oracle.parse_match_output
- vehicles 部分字段覆盖（latFrictionK/angDamping camelCase wire 名）：ProtocolJson 对未知字段静默忽略，生效性由行为对比实测钉死（正确拼写改变行为、错误拼写与基线逐位一致）
- 基线跨种子指标：us_block_scores 1.50±0.50，us_drops 49.50±72.92，score_margin -11.00±37.25，mount_fail 0.00±0.00；登台 8/8

## 阳性对照（classifyRate）

**敏感 ⇒ 方法学有效**（S_max=3.09，裁决：敏感）。视觉识别成功率扰动显著改变比赛指标，扫描/解析/聚合链路按预期响应参数变化。

## 逐参数总表

| 参数 | 子系统 | 通道 | 基线 | 梯队 | 各水平 max-S | S_max | 相变 | 裁决 |
|---|---|---|---|---|---|---|---|---|
| `latFrictionK` | friction | vehicles | 8 | 1 | m25=6:1.25; p25=10:1.00; m50=4:0.47; p50=12:0.65 | 1.25 | 否 | 弱/存疑 |
| `BLOCK_MU_K` | friction | parameters | 0.5 | 1 | m25=0.375:1.88; p25=0.625:1.41; m50=0.25:1.18; p50=0.75:1.13 | 1.88 | 否 | 弱/存疑 |
| `BLOCK_STICK_SPEED` | friction | parameters | 0.02 | 2 | m25=0.015:0.00; p25=0.025:0.00; m50=0.01:0.00; p50=0.03:0.00 | 0.00 | 否 | 不敏感 |
| `angDamping` | collision | vehicles | 3 | 1 | m25=2.25:1.02; p25=3.75:1.56; m50=1.5:1.19; p50=4.5:1.71 | 1.71 | 否 | 弱/存疑 |
| `COLLISION_RESTITUTION` | collision | parameters | null→速度相关公式；数值基线 0.75（双段） | 1 | null→数值 m25=0.75:效应0.28; null→数值 p25=0.9:效应0.26; null→数值 m50=0.5:效应0.28; null→数值 p50=0.9:效应0.26; 数值基线 0.75 m25=0.5625:0.53; 数值基线 0.75 p25=0.9:0.27; 数值基线 0.75 m50=0.375:0.13; 数值基线 0.75 p50=0.9:0.27 | 0.53 | 否 | 不敏感 |
| `STALL_SPEED` | stall | parameters | 0.03 | 1 | m25=0.0225:0.00; p25=0.0375:0.00; m50=0.015:0.00; p50=0.045:0.00 | 0.00 | 否 | 不敏感 |
| `STALL_TIME` | stall | parameters | 0.4 | 2 | m25=0.3:0.00; p25=0.5:0.00; m50=0.2:0.00; p50=0.6:0.00 | 0.00 | 否 | 不敏感 |
| `STALL_RELEASE` | stall | parameters | 0.06 | 2 | m25=0.045:0.00; p25=0.075:0.00; m50=0.03:0.00; p50=0.09:0.00 | 0.00 | 否 | 不敏感 |
| `STALL_DISPLACEMENT` | stall | parameters | 0.006 | 2 | m25=0.0045:0.00; p25=0.0075:0.00; m50=0.003:0.00; p50=0.009:0.00 | 0.00 | 否 | 不敏感 |
| `MOUNT_V_MIN` | mount | parameters | 0.3 | 1 | m25=0.225:0.27; p25=0.375:0.82; m50=0.15:0.28; p50=0.45:0.19 | 0.82 | 否 | 不敏感 |
| `MOUNT_ANGLE_MAX` | mount | parameters | 0.26 | 1 | m25=0.195:0.27; p25=0.325:0.82; m50=0.13:0.68; p50=0.39:0.41 | 0.82 | 否 | 不敏感 |
| `classifyRate` | vision-阳性对照 | parameters | 100 | 1 | rate80=80:3.09; rate50=50:1.06 | 3.09 | 否 | 敏感 |

> S 定义见 method §5：|Δmean| / (|Δp/p| × (基线跨种子 std+ε))，以基线种子噪声倍数为单位；S<1 不敏感 · 1≤S<3 弱/存疑 · S≥3 敏感；∞ = 基线 std=0 但均值偏移非 0（相变级）。S_max = 各水平各指标最大 S（保守排序）。

## 各参数 × 各指标 max-S 全表（不隐藏弱指标）

| 参数 | 指标 | 基线 mean±std | max-S |
|---|---|---|---|
| `latFrictionK` | us_block_scores | 1.50±0.50 | 1.00 |
| `latFrictionK` | us_drops | 49.50±72.92 | 0.82 |
| `latFrictionK` | score_margin | -11.00±37.25 | 1.25 |
| `latFrictionK` | mount_fail | 0.00±0.00 | 0.00 |
| `BLOCK_MU_K` | us_block_scores | 1.50±0.50 | 1.00 |
| `BLOCK_MU_K` | us_drops | 49.50±72.92 | 1.88 |
| `BLOCK_MU_K` | score_margin | -11.00±37.25 | 1.61 |
| `BLOCK_MU_K` | mount_fail | 0.00±0.00 | 0.00 |
| `BLOCK_STICK_SPEED` | us_block_scores | 1.50±0.50 | 0.00 |
| `BLOCK_STICK_SPEED` | us_drops | 49.50±72.92 | 0.00 |
| `BLOCK_STICK_SPEED` | score_margin | -11.00±37.25 | 0.00 |
| `BLOCK_STICK_SPEED` | mount_fail | 0.00±0.00 | 0.00 |
| `angDamping` | us_block_scores | 1.50±0.50 | 0.00 |
| `angDamping` | us_drops | 49.50±72.92 | 1.71 |
| `angDamping` | score_margin | -11.00±37.25 | 1.46 |
| `angDamping` | mount_fail | 0.00±0.00 | 0.00 |
| `COLLISION_RESTITUTION` | us_block_scores | 1.50±0.50 | 0.00 |
| `COLLISION_RESTITUTION` | us_drops | 49.50±72.92 | 0.48 |
| `COLLISION_RESTITUTION` | score_margin | -11.00±37.25 | 0.53 |
| `COLLISION_RESTITUTION` | mount_fail | 0.00±0.00 | 0.00 |
| `STALL_SPEED` | us_block_scores | 1.50±0.50 | 0.00 |
| `STALL_SPEED` | us_drops | 49.50±72.92 | 0.00 |
| `STALL_SPEED` | score_margin | -11.00±37.25 | 0.00 |
| `STALL_SPEED` | mount_fail | 0.00±0.00 | 0.00 |
| `STALL_TIME` | us_block_scores | 1.50±0.50 | 0.00 |
| `STALL_TIME` | us_drops | 49.50±72.92 | 0.00 |
| `STALL_TIME` | score_margin | -11.00±37.25 | 0.00 |
| `STALL_TIME` | mount_fail | 0.00±0.00 | 0.00 |
| `STALL_RELEASE` | us_block_scores | 1.50±0.50 | 0.00 |
| `STALL_RELEASE` | us_drops | 49.50±72.92 | 0.00 |
| `STALL_RELEASE` | score_margin | -11.00±37.25 | 0.00 |
| `STALL_RELEASE` | mount_fail | 0.00±0.00 | 0.00 |
| `STALL_DISPLACEMENT` | us_block_scores | 1.50±0.50 | 0.00 |
| `STALL_DISPLACEMENT` | us_drops | 49.50±72.92 | 0.00 |
| `STALL_DISPLACEMENT` | score_margin | -11.00±37.25 | 0.00 |
| `STALL_DISPLACEMENT` | mount_fail | 0.00±0.00 | 0.00 |
| `MOUNT_V_MIN` | us_block_scores | 1.50±0.50 | 0.00 |
| `MOUNT_V_MIN` | us_drops | 49.50±72.92 | 0.82 |
| `MOUNT_V_MIN` | score_margin | -11.00±37.25 | 0.79 |
| `MOUNT_V_MIN` | mount_fail | 0.00±0.00 | 0.00 |
| `MOUNT_ANGLE_MAX` | us_block_scores | 1.50±0.50 | 0.00 |
| `MOUNT_ANGLE_MAX` | us_drops | 49.50±72.92 | 0.82 |
| `MOUNT_ANGLE_MAX` | score_margin | -11.00±37.25 | 0.71 |
| `MOUNT_ANGLE_MAX` | mount_fail | 0.00±0.00 | 0.00 |
| `classifyRate` | us_block_scores | 1.50±0.50 | 1.00 |
| `classifyRate` | us_drops | 49.50±72.92 | 3.09 |
| `classifyRate` | score_margin | -11.00±37.25 | 2.13 |
| `classifyRate` | mount_fail | 0.00±0.00 | 0.00 |

## COLLISION_RESTITUTION 双段明细（PRD R1：null 与数值基线不混比）

> null 段（模型路径切换）的效应以全局 null 基线噪声倍数呈现、不参与跨参数 S 排序；数值段（固定恢复系数路径内扰动）的对照桶与噪声尺度均为 **0.75 段基线桶**（不混入 null→0.75 的模型路径切换效应）。

### null→数值（模型路径切换，效应按噪声倍数呈现，不参与跨参数 S 排序）

| 水平 | 值 | Δp/p | us_block_scores | us_drops | score_margin | mount_fail | max-S/效应 | 相变 |
|---|---|---|---|---|---|---|---|---|
| m25 | 0.75 | —（路径切换） | 0.00 | 0.28 | 0.24 | 0.00 | 效应 0.28 | 否 |
| p25 | 0.9 | —（路径切换） | 0.00 | 0.26 | 0.19 | 0.00 | 效应 0.26 | 否 |
| m50 | 0.5 | —（路径切换） | 0.00 | 0.28 | 0.19 | 0.00 | 效应 0.28 | 否 |
| p50 | 0.9 | —（路径切换） | 0.00 | 0.26 | 0.19 | 0.00 | 效应 0.26 | 否 |

### 数值基线 0.75（固定恢复系数路径，S 与其他参数可比）

| 水平 | 值 | Δp/p | us_block_scores | us_drops | score_margin | mount_fail | max-S/效应 | 相变 |
|---|---|---|---|---|---|---|---|---|
| m25 | 0.5625 | 0.250 | 0.00 | 0.48 | 0.53 | 0.00 | 0.53 | 否 |
| p25 | 0.9 | 0.200 | 0.00 | 0.07 | 0.27 | 0.00 | 0.27 | 否 |
| m50 | 0.375 | 0.500 | 0.00 | 0.09 | 0.13 | 0.00 | 0.13 | 否 |
| p50 | 0.9 | 0.200 | 0.00 | 0.07 | 0.27 | 0.00 | 0.27 | 否 |

## 相变记录（done_reason / mount 状态翻转明细）

无（所有参数所有水平下 done_reason 与 mount 状态翻转均 < 2/8 种子）。

## 方法边界

- OAT 只测**局部敏感度**（基线邻域 ±25%/±50%），不能外推参数大范围行为；交互项不显式测度，但跨水平非线性会显形（若 −25% 与 −50% 的 S 差异大 = 非线性提示）。
- 敏感度以基线跨种子 std 为噪声尺度；S<1 仅意味 “±50% 邻域内该参数效应淹没于种子噪声”，不排除更大范围或交互效应。
- mount_t 数值不进 S 排序（None=未登台是离散相变，由 mount_fail/登台数呈现）；登台种子上的 mount_t 均值见 sensitivity-results.json。
- 合成数据不入任何标定/fidelity 晋升链（Calibrator 制度封死 synthetic）；本报告仅为 08-28 采集优先级提供证据行。

## 对 08-28 的采集优先级建议（证据行，不替 08-28 拍板）

| 参数 | 子系统 | S_max | 裁决 | 建议 |
|---|---|---|---|---|
| `classifyRate` | vision-阳性对照 | 3.09 | 敏感 | 高优先级：OAT 邻域内显著影响比赛指标，建议 08-28 优先安排对应受控采集 |
| `BLOCK_MU_K` | friction | 1.88 | 弱/存疑 | 中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测 |
| `angDamping` | collision | 1.71 | 弱/存疑 | 中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测 |
| `latFrictionK` | friction | 1.25 | 弱/存疑 | 中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测 |
| `MOUNT_ANGLE_MAX` | mount | 0.82 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `MOUNT_V_MIN` | mount | 0.82 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `COLLISION_RESTITUTION` | collision | 0.53 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `BLOCK_STICK_SPEED` | friction | 0.00 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `STALL_SPEED` | stall | 0.00 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `STALL_TIME` | stall | 0.00 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `STALL_RELEASE` | stall | 0.00 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |
| `STALL_DISPLACEMENT` | stall | 0.00 | 不敏感 | 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性） |

> 完整原始指标与逐水平聚合见同目录 `sensitivity-results.json`；变体场景 JSON 见 `variants/`（可由脚本确定性再生成，不入库）。
