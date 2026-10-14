# 敏感性分析 — 代码事实基线（2026-10-14 实测探明）

> 本文所有行号与机制均为本任务规划期实测核实，非转述。实施期若行号漂移以符号名为准。

## 1. 参数扰动通道（两条，全部走场景 JSON 变体）

### 1.1 `parameters` 字典 — SimParameters 级

- 入口：`src/Sim.Core/SimParameters.cs:42` `public static SimParameters FromDictionary(IReadOnlyDictionary<string, double>? source)`
- 机制：场景 JSON 顶层 `parameters` 对象，键为 legacy 大写/小写混合拼写，逐项覆盖默认值；**未知键名直接抛 ArgumentException**（SimParameters.cs:92-96，实施时键名拼写错误会立刻失败——这是好事）。
- 物理相关键（SimParameters.cs:10-30 默认值）：

| 键名 | 默认 | 归属子系统 |
|---|---|---|
| `BLOCK_MU_K` | 0.5 | friction（能量块库仑动摩擦） |
| `BLOCK_STICK_SPEED` | 0.02 | friction（块静摩擦粘住阈值 m/s） |
| `COLLISION_RESTITUTION` | null（速度相关公式） | collision（恢复系数；null=确定性回归验证路径） |
| `STALL_SPEED` | 0.03 | stall（堵转线速度阈值 m/s） |
| `STALL_TIME` | 0.4 s | stall |
| `STALL_RELEASE` | 0.06 m/s | stall |
| `STALL_DISPLACEMENT` | 0.006 m | stall |
| `MOUNT_V_MIN` | 0.3 m/s | mount（登台门槛，校验 (0,2]） |
| `MOUNT_ANGLE_MAX` | 0.26 rad | mount（入射角上限，校验 (0,1.2)） |
| `classifyRate` | 100 % | vision 随机桩（阳性对照候选；wire 名 camelCase，`SimParameters.cs:61` `case "classifyRate"`，Ordinal 大小写敏感——研究期误记 `CLASSIFY_RATE`，实测 `CLASSIFY_RATE` 抛 Unknown simulation parameter） |
| 其余（`MOUNT_SPEED`/`IR_TRIGGER`/`EDGE_THRESHOLD` 等） | — | FSM 决策参数，**不属于本任务扰动对象**（那是 param-optimization 的域） |

### 1.2 `vehicles` 字典 — VehicleProfile 级

- 入口：`src/Sim.Protocol/Scenario.cs:303` `public Dictionary<string, VehicleProfile> Vehicles`，键 = 角色（`us`/`them`）。
- 字段（`src/Sim.Protocol/Profiles.cs:236-311`，默认值）：`latFrictionK`=8（1/s，校验 0.5–60）、`angDamping`=3（1/s，0–40）、`mass`=1.0、`pushFactor`=1.0、`accelK`=12、`maxSpeed`=1.5、`maxTurnRate`=4.0、`wheelBase`=0.16、`trackWidth`=0.18、`shovelHeight`=0.015、`collisionRadius`=0.16。
- 校验：`Profiles.cs:313` `Validate()`（正数域/有限性逐字段，wire 名经 `ToWireName`）。
- 物理消费点：`src/Sim.Core/Physics.cs:1192-1193` `latF = exp(-(LatFrictionK!=0?…:8)·dt)` / `angF = exp(-(AngDamping!=0?…:3)·dt)`。

## 2. 指标解析 — 现成复用件

- `tools/param-optimization/oracle.py`（4332B）已封装：
  - `write_scenario(base, parameters, out)`：拷贝基础场景 + 覆盖 `parameters`（**只覆盖 parameters 字典，不含 vehicles**——本任务需自写 vehicles 覆盖变体生成，或扩展）；
  - `run_match(dotnet, dll, scenario, seed, duration)`：跑 `match --seed --scenario --duration --events`；
  - `parse_match_output(stdout)` → `{us_block_scores, us_drops, mount_t, done_reason, final_score}`。
- **编码坑（实测订正）**：CLI match 管道输出实为 **UTF-8**（实证 `[我方]` 首两字 = 字节 E6 88 91 E6 96 B9；gb18030 解码反而使归属全失效，us_drops 210→0）——`oracle.py:41` `encoding="gb18030", errors="replace"` 实为 **bug**（param-optimization 资产，本任务不修留裁决）。sensitivity.py 因此自写 `run_match_utf8`；`parse_match_output`/`write_scenario` 为纯函数可安全复用。研究期"输出 GBK/oracle 已处理"为误记。
- 事件行形状（oracle.py docstring 实录）：
  - `[  25] t= 236.00 BlockScore [我方] [score] 增益块被推下擂台! 我方 +3 (4:3)`
  - `[  19] t= 105.00 Drop [我方] [score] 我方掉台, 对方 +1 (0:1)`
  - 汇总行 `seed=42 ticks=2400 score 我方 8 : 3 对手 done=比赛时间结束 faults(us/them)=0/0`
- CLI：`src/Sim.Cli/Program.cs:341-350` — `match/batch [--seed|--seeds 1,2,3] [--scenario <path>] [--controller-us <cmd>] [--events]`。

## 3. 种子口径先例

- `tools/param-optimization/optimize.py`：TRAIN_SEEDS=[1,2,3,4]，holdout s5-s8；60s 筛选 / 120s holdout。
- 本任务（敏感性分析，非优化）沿用 s1-s8 全集，记录口径差异于 method 文档。

## 4. 扰动对象全集（来自 fidelity.json 根目录，2026-08-27 版）

- friction `uncalibrated`：`vehicle.latFrictionK`、`params.BLOCK_MU_K`
- collision `uncalibrated`：`vehicle.angDamping`、`params.COLLISION_RESTITUTION`
- stall `uncalibrated`：`params.STALL_SPEED`
- mount `hand_drawn`：`MOUNT_V_MIN`/`MOUNT_ANGLE_MAX`（参数显式化，calibrate 仅按分桶混淆矩阵验证门控）
- 服务对象：`.trellis/tasks/08-28-real-telemetry-capture-first-promotion`（status=planning；PRD R2 六类受控实验 / R5 选择性晋升）

## 5. 环境事实

- dotnet：`C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe`（optimize.py `default_dotnet()` 已内置该候选）。
- Sim.Cli.dll 构建产物路径：实施期以 `dotnet build src/Sim.Cli -c Release` 输出为准。
- 本机单场 60-120s 比赛耗时量级：param-optimization 历史经验（150 trial × 4 seed 60s 可在数小时内完成）。
