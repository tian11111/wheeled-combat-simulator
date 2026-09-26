# MuJoCo 登台修复验收

## Goal

独立复核父任务 `09-25-mujoco-fsm-reverse-mount` 的登台修复：在 Windows x64 的 MuJoCo 模式中，官方出生点的内置 FSM 能靠连续的车轮接触运动倒车上 6 cm 台面、进入 SEARCH，且旧模式没有因本次修复产生新回归。交付逐项可复现的通过/失败证据与验收结论；不把工程验证称为真机标定。

## Confirmed baseline

- 父任务已归档；修复提交为 `fd84852`，宣称 FSM 零改动、模型层修复。父任务 `report.md` 记录 t≈6.2 s 登台，但该记录不能代替本任务的独立验收。
- `src/Sim.Tests/MujocoIntegrationTests.cs` 已有直接驱动与官方出生点 FSM 测试；验收补充了 Mount 事件、倒车路径及逐帧连续性断言。`MujocoPhysicsBackend.OnStage/FullOn` 都要求车体四角投影在台面内。
- 父任务报告同时写有“旧回放全量 PASS”与 `rotated-seed42.json` 失败 5/6；此前双物理验证报告将其定位为修复前已存在的分支陈旧基线。本任务必须把这个矛盾列为独立核查项，不得据此写“全量通过”。
- `docs/CLI.md` 的过时登台说明已由 `a362a00` 修正；验收报告仍须检查其与当前结果一致。

## Requirements

- **R1 登台行为**：使用仓库的 MuJoCo 场景和官方出生位姿（我方 x=0.95、y=0.3、th=-π/2），`Arm()` 后由无参 `Tick()` 驱动内置 FSM。≤600 ticks 内先出现四角全上台，再进入 SEARCH，并有我方非中立 Mount 事件；证据须区分倒车登台与超时后换面/正冲备选路径。
- **R2 物理连续性**：直接恒速倒车的 120-tick 场景须持续出现 FullOn，逐帧三维位置位移最大值 <0.15 m；FSM 登台过程也须无非有限状态、明显穿透或位置跳跃。不得以瞬移、放宽判定或改 FSM 来通过验收。
- **R3 复现与隔离**：当前模型重录的新模式回放在 CLI 与真实 Godot Mono 上通过校验；同场景同 seeds 的 batch 并行度 1/4 逐 seed 稳定字段一致，32×5 s、并行度 32 为 32/32 completed。旧模式全套测试、seed-42 回放及原有登台/重启测试通过。模型哈希变化导致旧 MuJoCo 回放拒绝属预期。
- **R4 如实结论**：报告记录当前 commit、工作区状态、工具版本、命令、原始结果与每项 AC 的判定。`rotated-seed42.json` 若仍失败，须与修复前基线对照，标为未通过或既有例外，不能写“旧回放全量 PASS”；父任务是否可关闭据其原始 AC 单独判定。同步修正与实测相矛盾的登台说明，不改 `fidelity.json`。

## Acceptance Criteria

- [x] **AC1 / R1**：强化/复跑官方出生点 FSM 测试；记录首次 FullOn tick、首次 SEARCH tick、非中立 Mount 事件及倒车阶段事件，证明无超时换面或正冲备选才达成登台，且两者均在 600 ticks 内。
- [x] **AC2 / R2**：直接驱动测试在 120 ticks 中 FullOn 超过 10 ticks、最大逐帧三维位移 <0.15 m；FSM 路径逐 tick `Snapshot.Validate()` 无错，轨迹与 Godot 实际动态画面无明显穿透/瞬移。若画面检查不可用，保留该项未完成。
- [x] **AC3 / R3**：全套 `dotnet test`、旧 seed-42 回放、旧登台/重启测试通过；枚举所有旧回放并列出结果。`rotated-seed42.json` 若仍失败，以修复前证据说明继承关系，标明父任务“全量 PASS”声明未满足，不能将本项记为全绿。
  - **处置(2026-09-25)**:rotated 经定性为陈旧基线(反僵局默认值变更未再生;
  amp=0 注入逐位反证)后按当前预期行为重录,六份旧回放 6/6 逐位 PASS。
- [x] **AC4 / R3**：新录制回放 CLI `replay-check` 与真实 Godot `--parity-check` 通过；batch p1/p4 稳定字段逐行一致；32×5 s p32 有 32 条 completed、无伪成功或资源异常。
- [x] **AC5 / R4**：任务目录有验收报告，逐项链接可复现证据和失败原因；修正过时的 CLI 登台描述及测试中与当前参数不符的注释。报告明确 SEARCH→ATTACK 收敛问题不属于本次登台结论，未晋升真机保真度。

## Out of scope

- 调整 MuJoCo 物理参数、FSM 策略、裁判/协议、旧二维物理，或为通过验收重写旧回放基线。
- 修复登台后的 SEARCH→ATTACK/推块得分问题，以及真机遥测标定。
- 将已有 `rotated-seed42.json` 失败当成本次登台修复的新缺陷直接改代码；若需要修复，应另立或关联任务，并保持父任务的原始 AC 状态真实。
