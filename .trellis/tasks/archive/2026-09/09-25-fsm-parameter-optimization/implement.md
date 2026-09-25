# 实施清单:FSM 参数自动寻优

1. [x] tools/param-optimization 三件套(oracle/objective/optimize)+ requirements。
2. [x] 默认参数基线:seeds 1-4/5-8 × 60s/120s,存任务目录。
3. [x] Optuna study ≥150 trials(60s 筛选、seeds 1-4),逐 trial CSV。
4. [x] holdout 验证(top 参数, seeds 5-8 × 120s)。
5. [x] tuned 场景文件 + 回归(全套/官方场景/旧回放)。
6. [x] 报告(参数重要性/对比表/边界)+ 提交。
