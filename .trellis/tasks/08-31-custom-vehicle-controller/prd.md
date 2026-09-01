# 自定义小车控制器接入

## Goal

让用户在 Godot 桌面端为我方/对手绑定自己的小车控制代码，并在发令前预检、启动和观察控制器状态，同时复用已有无头控制器协议和故障语义。

## Confirmed Baseline

- `src/Sim.Cli/PythonBridge.cs` 已实现外部进程 JSONL stdio：每帧发送 `Observation`，接收带可选 `requestId` 的 `{v,w}`，超时/坏行/死进程回退零动作并计 fault。
- `docs/CONTROLLER_PROTOCOL.md` 明确规定每场独立进程、请求匹配、有限值校验、超时与进程退出处理；`docs/CLI.md` 已支持 `--controller-us`/`--controller-them`。
- Godot 当前 `Main` 没有控制器命令配置字段或桌面端桥接入口；直接把控制器执行塞入 `Sim.Core` 会违反层次规范。

## Requirements

- R1：设置界面可为角色选择内置 FSM 或外部命令/脚本，显示路径、命令摘要、可执行性/协议预检结果和当前连接状态。
- R2：比赛开始前启动所需控制器；控制器进程与当前桌面会话/比赛绑定，退出、重置、回放切换和窗口关闭时可靠回收。
- R3：沿用 `PythonBridge` 的 JSONL、request-id、timeout、zero-action、fault 和有限值语义；故障可定位到角色且不冻结 UI。
- R4：外部命令只在桌面壳/CLI 边界执行，不写入 Scenario/Replay，不允许任意进程影响核心确定性；配置文件按版本化用户偏好保存。
- R5：至少覆盖正常控制器、缺失命令、坏响应、超时、退出和重置回收的自动化/集成验证。

## Acceptance Criteria

- [ ] 用户能在发令前看到双方控制器来源并成功启动一份外部控制器，动作能被比赛消费。
- [ ] 控制器启动失败、坏 JSONL、request-id 错配、超时和死进程均安全回退，UI 不冻结，故障角色可识别。
- [ ] 新场次/重置不会复用旧控制器进程；退出和异常路径无孤儿进程，既有 CLI batch/replay 行为不回归。

## Key Decision

- 已确认：MVP 使用外部脚本/命令选择与角色绑定，复用既有 JSONL stdio；不做 Godot 内嵌代码编辑器和进程内动态执行。这样复用最多、隔离最好，代价是代码需在外部编辑器维护。
