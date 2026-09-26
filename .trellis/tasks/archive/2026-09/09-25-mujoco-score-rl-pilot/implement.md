# 实施清单：MuJoCo SCORE_BLOCK RL 试点

> 2026-09-26 已执行 `task.py start`，任务状态为 in_progress。SEARCH 前置门禁已通过。逐项记录可复核命令和原始结果；不要把奖励回报代替裁判事件。

1. [x] 前置门禁：`.trellis/tasks/09-25-mujoco-search-targeting/report.md` 已记录 AC4 的官方 seed 42 我方真实 `BlockScore`、无我方掉台，并补齐 AC5；这是已完成的规划依赖，不需为开始编写训练桥重复跑整场。
2. [x] 已保存 20 冷/100 热建模原始样本及 100 完整 reset/1000 step 样本，见 `evidence/perf-reset-2026-09-26.json`、`evidence/perf-full-2026-09-26.json`。
3. [x] 训练专用持久 CLI 已实现；阶段事件隔离、锁定目标归因、原生资源释放顺序已补定向测试。`step_fsm` 仅供同阶段基线评测。
4. [x] Gymnasium 单环境、固定 11 维观测/动作、明确异常、锁定依赖已实现；11 维版本的 Gymnasium `check_env`、seed 42 两次 reset/固定 100 动作逐帧复现，以及 C# 观测定向测试均已通过，见 report.md。
5. [x] 20 冷/100 热断言编译次数 20、模型身份变化另编译且 p95 比率低于 0.5；100 次完整 reset 与 1000 策略 step 数据已保存。新增 101 次 reset 的进程句柄/内存审计及异常恢复/重复 close 检查，边界见 report.md。
6. [x] 干净虚拟环境安装、`pip check`、UTF-8 2,048 步烟测与默认 500,000 步训练通过；实际 501,760 步，模型、版本、配置、728 行 UTF-8 逐集回报保存至 `%TEMP%`，评测重新加载成功。
7. [x] 3001–3010 开发集上，干净环境 51,200 步短训模型满足预定选模门槛，500,000 步模型目标得分 0；已冻结短训模型并首次运行 4001–4010。最终策略得分 1 对 FSM 0，但我方掉台 8 对 FSM 7，**AC4 失败**；保持任务 `in_progress`，不换模型或重用最终集调参。
8. [ ] 验证与交付：运行新增定向训练桥/环境测试、dotnet test RobotSimulator.sln -m:1、既有 MuJoCo integration/protocol tests 与 MuJoCo 新回放 replay-check；运行 replays/*.json legacy 回放检查。执行 git diff --check，并在模型复用通过后把训练专用例外写入 .trellis/spec/sim/index.md，同时审阅 .trellis/spec/backend/index.md。报告列明是否通过 AC2/AC4、策略使用 privileged state、依赖/模型哈希、reset/step 吞吐、各 seed 比较、失败场次及未验证项；不得晋升 fidelity.json。
