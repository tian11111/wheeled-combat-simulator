# Design — SCORE_BLOCK 失败原因诊断

## 1. 边界与不变量

| 项 | 约束 |
|---|---|
| `Sim.Core` 物理/裁判/计分 | **零改动**（本任务只读） |
| 观测维度（11）/奖励常量 | **零改动** |
| 训练/评测默认路径 | 行为与输出**逐位不变** |
| 允许的代码改动 | `rl-env` 可选 trace（显式开启才生效）+ 新增诊断脚本 + `gym_env` 一个默认关闭的开关 |
| 已揭示 seed 集 | 仅作分析，不得作门槛证据 |

设计核心：**把诊断所需的只读状态从内核里"取出来"，而不是把诊断逻辑塞进内核**。
`rl-env` 是训练专用桥（非版本化协议、非生产 FSM 路径），因此在其 `info` 下新增一个
可选子对象是加性、可回退的改动；内核一行不改。

## 2. 为什么需要新增可观测性

现有 `rl-env` 的 `info` 只给**计数器与布尔量**（`us_drops`、`unowned_block_offs`、
`target_last_contact_role`、`target_edge_distance`…），无法回答三个定位问题：

1. 掉台那一刻车离台沿多远、朝哪个方向、在推块还是在被顶；
2. 块出界那一 tick 的**原始接触记录**是什么（角色 + 接触时刻），从而区分
   "真双方争抢"与"单机器人多点接触被误判"；
3. 对手当时在哪（判断"同时接触"是否可能）。

这三项都需要逐 tick 的原始状态，因此新增 trace。

## 3. `rl-env` 可选 trace

### 3.1 触发协议（加性）

- `reset` 请求新增可选布尔 `trace`（缺省 `false`）。`state.Trace` 按集保存。
- `trace=false`（缺省）时：请求解析、响应 JSON、字段集合与顺序**与改动前完全一致**
  ——`Info` 字典不新增任何键。
- `trace=true` 时：`reset` 与 `step` 响应的 `info` 下新增 `trace` 对象。

### 3.2 trace 载荷（每 tick 一份）

```jsonc
{
  "tick": 1234,                 // engine.TickIndex
  "policy_ticks": 57,           // 阶段内已推进的 tick 数
  "seed": 6001,
  "us":   { "x","y","th","v","w","vx","vy","on_stage","edge_distance" },
  "them": { "x","y","th","v","w","vx","vy","on_stage","edge_distance" },
  "target_index": 0,            // -1 表示无锁定目标
  "blocks": [
    { "name","kind","x","y","vx","vy","out","was_on","edge_distance",
      "last_contact_role",
      "contacts": [ { "r": "us", "t": 0.045 }, ... ]   // 本 tick 原始接触记录
    }
  ],
  "events": [                    // 本 tick 新增的结构化裁判事件
    { "seq","tick","kind","role","is_us","neutral","block","reason" }
  ]
}
```

- `v`/`w` 为 `RobotRuntime.V/W`（**已被内核钳位后的请求值**），因此策略路径下它们就是被接受的动作；
  FSM 路径下是 FSM 的请求值。两条路径口径一致，可直接比较"动作"。
- `on_stage` 取 `IPhysicsBackend.OnStage(robot)`（**裁判口径**，与掉台判定同源）；
  `edge_distance` 取 `FieldModel.DistToNearestEdge`（与 FSM 防掉台阈值同源）。
- `contacts` 是 `BlockRuntime.ContactThisStep` 的只读投影（`Role` + `T`）。
  这是判定 `"simultaneous"` 真伪的**决定性证据**。
- `events` 复用 `Step` 中已计算的「自上一 tick 起的新增事件」列表；只有本 tick 的事件。
- `reason`/`block` 从 `CoreEvent.Data` 中取（与 `EventBlockName` 同一套序列化方式）；
  缺失时给空串，不抛异常。

### 3.3 实现位置

在 `RlEnvCommand` 内新增私有 `BuildTrace(engine, state, events)`，`Reset`/`Step` 在构造完 `info`
之后按 `state.Trace` 追加 `info["trace"]`。全部是**只读**访问，不消费随机流、不改状态，
因此不破坏确定性契约（同 seed 同动作序列仍逐位一致）。

