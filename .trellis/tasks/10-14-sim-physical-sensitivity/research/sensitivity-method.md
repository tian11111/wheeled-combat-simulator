# 敏感性分析方法设计（OAT）

## 1. 方法选型：OAT（one-at-a-time）逐参数扰动

- **为什么 OAT 而不是全局方法（Sobol/FAST）**：本任务目标是给"真机采集优先级"排序（08-28 任务的输入），不是精确分解方差贡献；OAT 成本 = 参数数×水平数×种子数，一次可跑完；全局方法成本指数级且需大量样本，收益（交互效应分解）对当前决策（采集优先级）无增量。
- **方法边界（如实写进报告）**：OAT 只测局部敏感度（基线邻域），不能外推到参数大范围行为；交互项不显式测度，但跨水平非线性会显形（若 ±10% 与 ±50% 敏感度差异大 = 非线性提示）。

## 2. 参数集与对照

| 组 | 参数 | 通道 | 基线值 |
|---|---|---|---|
| friction | `latFrictionK` | vehicles.us/them | 8 (1/s) |
| friction | `BLOCK_MU_K` | parameters | 0.5 |
| collision | `angDamping` | vehicles.us/them | 3 (1/s) |
| collision | `COLLISION_RESTITUTION` | parameters | null→速度相关公式；**显式设数值后走固定恢复系数路径** |
| stall | `STALL_SPEED` | parameters | 0.03 m/s |
| mount | `MOUNT_V_MIN` | parameters | 0.3 m/s |
| mount | `MOUNT_ANGLE_MAX` | parameters | 0.26 rad |
| 阳性对照 | `CLASSIFY_RATE` | parameters | 100% → 扰到 50%/80%（已知会影响视觉识别 → 比赛形态应显著变化；若对照不敏感 = 方法学有 bug，全表作废） |

- 碰撞速度：正面/偏心碰撞必须实测 before/after 法向速度（08-28 PRD R2）；**COLLISION_RESTITUTION=null 是当前默认**，扰动它等于"改变物理模型路径"而不仅是数值——报告必须把 null 基线（速度相关公式）与固定值基线分开两行呈现，不可直接比较敏感度。
- BLOCK_STICK_SPEED / STALL_TIME / STALL_RELEASE / STALL_DISPLACEMENT：核心集之外，若扫描预算允许做第二梯队（见 §6 缩减档）。

## 3. 扰动设计

- **水平**：相对基线 ±10%、±25%、±50%（4 水平 + 基线 = 5 点/参数）。有界参数（MOUNT_V_MIN∈(0,2] 等）自动 clamp 到合法域。
- **双侧对称扰动**：vehicle 级参数 us/them 同值改（参数是同型车/场地属性——"该参数真实值不确定"的语义是两车一起不确定）；parameters 级天然全局。
- **种子**：s1-s8 全集（8 种子）。敏感性分析不做 train/holdout 分割（那是拟合评估的口径），但沿用同一种子池保证与 param-optimization 历史结果可比。

## 4. 响应指标（oracle 五指标 + 派生）

- 原始：`us_block_scores`、`us_drops`、`mount_t`（我方首次登台时间 s，None=未登台）、`final_score`（"我方X:Y对手"）、`done_reason`。
- 派生：
  - `score_margin` = 我方分 − 对手分（数值化 final_score）；
  - `mount_fail` = mount_t 为 None 时 1 否则 0（未登台是离散但关键的行为相变）。
- 每指标按"跨种子均值 ± std"聚合；`done_reason`/`mount_t` 按跨种子分布呈现（相变检测：任一水平下 ≥2/8 种子 done_reason 或 mount 状态翻转 = 单独标注）。

## 5. 敏感度定义（归一化，无量纲，跨参数可比）

对参数 p、水平 l、指标 m：

```
S(p,l,m) = | mean_m(p=l) − mean_m(p=baseline) | / ( |Δp/p_baseline| × scale_m )
```

- `scale_m`：指标自身的基线跨种子 std + 平滑项（防 0 除）——即敏感度以"基线噪声的倍数"为单位：**S<1 = 变化淹没在种子噪声里（不敏感）；S≥3 = 显著**。中间地带如实标"弱/存疑"。
- 参数级汇总敏感度 `S(p,m) = max over levels`（最坏水平，保守排序）。
- 最终排序按各指标最大 S；报告附全表（不隐藏弱指标）。

## 6. 预算与缩减档

- 全量：8 参数 × 4 水平 × 8 种子 = 256 场 + 基线 8 场 ≈ 264 场。120s/场约 ~10-20s wall-clock（按 param-optimization 历史经验估算，实施首日实测校准）→ 全量 1-1.5 小时量级。
- 缩减档（如果实测耗时超预期）：种子 s1-s4（4 种子）先跑全表 → 弱参数（S<1）不再加种子；强参数补 s5-s8。预算 ~150 场。
- 60s 时长 vs 120s：用 120s（正式比赛时长，与 08-28 判读口径一致；60s 筛选口径属 param-optimization 的 FSM 决策域）。

## 7. 结论产出形态（对 08-28 的接口）

报告每参数一行：参数名 | 子系统 | 通道 | 基线 | 各水平 S | 裁决（不敏感/弱/敏感/相变）| 对 08-28 的建议（如"block_push 采集优先级 P0/降级/维持"）。
- 不替 08-28 拍板，只给证据行；采集优先级建议同步附注到 08-28 任务 research/（一条交叉引用，不改其 PRD）。
