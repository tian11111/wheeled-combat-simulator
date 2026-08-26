# 重构武术擂台模拟器架构

## Goal

为 2026 RoboCup 武术擂台轮式格斗建立可维护、可复现、可替换控制器的桌面模拟器，使 3D 观察、无头评测和真实小车策略接入使用同一套比赛状态与规则语义。

## Background / Confirmed Facts

- 规则来源为 `D:/project/robocup/1779761830740288.pdf`：单场双方各一台自主轮式机器人；场地 3.8m×3.8m；中央擂台 2.4m×2.4m、高 6cm；比赛 2 分钟；擂台上有 2 个增益块和 1 个减益块。
- 规则核心行为包括自主登台、掉台后重登、推下对手、能量块计分、读秒、重启罚分、消极比赛和同时掉台判定。
- 现有原型位于 `D:/project/robocup/robot-simulator`，已经包含确定性核心、Three.js 3D 页面、Rapier 辅助碰撞、Node HTTP 服务、Python 策略桥、固定 seed 评测和诊断轨迹；它只作为行为和协议参考。
- 新实现目标目录为当前工程 `D:/project/robot-simulator`，不再以浏览器页面作为产品入口。
- 当前核心仍嵌在 `wushu_ring_sim.html` 的 HTML 脚本块中，并由 `build_3d.js` 注入生成 3D 页面；这会增加模块边界、类型约束和回归维护成本。
- `fidelity.json` 已明确区分规则验证、手绘灰度、随机视觉和未标定物理，模拟结果不能直接宣称为真机成绩。

## Requirements

- Godot 桌面客户端、.NET 无头模式和 Python 策略接入必须共享同一份比赛状态协议和规则结果。
- 比赛逻辑必须使用固定步长和可复现 seed；同一 seed、参数、车辆 profile 和动作序列应得到一致结果。
- 控制器、传感器 profile、视觉输入和物理实现必须可替换，且替换不需要修改裁判计分逻辑。
- 每次掉台、登台、碰撞、能量块下台、罚分和比赛结束都必须能以结构化事件追踪。
- 继续兼容现有 `decide(obs) -> {v,w}` 外部策略协议和当前无头评测入口，迁移应为渐进式而非一次性重写。
- 3D 引擎只负责显示和交互，不得成为比赛规则或判分的唯一来源。
- 第一阶段不要求实现完整分组赛/淘汰赛管理；这些属于独立的赛事编排层。

## Acceptance Criteria

- [ ] 可以在没有图形界面的情况下运行完整 2 分钟对战，并输出比分、状态和结构化事件。
- [ ] Godot 3D 客户端与 .NET 无头模式对同一 seed 的规则结果一致，渲染断开不会改变比赛结果。
- [ ] 可分别替换双方控制器，并保持现有 Python `decide(obs)` 接口可用。
- [ ] 固定 seed 回归、动作/观测协议和现有诊断轨迹没有无记录的破坏性变化。
- [ ] 规则核心、传感器、物理、裁判和渲染的职责边界在代码结构和文档中清晰可定位。
- [ ] 物理和视觉保真度状态继续通过 `fidelity.json` 对外声明，未标定部分不会被包装成真机结论。

## Scope Decision

- 第一阶段包含 Godot 桌面壳、.NET 核心拆分、固定时钟、协议/回放稳定；美术和高级视觉效果放到第二阶段。
- 第一阶段不改变现有外部 `decide(obs) -> {v,w}` 入口，不实现完整分组赛/淘汰赛管理，也不把未标定物理包装成真机结论。
- 不引入数据库或微服务；Python 控制器使用本机进程/JSONL 适配器，回放使用版本化文件。

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.