## 4. `gym_env` 开关

`ScoreBlockEnv.__init__(..., trace: bool = False)`：为真时 `reset` 请求带 `"trace": true`。
缺省 `False` → 训练/评测路径请求体不变。trace 由服务端按集记忆，`step`/`step_fsm` 无需改动。

## 5. 诊断脚本 `controllers/score_block_rl/diagnose.py`

两个阶段，各自可单独运行，便于审查：

### 5.1 采集（`collect`）

对给定 seed 列表与模式（`policy` / `fsm` / `both`）逐集运行，把每 tick 的 `info.trace`
逐行写入 `<outdir>/traces/<mode>-<seed>.jsonl`，并追加一行 `episode` 汇总
（最终计数、终止原因、`no_score_block`、得分、掉台）。

- policy 模式：加载 SB3 模型，`deterministic=True`（与 `evaluate.py` 同口径）。
- fsm 模式：`step_fsm()`（与 `evaluate.py` 的基线口径一致，模型无关）。
- 守卫：单集 tick 上限、缺 `trace` 字段即报错（避免"看起来有数据其实没开"）、
  `--out` 已存在默认拒绝（`--force` 覆盖）。

### 5.2 分析（`analyze`）

只读 traces，产出 `<outdir>/diagnosis.json` 与人类可读摘要：

**掉台（R2）**：对每个我方 `Drop` 事件取窗口 `[t-25, t]`，计算
`edge_distance` 轨迹（最小/掉台时）、掉台时速度沿"向外法向"的分量、`|v|`、`|w|`、
是否在窗口内接触过目标块、对手是否在 0.5 m 内、掉台前是否在"块距台沿 <0.45 m"的推块段。
输出：逐条事实 + 客观标签的交叉计数 + 一个**规则明确、含"未定"档**的粗分类。

**块出界（R3）**：对每个 `BlockOff` 事件定位该块，回溯到**最后一次非空 `contacts`** 的 tick：

| 条件 | 分类 |
|---|---|
| 该块从未有任何接触记录（`last_contact_role` 空且 contacts 恒空） | `no_valid_contact` |
| 最后接触 tick 的最大 `t` 上只有**一个角色**且记录数 >1 | `simultaneous_self_<role>`（**误判**） |
| 最后接触 tick 的最大 `t` 上只有**一个角色**且记录数 =1 | `attributed_<role>`（正常，不应出现在无归属里） |
| 最大 `t` 上**两个角色都有** | `simultaneous_genuine` |

并用**对手车到该块的距离**与**我方车到该块的距离**作旁证；`simultaneous_self_*` 额外记录
该角色的接触点数量，量化"多点误判"的频次与 seed 清单。

**得分对照（R3 反证）**：对 `BlockScore` 事件做同样回溯，证明归属通道本身可用。

输出必须区分「已核对次数」与「估计」，不得把分类结果外推成未观测的结论。

## 6. 报告与证据

- 聚合结论、逐 seed 清单、代表样本（每类若干 tick 片段）与脚本命令进
  `.trellis/tasks/09-26-score-block-failure-diagnosis/`（`report.md` + `evidence/`）。
- 逐 tick 全量 traces 在 `%TEMP%`（体积原因），evidence 里放**聚合 JSON + 哈希 + 代表片段**。
- 模型/SDK/场景/CLI 哈希写入报告。

## 7. 风险与回退

| 风险 | 处理 |
|---|---|
| trace 影响默认路径 | 默认 `false`；回归用「同 seed 同 seed 对拍」验证默认输出一致 |
| trace 体积过大 | 只落 `%TEMP%`，逐 seed 分文件；`blocks.contacts` 仅 3 块 × ≤10 子步 |
| ABI/版本漂移 | 不触 `MujocoContacts`，只读已公开的运行时字段 |
| 分类过度解读 | 分类规则写死在脚本里并输出规则文本；设"未定"档；报告区分事实与推断 |
