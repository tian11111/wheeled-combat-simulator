# FSM 参数自动寻优(Optuna TPE × MuJoCo 确定性仿真)

## Goal

以确定性 MuJoCo 仿真为评估 oracle,用 Optuna TPE 自动寻优内置 FSM 的决策参数
(场景 `parameters` 白名单),提升共享 FSM 的绝对对抗能力:登台更快、推块出界更多、
掉台更少。对称参数框架(双方同参);不训练神经网络策略(09-25-mujoco-score-rl-pilot
保留 planning);不动 legacy;`fidelity.json` 不晋升。

## Confirmed Baseline

- 可零代码调参的 FSM 决策参数(SimParameters 白名单,mujoco 后端全部消费):
  `MOUNT_SPEED`(登台速度)、`IR_TRIGGER`(索敌触发)、`FALL_THRESHOLD`(登台完成
  判定)、`EDGE_THRESHOLD`(扫描避边)、`RECOVER_LIMIT`(恢复预算,整数)。
- 场景 `parameters` 为**全场共享**(单 SimParameters 驱动双方同 FSM),无按侧参数
  ——目标框架为对称能力优化(用户已确认)。
- 评估确定性:同参数同种子逐位可复现(batch 指纹已验证)→ 训练 seeds {1,2,3,4} /
  holdout seeds {5,6,7,8} 严格分离防过拟合。
- 吞吐:4 种子 × 60 s trial ≈ 1.2-2 s → 每小时 1800+ 试验,足够 TPE 数百代。
- 修复后基线:登台 3.2 s、首次真实 BlockScore t=236(23.6 s)、官方 120 s 8:3。

## Requirements

- R1 优化器为纯 Python 工具(`tools/param-optimization/`,仅 optuna 依赖),以
  `Sim.Cli match --events` 子进程为 oracle;每 trial 产出场景 JSON 与逐 seed 指标
  (CSV),可复现。
- R2 搜索空间:MOUNT_SPEED ∈ [500,1500](步10)、IR_TRIGGER ∈ [0.15,0.8]、
  FALL_THRESHOLD ∈ [80,400](步5)、EDGE_THRESHOLD ∈ [150,800](步10)、
  RECOVER_LIMIT ∈ [1,8]。classifyRate 不调(视觉真实性)。
- R3 目标函数(60 s 筛选、seeds 1-4):`Σ[1.0×我方BlockScore − 1.0×我方Drop]
  − 10×恢复超限`,次级记录首登台 t;任一 seed 崩溃/非有限 → trial 判负。
- R4 holdout 验证:top 参数在 seeds 5-8 × 120 s 上不劣于默认参数基线。
- R5 产出 `scenarios/wushu-ring-2026-mujoco-tuned.json`(独立文件,双方同参);
  官方场景、旧回放、legacy 默认值零改动。

## Acceptance Criteria

- [x] AC1 逐 trial 场景 JSON、命令、逐 seed 指标 CSV 可复现并存档。
- [x] AC2 Optuna study ≥150 trials 完成(60 s 筛选、seeds 1-4、TPE),含默认参数
  基线对照与逐 trial CSV。
- [x] AC3 top 参数在 holdout seeds(5-8)× 120 s 上主指标不劣于默认。
- [x] AC4 tuned 场景文件产出;官方场景与六份旧回放逐位不变;全套 dotnet test 通过。
- [x] AC5 报告含参数重要性、默认 vs 最优对比与诚实边界(对称参数/仿真自博弈/
  未标定);fidelity.json 与 legacy 零改动。

## Out of Scope

- 决策树阈值(相位定时器/对准阈值/守卫距离等 Fsm.cs 常量)的白名单化与优化
  ——需一次 CoreVersion bump,单列后续任务。
- 神经网络策略训练(见 09-25-mujoco-score-rl-pilot);真机参数标定(见 08-28)。
- 修改官方场景、裁判规则、legacy 默认值或 fidelity.json。
