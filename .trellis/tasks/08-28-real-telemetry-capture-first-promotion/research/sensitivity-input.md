# 敏感性分析输入（交叉引用，10-14 任务附注）

> 证据来源：`.trellis/tasks/10-14-sim-physical-sensitivity`（OAT 局部敏感度，git HEAD 1250bb871198ea0495fad5fb22529166aac4aab4）；完整数据见其 `tools/sensitivity/report/sensitivity-results.json` 与 `sensitivity-report.md`。本文件仅为采集优先级建议摘要（证据行），**不替 08-28 拍板、不改其 PRD**。

## 采集优先级建议摘要（按 S_max 降序）

- `classifyRate`（vision-阳性对照）：S_max=3.09，裁决 敏感 —— 高优先级：OAT 邻域内显著影响比赛指标，建议 08-28 优先安排对应受控采集
- `BLOCK_MU_K`（friction）：S_max=1.88，裁决 弱/存疑 —— 中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测
- `angDamping`（collision）：S_max=1.71，裁决 弱/存疑 —— 中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测
- `latFrictionK`（friction）：S_max=1.25，裁决 弱/存疑 —— 中优先级：效应在种子噪声边缘（1≤S<3），建议 08-28 酌情纳入或先以更大扰动范围复测
- `MOUNT_ANGLE_MAX`（mount）：S_max=0.82，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `MOUNT_V_MIN`（mount）：S_max=0.82，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `COLLISION_RESTITUTION`（collision）：S_max=0.53，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `BLOCK_STICK_SPEED`（friction）：S_max=0.00，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `STALL_SPEED`（stall）：S_max=0.00，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `STALL_TIME`（stall）：S_max=0.00，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `STALL_RELEASE`（stall）：S_max=0.00，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）
- `STALL_DISPLACEMENT`（stall）：S_max=0.00，裁决 不敏感 —— 低优先级：OAT ±50% 邻域内效应淹没于种子噪声，建议 08-28 评估暂缓采集（OAT 只测局部敏感度，不排除大范围非线性）

## 注意事项

- OAT 只测局部敏感度（±25%/±50% 邻域），S<1 不排除大范围非线性；敏感参数建议对应受控实验（碰撞 before/after 法向速度、摩擦、堵转、登台成败）优先采集。
- 合成数据不入标定/fidelity 晋升链（Calibrator 制度封死 synthetic）。
