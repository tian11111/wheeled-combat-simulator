# 设计:FSM 参数自动寻优

## 架构

```
Optuna TPE study (optimize.py)
  → trial 参数向量 θ (5 维: MOUNT_SPEED/IR_TRIGGER/FALL_THRESHOLD/EDGE_THRESHOLD/RECOVER_LIMIT)
  → oracle.py: 写 per-trial 场景 JSON(base + parameters 覆盖)
  → Sim.Cli match --events 子进程(逐 seed, 60 s 筛选)
  → objective.py: 解析事件流 → 指标 → 目标值
  → study 记录 + trial CSV 落盘
```

- oracle 为纯子进程封装(`dotnet exec Sim.Cli.dll match --events`),不改产品代码。
- 事件解析按行匹配:`BlockScore`+`[我方]` → 我方推块得分;`Drop`+`[我方]` → 我方
  掉台;`已上台 on_stage`+`[我方]` → 首登台 t;末行 `done=` → 终局原因。
- 确定性:同 θ 同 seeds 逐位可复现,目标值无采样噪声 → TPE 可靠收敛。
- 试验预算:60 s 筛选(搜索)+ 120 s(holdout 终验);训练 seeds 1-4,holdout 5-8。

## 目标函数

```
J(θ) = Σ_seeds∈{1..4} [ 1.0 × BlockScore_us − 1.0 × Drop_us ] − 10 × 1{恢复超限}
次级: 首登台 t(越小越好, 仅记录不进主目标——避免与推块目标冲突)
崩溃/非有限 → trial 返回 None(pruned/failed)
```

## 搜索空间(5 维,白名单内)

| 参数 | 范围 | 类型 | 行为 |
|---|---|---|---|
| MOUNT_SPEED | 500-1500 (步10) | int | 倒车登台速度 |
| IR_TRIGGER | 0.15-0.8 | float | 索敌/避障触发 |
| FALL_THRESHOLD | 80-400 (步5) | int | 登台完成灰度判定 |
| EDGE_THRESHOLD | 150-800 (步10) | int | 扫描避边触发 |
| RECOVER_LIMIT | 1-8 | int | 恢复预算(超限=停车) |

## 边界

- 官方场景/旧回放/legacy 默认值/fidelity.json 零改动;tuned 参数只写入独立场景文件。
- classifyRate 不调(视觉真实性);决策树阈值白名单化为后续任务(需 CoreVersion bump)。
- 调优结果表述为"仿真自博弈工程寻优",不宣称真机保真。
