# 2026-09-25 当前代码证据与待验证项

## 已由当前 checkout 代码确认

- `src/Sim.Core/Sensors.cs:103-145`：探针按 range + 目标半径过滤，`Probe.D = 中心距离 - 半径`；`src/Sim.Protocol/Profiles.cs:303-340` 默认 `legacy14` 对角通道量程 1.6 m。
- `src/Sim.Core/Fsm.cs:595-635,671-735`：SEARCH 以真实 `Probe` 与逻辑红外值选台上目标；`turn` 以 0.15 rad 对准，3 s 超时。`Fsm.cs:739-775`：buff → SCORE_BLOCK，opponent → ATTACK。
- `src/Sim.Core/Fsm.cs:707` 的发现日志格式化 `Probe.D`；一位小数舍入不能解释默认对角探针显示 4.0 m。旧 `src/Sim.Tests/fixtures/godot-parity-seed42.json` 和归档报告中的 4.0 m 来源有待核对。
- `src/Sim.Mujoco/MujocoPhysicsBackend.cs:100-150`：`V/W` 映射至轮目标速度，限速 80；纵向命令先经斜坡。`src/Sim.Mujoco/MujocoModel.cs:15-27`：轮力上限 3.0、伺服 kv 0.25。
- `src/Sim.Core/MatchEngine.cs:608-626`：采样传感器、FSM 决策、推进物理依次进行。`MatchEngine.cs:265-283` 暴露受控试验所需运行态。
- `src/Sim.Hosting/MatchEngineHost.cs:19-49`：MuJoCo 回放仅检查后端、原生版本和 MJCF 哈希，未检查 `CoreVersion`。`src/Sim.Cli/Program.cs:183-239` 的 replay-check 比较事件指纹与最终比分。
- `src/Sim.Tests/MujocoIntegrationTests.cs:151-217`：官方出生点内置 FSM 测试止于 SEARCH，未验收分类与得分。

## 历史观察，当前仍需重测

- 本任务的原诊断记载原地 `W=2` 实际偏航约 0.068 rad/s，整体提高 kv 与降低摩擦损害登台，弧线机动出现掉台/绕块。任务目录没有原始逐 tick 轨迹，不能将数值当作本轮已独立验证结果。
- 归档登台验收的测试、旧回放、Godot parity 与 batch 证明登台修复当时的状态，不证明本次 SEARCH 修复完成。
