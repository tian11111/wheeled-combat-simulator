# SCORE_BLOCK 失败原因诊断报告

任务：`09-26-score-block-failure-diagnosis`　状态：诊断完成　日期：2026-09-26
性质：**只诊断、不修复**。本报告不改动 `Sim.Core` 物理/裁判/计分、观测、奖励、门槛、默认 FSM、`fidelity.json`。

---

## 0. 结论摘要

1. **"块出界但未获得我方归属得分"已 100% 定位为 `Sim.Core` 归属判定缺陷，不是物理随机性，也不是双方争抢。**
   两轮盲验、两条路径合计 14 次 `BlockOff`，**14 次全部是单台机器人的多点接触被误判为 `simultaneous`**；
   用原始接触记录独立核实后，**真正的双方争抢为 0 次**。
   同一份代码在 max 接触时刻恰好只有 1 条记录时归属正常：27 次 `BlockScore` **全部**是
   `single_contact_us`/`single_contact_them`。判别式因此非常干净：

   > `FinalizeBlockContacts` 用的是**记录条数** `last.Count == 1`，而不是**不同角色数**。
   > 单台机器人有多个几何体（MuJoCo 后端不做几何对去重），同一子步产生 ≥2 条同角色记录 →
   > 被判 `"simultaneous"` → 不计分。

2. **量化影响（只报可核对的次数）**：若只修此归属判定，6001–6050 上策略的锁定目标得分
   **5 → 9**（其中 4 次为被我方推出界、被误判的 Buff 目标块），FSM 8 → 8；
   4001–4010 上 FSM **0 → 1**（第一轮 FSM 也丢了一次合法 +3）。掉台数不变。

3. **但单独修此项不足以通过门槛。** 门槛要求"得分不低于 FSM"**且**"掉台不高于 FSM"。
   归属修复后 6001–6050 得分条件由失败（5 < 8）转为通过（9 ≥ 8），
   **掉台条件仍失败（28 > 21）**。这一点必须如实记录，不得用修复后的推算值宣称门槛通过。

4. **掉台缺口的实测原因是"到沿速度与转角"，而不是"是否在推块"。**
   6001–6050 上策略 28 次掉台 vs FSM 21 次，两者的"窗口内接触目标块"比例几乎相同
   （68% vs 71%，**无区分度**）；真正区分的是到沿速度：
   **速度 > 0.4 m/s 的掉台占 82%（策略）vs 14%（FSM）**，掉台瞬间速度四分位
   0.474/0.670/0.863（max 0.938）vs 0.082/0.134/0.320（max 0.542）；
   **93% vs 29%** 的掉台伴随 |w| > 0.2；且策略在 79% 的掉台时刻已在指令 `v < -0.5`（紧急倒车）——
   说明**策略有刹车意图，是动量把它带下去的**，而不是"没有防掉台意识"。
   另有 **28 次中的 8 次发生在完全相同的 tick 283**，是可复现的系统性失效模式，不是随机噪声。

5. **单一候选修复（R4）**：修 `Physics.FinalizeBlockContacts` 的归属判定（按"不同角色数"而非"记录条数"）。
   理由见 §4；被否候选的反向证据同样在 §4。

6. **纪律**：6001–6050 与 4001–4010 在本报告中**仅为分析输入**，未被当作任何模型的盲验集，
   也未被用于任何门槛判定。诊断未启动训练、未改门槛、未删样本。
   下一轮的 seed 划分建议见 §7。

---

## 1. 诊断方法与可复现性（AC1 / AC2 / R1 / R6）

### 1.1 可观测性改动（默认关闭）

唯一的生产代码改动是 `rl-env` 的**可选、显式开启**轨迹输出（`src/Sim.Cli/RlEnvCommand.cs`）：

