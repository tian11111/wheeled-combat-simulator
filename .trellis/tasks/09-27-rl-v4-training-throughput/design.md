# 设计：五训练 seed 吞吐门槛

## 比较对象

同机、同场景、同代码与依赖，用训练 RNG seed `20260927–20261001` 各完成 500k transitions。一次“套件”的墙钟从首个进程启动到第五个有效模型及 manifest 落盘；三个完整套件的中位数用于 ≤60 分钟判定。重复套件不得筛掉慢或失败运行；失败须记录并使该套件无效，重测原因公开。

## 候选顺序

1. 五个独立 `n_envs=1` 训练进程并行；每个维持原 PPO rollout 2048 transitions/update 和单环境 `ScoreBlockEnv`。
2. 仅当第一候选未达目标，评估 `SubprocVecEnv` 的合理环境数。`n_steps × n_envs=2048`，训练 RNG 和 worker episode seed 流独立，checkpoint 按全局 transitions 触发。Windows worker 初始化须在安全的主入口下进行。
3. 仅当 profiling 报告显示 IPC/序列化≥25%、CPU 未饱和时，可在以上候选未达标后研究原生 .NET batch。每槽独立 episode runtime/`mjData`，模型复用只允许已验证的只读共享；固定动作轨迹、reset 与终止行为必须与单环境对照。

通过候选一即停止提速探索。候选二通过也不做原生 batch。任何候选在语义或故障检查失败时不能用速度数字声明通过。

## 身份与可复现性

训练入口显式接受训练 seed 和独立输出目录；默认路径仍单环境。manifest 包含代码、场景、CLI、split、依赖、硬件、PPO 参数、训练 RNG、每 worker episode seed 流、总 transitions、checkpoint cadence 和模型/checkpoint 哈希。计时套件重复生成的权重不得混用于后续选模；后续只可复用预先指定且身份完全一致的一个有效套件。

## 不变量与回退

`n_envs` 改变时，PPO update 的总 rollout 保持 2048 transitions，checkpoint 间隔按总 transitions 计算为 51,200。固定 seed 轨迹、Gymnasium reset/step/terminated/truncated 和裁判事件对照通过才算语义一致。候选失败可回退上一候选，不改变算法、reward、11 维观测、物理、FSM 或场景。
