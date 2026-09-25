# 自定义小车控制器发令前预检(缩范围后)

> 2026-09-25 缩范围说明:本任务原覆盖"桌面端自定义控制器接入"全流程。核对发现其主体
> (角色绑定 UI、每场独立生命周期与回收、fault/零动作语义、HUD 控制器状态条、故障路径
> 自动化测试)已由归档任务 `archive/2026-09/08-31-glass-settings-custom-controller`
> 的交付(提交 `1053e8d`:`SettingsPanel.cs`、`DesktopLiveDriver.cs`、
> `ExternalControllerBridge.cs` 及配套测试)实现并通过验收。唯一未覆盖的验收点是
> 原 PRD R1 的"可执行性/协议预检结果"——本任务缩范围为该缺口,原 PRD 全文见
> git 历史(本文件重写前的版本)。

## Goal

让用户在**发令前**就知道所配置的外部控制器命令能否启动并按协议应答:在设置页提供显式
预检动作,把预检结果显示在设置页与 HUD,避免开赛后才发现启动失败。

## Confirmed Baseline

- `godot/src/SettingsPanel.cs` 控制器页已支持双方角色选择 内置 FSM / 外部命令 并保存到
  `DesktopSettings` 用户偏好(不进 Scenario/回放)。
- `godot/src/DesktopLiveDriver.cs` 每场启动控制器进程,启动失败/超时/坏行回退零动作并计
  fault;`HudPanel` 有控制器状态条(含 fault 显示)。
- 协议语义(每场独立进程、JSONL、request-id、超时、有限值校验、zero-action、fault)由
  `Sim.Controller.ExternalControllerBridge` 与 `docs/CONTROLLER_PROTOCOL.md` 权威定义。
- 现状缺口:设置页没有"预检"动作;命令不可执行或不应答只能在发令后的 fault 里发现。

## Requirements

- R1:设置页控制器分区提供"预检"动作:校验命令可执行(可启动),并做一次协议握手
  (发送一帧 Observation、等待一个合法 `{v,w}` 响应或按超时判定),结果即时显示。
- R2:预检进程必须可靠回收,不得残留孤儿进程,不得影响随后的正式比赛或既有 fault 语义。
- R3:预检成功/失败(命令缺失、不可执行、坏 JSONL、request-id 错配、超时、立即退出)
  均有明确文案并可定位到角色;预检不改 Scenario/回放/fidelity,不进入比赛结果。
- R4:既有比赛启动路径、CLI batch/replay 行为与全部既有测试保持逐位不变。

## Acceptance Criteria

- [ ] 用户可在发令前对任一角色执行预检,并在 UI 看到 成功 / 失败原因(定位到角色)。
- [ ] 预检失败路径(缺命令、坏响应、超时、立即退出)有自动化覆盖;预检后无孤儿进程。
- [ ] 预检动作不改变正式比赛行为:既有全套测试与 parity 逐位不变。

## Key Decision

- 沿用既有 JSONL stdio 协议做握手,不发明新协议;预检是桌面壳能力,不进入 `Sim.Core`。
