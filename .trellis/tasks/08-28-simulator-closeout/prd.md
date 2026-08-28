# 仿真器交付收尾

## Goal

把两个历史对话中发现的四项交付缺口收敛为可独立验收的工作：传感器漂移、真机保真度门禁、Godot 窗口渲染冒烟和 GitHub 同步。保留现有确定性仿真行为，不用合成数据或未经确认的阈值制造“已标定”结论。

## Confirmed Facts

- 架构、Godot 桌面端、场地编辑器、telemetry-v1 标定工具和 MBri 传感器证据导入已经有实现提交；对应旧任务已归档。
- 当前 `main` 工作区干净，但本地分支比 `origin/main` 超前 7 个提交。
- `calibration/mbri-summer-sensor-report.json` 已记录灰度 `near_edge_enter`、前 ADC `diff_low`、铲子 `hang_enter` 的存档/重算/config 差异；导入流程只报告差异，不自动合并。
- `fidelity.json` 当前仍将摩擦、碰撞、堵转列为 `uncalibrated`，将登台列为 `hand_drawn`；这符合“真实遥测留出集达标后才晋升”的现有契约。
- Godot 编辑逻辑断言已通过，但历史 headless dummy renderer 截图失败，窗口版复验曾被 GUI 审批代理阻断。

## Requirements

### R1. 收敛 MBri 三处漂移

按采集批次、选择清单和模型语义分别分析灰度、前 ADC、铲子三处漂移，输出可审计的候选、适用范围、回放结果和拒绝理由。未经确认不得直接改写 MBri `config.py` 或模拟器运行时阈值。

### R2. 收敛真机保真度门禁

检查可用的 telemetry-v1 数据并运行拟合集/留出集验证；只有来源为真实且达到既有门禁的子系统才可更新 `fidelity.json` 或生成可应用 profile。没有真实物理遥测时不得用合成数据晋升。

### R3. 收敛 Godot 视觉门禁

完成 renderer-backed 的布局编辑冒烟与截图验证，区分编辑逻辑通过、headless 加载通过和真实窗口渲染通过三种证据；不得把 dummy renderer 的逻辑通过写成完整视觉通过。

### R4. 收敛仓库同步

在最终验证通过后核对本地与远端提交关系并完成 GitHub 同步。当前工作继续使用 `main`；只有确需新分支时才使用项目约定的 `test/` 前缀。

## Cross-Task Acceptance Criteria

- [x] 四个子任务均有独立 PRD、设计和执行清单，并按顺序通过各自验收。
- [x] `dotnet test`、seed-42 `replay-check` 和相关 Godot parity 不回退。
- [x] 漂移报告、保真度登记、视觉截图和 GitHub 远端状态相互一致且可追溯。
- [x] 最终工作区干净，远端包含本次应同步的提交（任务记录提交后再次复核）。

## Key Decisions

- 当前没有真实物理 telemetry-v1 时，第 2 项以“数据门禁和阻断报告完成、`fidelity.json` 保持诚实状态”收尾，不伪造参数或晋升结论。
- 继续使用当前 `main`，不新开分支；若执行中确需新分支，名称使用 `test/` 前缀。
- 执行顺序为：传感器漂移分析 → 物理保真度门禁 → Godot 窗口渲染 → 全量验证与 GitHub 同步。

## Out Of Scope

- 不重写仿真器架构、不引入数据库/联网服务、不替换 `Sim.Core` 权威物理。
- 不把传感器证据直接接入运行时 FSM，不自动选择“最新”文件覆盖旧配置。
- 不伪造缺失的真机位姿、速度、碰撞或登台标签；没有数据时如实保留未标定状态。
- 不制作新的美术资产；Godot 子任务只处理门禁与证据。