- `{"op":"reset","seed":N}` → 行为与响应结构与改动前**逐位一致**（不含 `trace` 键）。
- `{"op":"reset","seed":N,"trace":true}` → 响应 `info` 中额外附带 `trace` 对象：
  逐 tick 的 `us`/`them`（位置/朝向/速度/指令/on-stage/到台沿距离）、
  `blocks[]`（名称/类型/位置/速度/`out`/`was_on`/`last_contact_role`/**本 tick 原始接触记录 `[{r,t}]`**）、
  以及本 tick 的结构化裁判事件（`seq`/`tick`/`kind`/`role`/`is_us`/`neutral`/`block`/`reason`）。

Python 侧 `gym_env.ScoreBlockEnv(trace=False)` 默认关闭，仅诊断脚本显式打开。
未改任何物理、裁判、计分、观测维度或奖励常量。

**默认路径一致性（AC6）**——`diagnose.py default-off-check`：同一 seed 6001、
同一脚本动作序列（`v=0.6*sin(0.05t)`、`w=0.3*cos(0.03t)`）各跑一次（关闭 / 开启），
比对 147 行：

```json
{ "rows_compared": 147, "trace_absent_when_off": true, "trace_present_when_on": true,
  "problems": [], "identical": true }
```

即：观测、奖励、`terminated`/`truncated`、除 `trace` 外的全部 `info` 字段完全一致。

### 1.2 采集范围（AC2）

| 轮次 | seed 集 | 路径 | episode 数 | 四类量 |
| --- | --- | --- | --- | --- |
| 第二轮盲验（已揭示） | 6001–6050 | 策略 + FSM | 50 + 50 | 全部采集 |
| 第一轮盲验（已揭示） | 4001–4010 | 策略 + FSM | 10 + 10 | 全部采集 |

四类量均已采集：**动作**、**车与台沿距离**、**块接触（含原始接触记录）**、**裁判事件**。

缺项显式说明（R1 要求"不得静默省略"）：

- **FSM 路径的"动作"记为 `null`**：FSM 由引擎内置，不接受策略动作，无可记录的外部指令；
  但 FSM 路径仍记录了引擎内该 tick 实际生效的 `us.v`/`us.w`，对照因此仍是同口径的。
- 全量逐 tick 轨迹（120 个 `.jsonl.gz`，24.6 MB）**不入 Git**，保留在
  `%TEMP%\score-block-rl-diagnose-20260926\traces\`；入库的是聚合结论、逐事件清单与决定性摘录。

### 1.3 关键校验：轨迹必须复现既有盲验结果（R6 门槛前置条件）

用 `diagnose.py analyze --compare-recorded` 把轨迹重算的计数器与归档的盲验原始结果逐 seed 比对，
比对字段为 `locked_target_us_block_scores`、`us_drops`、`unowned_block_offs`、`policy_ticks`、
`total_reward`（后者浮点容差 1e-6）：

| 归档结果文件 | SHA-256 | 对数 | 不一致 |
| --- | --- | --- | --- |
| `final-holdout-v2.json`（第二轮） | `38776f8457480b1135797c7017c78ba4dc06b864896e58f5f134ea58342c7df0` | 100 | **0** |
| `final-holdout.json`（第一轮） | `6deb1b0aee7d6aec4f18be1b2d42e6f003450a285583e0ad2307101df1f8fc14` | 20 | **0** |

**120/120 对全部一致，含 `total_reward` 与 `policy_ticks`。** 这同时证明：开启轨迹
**没有扰动仿真**——逐位依赖的动作序列、奖励累计与 episode 长度都与改动前的归档值相同。
若此处出现不一致，本报告的结论一律作废（`implement.md` 的停止条件）。

### 1.4 输入与依赖哈希（AC1）

| 对象 | SHA-256 |
| --- | --- |
| 场景 `scenarios/wushu-ring-2026-mujoco.json` | `52ec978e001e0ca09b1ce4b3620d89e356cbc70ffccd8b9ad9e8389bbecff7d3` |
| CLI `src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll` | `a60be975860989959e43aaf58076ed78001dbd3a44d8c4221302918e3a7ca7e2` |
| 第二轮冻结候选 `rl_model_307200_steps.zip` | `6ed360757633d63553978285e1455c980f66993a3685ae727ef6ddc4ed2bd829` |
| 第一轮冻结候选 `ppo_score_block.zip` | `b943a92d2bee0095cd2be04535be07086ea4dbbeb61915afe15c4c23860104b7` |
| `controllers/score_block_rl/diagnose.py` | `9fc9d10efab30ac62fab4a2fe4a8aa6799d619524b0af0abfdfde3685dbac535` |
| `controllers/score_block_rl/gym_env.py` | `49b3b171125ff54b1d34819da656e1135b527ccc1933a0f0a821396a38412357` |
| `src/Sim.Cli/RlEnvCommand.cs` | `638a6ed0b657ec85b8177cd418239ccc78a608d644ec66fca801b90721e1362b` |
| `src/Sim.Core/Physics.cs`（**未改动**，缺陷所在） | `9d7c385ffd10321a46ba00466c6a997672a6085b38c084858c5c6f93414fec3e` |
| `src/Sim.Core/MatchEngine.cs`（**未改动**） | `8ea187d32b497e28dd2f1e9a81fdeccf8e7e9b7311182e3a6b396143c17f5dbd` |

运行时：Python 3.12.10、numpy 2.5.0、gymnasium 1.3.0、stable-baselines3 2.9.0、torch 2.13.0+cpu。

### 1.5 复现命令

```powershell
$py = "$env:TEMP\score-block-rl-venv-11d\Scripts\python.exe"
$dotnet = "$env:TEMP\robot-simulator-dotnet-sdk\dotnet.exe"
$out = "$env:TEMP\score-block-rl-diagnose-20260926"
$r2model = "$env:TEMP\score-block-rl-v2-500k-20260926\checkpoints\rl_model_307200_steps.zip"
$r1model = "$env:TEMP\robot-simulator-rl-11d-50k-clean-20260926\ppo_score_block.zip"
$ev = ".trellis\tasks\09-26-score-block-failure-diagnosis\evidence"

# 1) 采集
& $py -X utf8 controllers/score_block_rl/diagnose.py collect --mode both --split final_holdout_v2 `
  --model $r2model --dotnet $dotnet --out $out --force
& $py -X utf8 controllers/score_block_rl/diagnose.py collect --mode both --split legacy_final_holdout `
  --tag legacy_round1 --model $r1model --dotnet $dotnet --out $out --force

# 2) 分析（含与归档结果的逐 seed 复现核对）
& $py -X utf8 controllers/score_block_rl/diagnose.py analyze --out $out --tag final_holdout_v2 `
  --compare-recorded "$ev\..\..\archive\2026-09\09-26-score-block-ppo-checkpoint-round\evidence\final-holdout-v2.json"
& $py -X utf8 controllers/score_block_rl/diagnose.py analyze --out $out --tag legacy_round1 `
  --compare-recorded "$ev\..\..\archive\2026-09\09-25-mujoco-score-rl-pilot\evidence\final-holdout.json"

# 3) 默认关闭一致性与原始摘录
& $py -X utf8 controllers/score_block_rl/diagnose.py default-off-check --seed 6001 --ticks 300 --dotnet $dotnet --out $out
& $py -X utf8 controllers/score_block_rl/diagnose.py excerpts --out $out
```

---

## 2. "块出界但未归属得分"的原因（R3 / AC4）

### 2.1 判定链（代码事实）

1. MuJoCo 后端每个裁判 tick 跑 `SubstepsPerTick = 10` 个子步，`contactTime = (i+1)*0.005`；
   `MujocoContacts.Visit` 遍历 **全部 `ncon` 接触且不做几何对去重**；
   单台机器人拥有多个几何体，因此**同一子步可产生多条同角色记录**。
2. `Physics.FinalizeBlockContacts`：取本 tick 内接触时刻最大者 `maxT`，再取
   `last = ContactThisStep.Where(|c.T - maxT| <= 1e-6)`，然后
   **`o.LastContactRole = last.Count == 1 ? last[0].Role : "simultaneous";`**
   ← 缺陷行：判据是**记录条数**，不是**不同角色数**。
3. `MatchEngine.ObjFallCheckAll`：增益块离开擂台时，只有 `LastContactRole` 恰为单一角色
   （`us`/`them`）才 +3；`"simultaneous"` 与 `null` 都发 `BlockOff` 且**不计分**。

### 2.2 原始接触记录独立核实（R3 要求）

对本报告覆盖的全部 41 次块出界事件，用**原始接触记录**重新分类
（规则：回溯到出界 tick 及之前最近一条有接触记录的 tick；取该 tick 内 max 接触时刻的记录集；
按**不同角色数**判定）：

| 轮次 / 路径 | `BlockScore` 次数 | 分类 | `BlockOff` 次数 | 分类 | 真·双方争抢 |
| --- | --- | --- | --- | --- | --- |
| 6001–6050 策略 | 11 | 全部 `single_contact_*` | 10 | 全部 `multi_point_same_robot_*` | **0** |
| 6001–6050 FSM | 15 | 全部 `single_contact_*` | 1 | `multi_point_same_robot_them` | **0** |
| 4001–4010 策略 | 1 | 全部 `single_contact_*` | 2 | 全部 `multi_point_same_robot_them` | **0** |
| 4001–4010 FSM | 0 | — | 1 | `multi_point_same_robot_us` | **0** |
| **合计** | **27** | 15 `us` + 12 `them`，无一例外 | **14** | 9 `them` + 5 `us` | **0** |

判为 `"simultaneous"` 的 14 次中，**单机器人误判占 100%**；真双方争抢 **0 次**。
另有 `no_valid_contact`（完全无接触记录）**0 次**——两类原因中的另一类在本数据上未出现。

### 2.3 逐事件清单（14 次 `BlockOff`）

`max 时刻记录数` = max 接触时刻处的记录条数（>1 即触发误判）；`操作方` = 该记录集的角色。

| 轮次 | 路径 | seed | tick | 块类型 | 锁定目标 | 操作方 | max 时刻记录数 | 裁判原因 | 我方距块 | 对手距块 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 6001–6050 | policy | 6011 | 353 | Buff | 否 | them | 2 | simultaneous | 0.816 | 0.461 |
| 6001–6050 | policy | **6022** | 338 | Buff | **是** | **us** | 4 | simultaneous | 0.817 | 0.415 |
| 6001–6050 | policy | 6029 | 395 | Buff | 否 | them | 2 | simultaneous | 1.403 | 0.388 |
| 6001–6050 | policy | 6031 | 354 | Buff | 否 | them | 2 | simultaneous | 0.622 | 0.318 |
| 6001–6050 | policy | **6037** | 317 | Buff | **是** | **us** | 2 | simultaneous | **0.218** | **1.782** |
| 6001–6050 | policy | **6040** | 288 | Buff | **是** | **us** | 2 | simultaneous | **0.232** | 0.632 |
| 6001–6050 | policy | 6043 | 396 | Buff | 是 | them | 2 | simultaneous | 0.769 | 0.295 |
| 6001–6050 | policy | 6047 | 330 | Buff | 否 | them | 2 | simultaneous | 0.818 | 0.322 |
| 6001–6050 | policy | **6048** | 260 | Buff | **是** | **us** | 4 | simultaneous | **0.223** | **2.164** |
| 6001–6050 | policy | 6049 | 558 | Buff | 否 | them | 2 | simultaneous | 0.590 | 0.305 |
| 6001–6050 | fsm | 6012 | 395 | Buff | 否 | them | 2 | simultaneous | 2.084 | 0.335 |
| 4001–4010 | policy | 4005 | 553 | Buff | 否 | them | 2 | simultaneous | 1.688 | 0.321 |
| 4001–4010 | policy | 4008 | 324 | Buff | 否 | them | 2 | simultaneous | 1.338 | 0.248 |
| 4001–4010 | fsm | **4005** | 349 | Buff | **是** | **us** | 2 | simultaneous | **0.353** | **2.431** |

**旁证（R3 要求的对手距离）**：加粗三行是决定性的——我方距块 0.218 / 0.223 / 0.353 m，
**对手距块 1.782 / 2.164 / 2.431 m**。把"对手在 2 m 外"判为"双方同时接触"在物理上不成立。

### 2.4 对照组：同一份代码、同一后端，归属成功的情形

| seed / 路径 | tick | 本 tick 接触记录总数 | **max 接触时刻记录数** | 不同角色 | 结果 |
| --- | --- | --- | --- | --- | --- |
| 6001 / policy | 292 | **10** | **1** | `us` | `BlockScore` +3（锁定目标） |
| 6037 / policy | 317 | 8 | **2** | `us` | `BlockOff`，**0 分** |
| 6048 / policy | 260 | 17 | **4** | `us` | `BlockOff`，**0 分** |

6001 的 tick 292 有 **10 条**接触记录却归属成功——因为**只有 1 条落在 max 接触时刻**。
6037/6048 的接触记录总数并不更多，却有 2/4 条**并列在 max 接触时刻**。
**判别式完全落在"max 时刻是否恰好 1 条记录"上**，与接触总量、与对手距离都无关：
这排除了"物理上真的同时争抢"这一解释。

原始摘录见 `evidence/decisive-excerpt.md`（4 个样本，含上述三种情形）。

### 2.5 量化影响（AC4：只报可核对的次数）

"若只修归属判定"的投影（可核对：仅把 `multi_point_same_robot_<role>` 按 `<role>` 记为有效归属，
其余复算；**未重新训练、未重跑仿真**）：

| 轮次 / 路径 | 现锁定目标得分 | 修复后预测 | 其中"被我方推出界被误判"的 seed | 掉台（不受影响） |
| --- | --- | --- | --- | --- |
| 6001–6050 策略 | 5 | **9** | 6022, 6037, 6040, 6048 | 28 |
| 6001–6050 FSM | 8 | 8 | — | 21 |
| 4001–4010 策略 | 1 | 1 | — | 8 |
| 4001–4010 FSM | 0 | **1** | 4005 | 7 |

修复后**无归属 `BlockOff` 在这两个留出集上全部归零**（14 次全部获得真实归属），
但其中 9 次归属对手、5 次归属我方——因此"无归属归零"不等于"我方得分增加"，
本报告只把**锁定目标的我方得分**计入门槛投影。

对门槛的含义（**必须如实说明**）：

- 6001–6050 的"得分不低于 FSM"条件：**5 < 8（失败）→ 9 ≥ 8（通过）**；
- 6001–6050 的"掉台不高于 FSM"条件：**28 > 21，不变，仍失败**；
- 4001–4010：得分 1 ≥ 1 通过不变，掉台 8 > 7 仍失败。

**因此：归属缺陷是真实且可证的 bug，但它单独不足以让门槛通过；掉台是当前的约束瓶颈。**

### 2.6 该缺陷同时污染训练奖励（额外的量化后果）

`RlEnvCommand` 的奖励逻辑（`src/Sim.Cli/RlEnvCommand.cs:286-302`）：

```
targetScored  -> reward += TargetReward (+1.0)
targetLost && targetBlockOff -> reward += NotOursPenalty (-0.5)
```

而 `targetScored` 只认**真实的 `BlockScore` 事件**。于是被我方推出界却被误判为
`simultaneous` 的目标块，拿到的是 **−0.5 而不是 +1.0**——单个事件上 **1.5 的奖励方向性错误**。
在 6001–6050 上，这发生在 4 个 episode（45 个非 `no_score_block` episode 中的约 9%）。
更关键的是与塑形项自相矛盾：`EdgeShapingScale` 是**奖励把块推向台沿**（`+0.1*(edgeBefore-edgeAfter)`），
而推成功时代理却因归属误判被罚 −0.5。这一矛盾信号会随训练步数持续累积，
属于"归属缺陷"的后果，不需要单独列为第二个问题。

---

## 3. 掉台原因定位（R2 / AC3）

### 3.1 逐条事实与分类计数

每一次我方 `Drop` 都记录了：掉台 tick、掉台前 25 tick（1.25 s）窗口内的位置与到台沿距离轨迹、
当时的命令 `(v, w)` 与实际速度、是否处于目标块接触/推块过程、是否伴随对手接近、掉台瞬间是否仍在台上。
逐条明细表见 §3.4；分类计数（同口径，含"未定"档）：

| 分类 | 6001–6050 策略 | 6001–6050 FSM | 4001–4010 策略 | 4001–4010 FSM |
| --- | --- | --- | --- | --- |
| `pushing_target_inside_slow_zone`（推块且窗口内进入 0.45 m） | 19 | 15 | 0 | 6 |
| `driving_outward_fast`（外向速度 > 0.15 且速度 > 0.25） | 4 | 1 | 1 | 0 |
| `slow_drift_over_edge`（外向速度 ≤ 0.15） | 4 | 3 | 5 | 1 |
| `pushed_out_by_opponent_contact`（对手 < 0.42 m 且外向速度 > 0.05） | 1 | 2 | 1 | 0 |
| **`unclassified`（未定，不强行归类）** | **0** | **0** | **1**（seed 4005） | **0** |
| 合计 | 28 | 21 | 8 | 7 |

分类规则是**启发式、且已文档化**（阈值写死在 `diagnose.py`），因此本报告以**原始测量量**为准，
分类计数只作索引。`unclassified` 档如实保留（4001–4010 seed 4005：min_edge 0.189、速度 0.244、
|w| 0.997、窗口内未接触目标块、对手不近——不符合任何一条规则，故不归因）。

### 3.2 与 FSM 基线的同口径对照（第一轮 + 第二轮）

| 指标 | 6001–6050 策略 | 6001–6050 FSM | 4001–4010 策略 | 4001–4010 FSM |
| --- | --- | --- | --- | --- |
| 掉台次数 | 28 | 21 | 8 | 7 |
| 掉台瞬间速度中位 | **0.670** | **0.134** | 0.217 | 0.150 |
| 掉台瞬间速度四分位 | 0.474 / 0.670 / 0.863（max 0.938） | 0.082 / 0.134 / 0.320（max 0.542） | — | — |
| 速度 > 0.4 m/s 的占比 | **82%** (23/28) | **14%** (3/21) | 12% (1/8) | 0% (0/7) |
| \|w\| > 0.2 的占比 | **93%** (26/28) | **29%** (6/21) | 88% (7/8) | 43% (3/7) |
| 指令 `v < -0.5`（紧急倒车）的占比 | **79%** (22/28) | **0%** (0/21) | 12% (1/8) | 0% (0/7) |
| 25 tick 窗口内进入 < 0.27 m | 28/28 (100%) | 21/21 (100%) | 8/8 | 7/7 |
| 窗口内接触目标块 | 19/28 (**68%**) | 15/21 (**71%**) | 0/8 | 6/7 |
| 窗口内 min_edge 中位 | 0.187 | 0.252 | 0.220 | 0.251 |

### 3.3 结论：区分掉台的是"到沿速度与转角"，不是"是否推块"

三条独立读数指向同一结论：

1. **"是否在推目标块"没有区分度**：68% vs 71%。这否定了"策略因为推块而掉台、FSM 不推所以不掉"
   的直觉假设。FSM 也推块掉台（15/21），**双方都是推着块贴边掉台**。
2. **到沿速度有强区分度**：> 0.4 m/s 的掉台 82% vs 14%，中位速度差 **5.0 倍**。
   FSM 之所以慢，是因为它有一条策略侧没有的纪律：目标块距台沿 < 0.45 m 时把推进速度
   从 0.9 降到 0.35（`ScoreTick`），以及 `ReturnFromMujocoScoreEdge` 的 0.27 m 倒车/回中。
3. **策略不是"没有防掉台意图"，而是刹车太晚**：79% 的掉台瞬间指令已是 `v < -0.5`（多为 −1.0 满舵倒车），
   但此时的实测速度中位仍有 0.670 m/s——**动量已无法在剩余 0.187 m 内收回**。
   换言之，掉台在**进入刹车之前**就已经被决定了：问题在**接近台沿时的速度/转角控制**，不在刹车逻辑。

补充事实：6001–6050 策略的 28 次掉台中有 **8 次发生在完全相同的 tick 283**
（min_edge 中位约 0.187、速度 0.52–0.94 m/s、多数伴随推块）。同一 tick 重复出现说明这是一个
**可复现的系统性失效模式**（多 seed 在 SCORE_BLOCK 入口后收敛到同一状态），而非随机物理噪声。

诚实边界：第一轮策略的掉台形态与第二轮**不同**（0% 在推块、88% 伴随 |w| > 0.2、
速度仅 0.217 m/s 中位）——第一轮更像"贴边转向时滑落"，第二轮更像"高速冲沿"。
两轮用的是不同 checkpoint（第一轮 51,200 步、第二轮 307,200 步），
而两轮的 seed 集也不同，因此**跨轮比较只能是描述性的，不能当作代码/训练步数的因果证据**。

### 3.4 逐条事实（策略路径，6001–6050，28 次）

| seed | tick | min_edge(25t) | 速度 | \|w\| | 指令 v | 接触目标 | 分类 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 6002 | 306 | 0.152 | 0.542 | 0.477 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6003 | 283 | 0.188 | 0.938 | 0.235 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6005 | 283 | 0.181 | 0.868 | 0.372 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6009 | 1096 | 0.151 | 0.912 | 0.848 | −0.990 | 否 | driving_outward_fast |
| 6010 | 316 | 0.189 | 0.431 | 0.768 | −0.363 | 否 | driving_outward_fast |
| 6012 | 283 | 0.182 | 0.681 | 0.403 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6013 | 320 | 0.230 | 0.318 | 0.596 | +1.000 | 否 | pushed_out_by_opponent_contact |
| 6017 | 283 | 0.186 | 0.522 | 0.207 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6019 | 253 | 0.187 | 0.762 | 0.370 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6020 | 283 | 0.182 | 0.681 | 0.403 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6023 | 441 | 0.252 | 0.004 | 0.463 | +1.000 | 否 | slow_drift_over_edge |
| 6024 | 408 | 0.187 | 0.460 | 0.218 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6025 | 421 | 0.175 | 0.927 | 1.699 | −1.000 | 否 | driving_outward_fast |
| 6026 | 255 | 0.177 | 0.596 | 0.227 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6027 | 968 | 0.173 | 0.925 | 0.423 | −1.000 | 否 | driving_outward_fast |
| 6028 | 254 | 0.194 | 0.744 | 0.449 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6030 | 305 | 0.259 | 0.078 | 0.289 | +1.000 | 否 | slow_drift_over_edge |
| 6031 | 1651 | 0.185 | 0.649 | 0.009 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6032 | 283 | 0.195 | 0.850 | 0.376 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6034 | 260 | 0.176 | 0.713 | 0.302 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6035 | 283 | 0.188 | 0.938 | 0.235 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6039 | 283 | 0.187 | 0.516 | 0.186 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6041 | 267 | 0.192 | 0.659 | 0.359 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6042 | 252 | 0.186 | 0.901 | 0.521 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6044 | 308 | 0.255 | 0.773 | 1.265 | +0.966 | 是 | pushing_target_inside_slow_zone |
| 6045 | 1044 | 0.242 | 0.010 | 0.509 | −1.000 | 否 | slow_drift_over_edge |
| 6048 | 260 | 0.173 | 0.629 | 0.368 | −1.000 | 是 | pushing_target_inside_slow_zone |
| 6050 | 368 | 0.244 | 0.220 | 0.408 | +1.000 | 否 | slow_drift_over_edge |

`min_edge(25t)` = 掉台 tick 及之前 25 tick 窗口内我方到最近台沿的最小距离。

---

## 4. 单一候选修复（R4 / AC5）

### 4.1 推荐：修 `Physics.FinalizeBlockContacts` 的归属判定

**类别**：`Sim.Core` 归属判定缺陷（不是奖励/观测缺口，不是策略行为缺口，也不是评测口径问题）。

**改法**：把 max 接触时刻处的判据从**记录条数**改为**不同角色数**：

```
last = ContactThisStep.Where(|c.T - maxT| <= 1e-6)
roles = last.Select(c => c.Role).Distinct()
o.LastContactRole = roles.Count() == 1 ? roles.Single() : "simultaneous";
```

**它解释的缺口**：全部 14 次 `BlockOff`（100%）。定量地，6001–6050 上策略锁定目标得分 5 → 9，
第一轮 FSM 0 → 1。

**预测的可观测变化**：`BlockOff` 中 `reason="simultaneous"` 的次数在我方单机器人推块出界时
应降为 0；对应变成 `BlockScore`。`us_drops`、`policy_ticks`、车辆轨迹**均不受影响**
（归属只在块出界那一个 tick 决定计分与否，不反馈进物理）。

**如何验证（不需要训练）**：用**已揭示**的 6001–6050 / 4001–4010 重放同一批冻结模型，
比对 `locked_target_us_block_scores` 与 `unowned_block_offs`（预测：
6001–6050 策略 5 → 9、无归属 10 → **0**（其中 4 次转为对我方计分，6 次转为对对手计分）；
6001–6050 FSM 8 → 8、无归属 1 → **0**；4001–4010 策略无归属 2 → 0；4001–4010 FSM 0 → 1），
并新增一个 `Sim.Core` 单测：
"单机器人多几何体在同一子步产生多条同角色接触时归属该角色，而非 `simultaneous`"。
这两项都不需要重训、不消耗任何未见过的 seed。

**对回放与确定性契约的影响**：

- legacy 2D 后端每台机器人每 tick 恰好贡献 1 条记录，`Distinct()` 是**空操作** →
  既有 `replays/*.json` 应保持逐位一致（§6 已实测 6/6 PASS）。
- MuJoCo 后端的既有回放若包含此类出界事件，归属结论会改变，属**修正**而非回归；
  需按项目流程重新录制的回放应显式说明，不得静默覆盖。
- 确定性契约（同 seed + 同动作序列 ⇒ 逐位相同的事件/得分）不变：判定仍是纯函数，
  无新增随机源。

### 4.2 为什么更强、且必须排在第一位

1. **它是缺陷，不是选择**：把"对手在 2.164 m 外"记成"双方同时接触"在任何口径下都不成立（§2.3）。
2. **它是当前唯一可 100% 证明、且可零成本验证的结论**：其余候选都只能给出相关性。
3. **它污染奖励（§2.6）**：不先修它就去调奖励，等于在**错误的奖励信号**上做超参/塑形搜索；
   修完后"推动成功"的奖励才第一次与塑形项方向一致。
4. **它同时影响 FSM 基线**：第一轮 FSM 也丢了一次 +3。基线不公平时，任何"策略 vs FSM"的比较都不可靠。

### 4.3 被否 / 排后的候选与反向证据

| 候选 | 为何排后 | 反向证据 |
| --- | --- | --- |
| **奖励/观测缺口：给我方车加"靠近台沿"惩罚或增加边距观测** | 是**能力缺口**而非缺陷；且必须先有干净的奖励基线 | (a) "是否推块"无区分度（68% vs 71%），说明该候选缺机制假设，加惩罚项属于猜测；(b) FSM **自己也掉 21/50**，说明掉台在本保真度下部分内生于任务，"加惩罚就能降到 FSM 以下"没有证据支持；(c) 奖励现值与塑形项方向矛盾，先调奖励会与归属缺陷混淆 |
| **策略行为缺口：直接照搬 FSM 的 0.27 m 倒车/0.45 m 减速纪律** | 这会把 RL 试点退化成"模仿 FSM"，且 FSM 在同 seed 上的得分（8）也只略高于策略现值得分（5） | FSM 自己掉台 21/50、得分 8/50，把 FSM 纪律当作目标会把上限锁在 FSM 水平；且它同样受归属缺陷影响（第一轮丢 +3） |
| **评测口径：掉台门槛改为与"训练池均值"或"绝对阈值"比较** | 门槛写死在已冻结的 PRD 中，事后放宽即"移动球门"，PRD 与 AGENTS 均明令禁止 | 直接违反 PRD `Notes`："不允许用事后调参或放宽门槛制造结论" |
| **继续加训练步数** | 无证据支持；且不是"改一处" | 第一轮（51,200 步）与第二轮（307,200 步）的**掉台绝对数**没有随步数单调下降（PRD Confirmed Baseline）；开发集（5001–5020）上的得分优势未迁移到盲验集。**但跨轮 seed 不同，此条只能作为"无证据"而非"反证"** |
| **归属判定之外的第二处 `Sim.Core` 改动** | 无证据 | 41 次块出界事件中 `no_valid_contact` 为 0 次，说明另一条不计分路径在当前数据上未触发；无样本即无修复依据 |

### 4.4 必须写清的界限（诚实边界）

**只修归属，门槛仍不通过**：掉台 28 > 21 不变。因此：

- 若下一轮的目标是"通过门槛"，**一次只改一处不可能达成**——得分与掉台两个条件分别失败，
  单一改动最多翻转其中一个（归属修复翻转得分项）；
- 若下一轮的目标是"拿到可归因的实验结果"，则归属修复是**必须先做的那一处**，
  并应把该轮的预期显式写成"得分项转通过、掉台项预计仍失败"；
- 把归属修复与掉台侧改动**打包**在同一轮，会使"通过/失败"无法归因到单一原因，
  违反"只改一个明确问题"的纪律。

这一取舍需要用户决策（见 §8）。

---

## 5. 纪律与安全边界核对（R5）

| 约束 | 状态 |
| --- | --- |
| 不修改 `Sim.Core` 物理/裁判/计分语义 | ✅ 未改（`Physics.cs` / `MatchEngine.cs` 哈希与改动前相同） |
| 不修改 11 维观测定义与奖励常量 | ✅ 未改 |
| 不启动训练 | ✅ 未训练；仅加载两个**已冻结**的候选模型做前向推理 |
| 不改评测门槛 | ✅ 未改 |
| 不把已揭示 seed 集重新当盲验 | ✅ 6001–6050 / 4001–4010 仅作分析输入，未产生任何门槛判定 |
| 不触碰 `fidelity.json` | ✅ 未触碰 |
| 允许的代码改动仅限诊断可观测性 | ✅ 仅 `RlEnvCommand` 的可选 trace + 新增 `diagnose.py`、`gym_env` 的 `trace` 开关 |
| 默认关闭时 `rl-env` 输出与改动前一致 | ✅ 147 行逐字段一致（§1.1）；且开 trace 时 120/120 对与归档值一致（§1.3） |
| 大模型与逐 tick 全量轨迹不入 Git | ✅ 仅提交聚合结论、逐事件清单、4 个代表性样本与哈希 |
| 报告不含敏感设备标识 | ✅ 无 |

---

## 6. 回归结果（R6 / AC6）

在 `src/Sim.Cli/RlEnvCommand.cs`（可选 trace）与 Python 侧改动之后：

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 构建 | `dotnet build RobotSimulator.sln -m:1` | **0 错误**（增量构建，未重编译项目不重报警告） |
| 单元/集成测试 | `dotnet test RobotSimulator.sln -m:1 --no-restore` | **382/382 通过**（连续 2 次全绿） |
| 回放逐位一致 | `replay-check replays/*.json`（6 个文件） | **6/6 PASS**："reproduces the recorded match bit-for-bit" |
| 默认关闭 trace 一致性 | `diagnose.py default-off-check` | **147 行逐字段一致**，`problems: []` |
| 轨迹不扰动仿真 | `diagnose.py analyze --compare-recorded` | **120/120 对一致**（含 `total_reward`、`policy_ticks`） |

**诚实记录一次失败**：首次全量 `dotnet test` 有 1 个用例失败——
`TrainingResetPerformanceTests.HotResetP95_IsAtMostHalfOfColdCompileP95`
（热重置 p95 3.58 ms 需 ≤ 冷编译 p95 7.01 ms 的 50%，实测 51%）。
该用例是**墙钟性能比值**测试，在整套测试并发负载下呈偶发。隔离重跑该用例 **4/4 通过**，
随后两次全量 `dotnet test` 也 **382/382 通过**。判定为负载敏感的既有时序脆弱用例，
与本次改动无关（本次改动在 `trace` 关闭时只多一个被跳过的布尔分支）。
**未修改该测试或放宽其阈值。**

---

## 7. 下一轮 seed 划分建议（AC7，供后续任务预注册）

现状（`splits.py`，`SPLIT_VERSION = "score-block-split-v2"`）已揭示 / 已占用：

| 用途 | seed |
| --- | --- |
| 训练池 | `42` + `1000–1999`；SB3 RNG / 首个 reset seed `20260925` |
| `legacy_development`（已揭示） | 3001–3010 |
| `legacy_final_holdout`（已揭示，第一轮） | 4001–4010 |
| `development_v2`（已揭示） | 5001–5020 |
| `final_holdout_v2`（已揭示，第二轮） | 6001–6050 |

**建议新增（与上表全部互斥，也彼此互斥）**：

| 新 split | seed 范围 | 数量 | 用途 |
| --- | --- | --- | --- |
| `development_v3` | **7001–7020** | 20 | 新候选的模型选择（可反复使用） |
| `final_holdout_v3` | **8001–8050** | 50 | 新一轮**一次性**盲验（冻结后才可开启） |

- 与训练池（42、1000–1999、20260925）无交集；与 3001–3010、4001–4010、5001–5020、6001–6050 无交集。
- 必须同步：`SPLIT_VERSION` 升为 `score-block-split-v3`、在 `splits.py` 注册两个新 split、
  把 6001–6050 的语义从"最终留出"改为"已揭示、仅分析"（与 4001–4010 同等对待）、
  并保持 `REVEALED_SEEDS` 覆盖全部已揭示集合，使它们**无法**再被路线到任何留出集。
- 沿用 `--require-freeze` 冻结校验与"留出集只开一次"的纪律；6001–6050 上的任何后续实验
  **不得**再当作盲验证据。

---

## 8. 限制、未验证项与待决问题

**限制与未验证项**

1. 归属修复的投影（5→9 等）是**按现有轨迹重算计分口径**得到的，**不是**重跑改进后代码的结果；
   实际值需在修复任务中重放验证。
2. 修复后**重训**会不会同时改善掉台，本诊断**没有证据**（修复改变了训练奖励，但方向未知）。
3. 两轮盲验的 policy checkpoint 与 seed 集都不同，故**跨轮**差异不能归因于代码或训练步数；
   本报告的结论只基于**同轮同 seed 的策略 vs FSM 配对**。
4. 掉台分类是启发式规则，不是因果模型；§3.2 的原始测量量（速度、|w|、指令 v、min_edge）才是证据本体。
5. 未采集 5001–5020（开发集）的轨迹；本轮诊断未使用它，符合"仅可交叉核对、不得作门槛证据"的约束。
6. 未在 legacy 2D 后端上采集轨迹；本诊断只覆盖 MuJoCo，因为两轮盲验都跑在 `physics.backend=mujoco`。

**待决问题（需要用户确认，不擅自决定）**

诊断已收敛到"两个各自独立、均被量化的问题"：
**(A) 归属判定缺陷**（可证、零成本验证，但单独不足以通过门槛）与
**(B) 掉台缺口**（当前约束瓶颈，但属能力缺口，需改奖励/观测并重训，效果不确定）。

按"只改一个明确问题"的纪律，下一轮只能改其中之一。建议顺序与理由：

1. **先改 (A)**（推荐）：它是缺陷且污染奖励，是任何后续公平测量的前提；预测"得分项转通过、掉台项仍失败"。
2. **先改 (B)**：直接冲击约束瓶颈，但需在未修正的奖励信号上做设计，且效果不确定。

---

## 附件（`evidence/`）

| 文件 | 内容 |
| --- | --- |
| `diagnosis-final_holdout_v2.json` | 6001–6050 全量分析：聚合、逐事件、逐 episode、复现核对（含字段与不一致清单） |
| `diagnosis-legacy_round1.json` | 4001–4010 同上 |
| `collect-final_holdout_v2.json` / `collect-legacy_round1.json` | 采集清单：split、seed、模型/场景/CLI 的 SHA-256、逐 episode 摘要 |
| `misattribution-projection.json` | 41 次块出界事件明细 + 归属修复投影 |
| `decisive-excerpt.md` | 4 个决定性原始轨迹摘录（6037/6048/6001 对照/4005 FSM） |
| `default-off-check.json` | 默认关闭 trace 的一致性证明 |
