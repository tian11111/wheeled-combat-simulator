# MuJoCo 双物理后端：实施清单

任务已获批准并进入 `in_progress`。按下述顺序实施；每项以对应验证证据关闭。

> 2026-09-25 换 agent 交接：当前工程实现和部分验证已完成，但最终验收未过。先读 [handoff.md](./handoff.md) 的证据、未完成项与下一步顺序；下列清单保持未勾选，避免把阶段性结果误记为完整验收。

## 执行前门禁

- [ ] 确认本任务覆盖的原有未提交变更（目前 `.learnings/LEARNINGS.md`、`.trellis/spec/frontend/component-guidelines.md`、`godot/src/ArenaVisualizer.cs` 与另一个未跟踪 Trellis 任务）均保留，不覆盖或顺手提交。
- [ ] 阅读 `implement.jsonl` 的 spec；依照 [Windows 依赖锁定记录](./research/mujoco-windows-lock.md) 核实官方发布包的下载、内含 DLL SHA-256、C API 和许可。归档 SHA-256 已从官方发布 API 核实，DLL 与许可清单须在下载后再锁定。
- [ ] 保存旧场景序列化、旧 replay-check、batch 固定指纹和 `dotnet test` 结果作为回归基线；确认测试环境有 .NET 8 与 Godot 4.7.2 .NET。

## 有序实施

1. [ ] 在 `Sim.Protocol` 加可选物理配置、回放身份、仅快照可见的三维姿态及 batch 新模式身份；补 JSON round-trip/未知模式/旧字节兼容测试，确认 `Observation` 不新增三维真值。默认场景与旧 DTO 的序列化不得变化。
2. [ ] 在 `Sim.Core` 抽出最小物理后端与工厂；`MatchEngine` 先建运行时实体再通过上下文创建后端，适配现有 `PhysicsWorld`，让 FSM/`MatchEngine` 使用接口。保留旧构造路径与旧 `Physics` 诊断属性；先跑完整旧测试、旧回放与旧 batch 指纹，若有漂移先修复。
3. [ ] 新建 `Sim.Mujoco`：官方 C API 薄封装、Windows x64 原生库加载/版本检查、确定性 MJCF 生成、每场 `mjModel/mjData` 生命周期、车轮驱动、固定子步、状态与接触映射。分别验证自由运动、边界、车车接触、推块、台阶、身份/旋转场地坐标往返和原生错误路径。
4. [ ] CLI/Godot 所有比赛创建、录制、回放、parity 与会话重建路径接入同一个模式选择/释放入口；batch 的场景/库预检在 worker 前完成，运行期错误仍按 seed 报告。
5. [ ] 快照与 `SnapshotView` 处理新姿态，Godot 用真实高度/朝向显示车和块；补旧快照回退及桌面实况/回放渲染验证。传感器仍保持解析模型，记录已知碰撞/探测差异。
6. [ ] 写成套新模式场景与测试；生成对照报告（接触/计分/复现/吞吐/内存）并更新架构、CLI、Godot 文档中的模式与保真度边界。不要晋升 `fidelity.json`。

## 验证与交付门禁

- [ ] `dotnet test`，`dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json`，旧 batch 固定向量和旧重启回放均通过。
- [ ] 新模式同机重复回放及 `--parallelism 1`/`4` 逐 seed 结果一致；32 worker 压力测试检查句柄释放、错误隔离和完整 N 行输出。测试不可用耗时比值证明并发，只记录吞吐与峰值内存。
- [ ] `godot --headless --path godot -- --parity-check <new-replay>` 与新模式 CLI 事件/比分一致；按 Godot 现有捕获流程检查两种窗口分辨率下的车体、块高度及翻转，临时截图不入库。
- [ ] 负例：坏模式/模型标记、缺 DLL、原生版本或模型哈希不匹配、非法 tick、单场异常均有明确错误且不静默回退；旧模式在无 MuJoCo 库环境仍可运行。
- [ ] 审阅 `prd.md` 每条 AC 的证据链接、性能对照和未达标项；确认只有规划批准后的实施改动被提交，且不宣称真机标定。
