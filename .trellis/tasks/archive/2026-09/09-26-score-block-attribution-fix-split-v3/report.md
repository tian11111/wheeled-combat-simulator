# 报告 — SCORE_BLOCK 归属判定修复与 split v3 预注册

任务：`.trellis/tasks/09-26-score-block-attribution-fix-split-v3`
上游诊断：`.trellis/tasks/archive/2026-09/09-26-score-block-failure-diagnosis/report.md`

## 0. 结论摘要

1. **只改了一处**：`src/Sim.Core/Physics.cs` 的 `PhysicsWorld.FinalizeBlockContacts`，
   判据由"max 接触时刻的记录条数"改为"max 接触时刻的**不同角色数**"。
2. **回归测试先失败后通过**：新增 6 个测试，把修复 stash 掉后有 2 个失败（单元 + MuJoCo 端到端），
   修复后 6/6 通过；全量 `dotnet test` **388/388**，6 份 legacy `replay-check` **逐位 PASS**。
3. **已揭示集重放（仅分析）**：`unowned_block_offs` 在四格中全部归零（10→0、1→0、2→0、1→0），
   `us_drops` 四格**逐个不变**（28/21/8/7）。锁定目标得分实测
   **5→8**（策略 6001–6050）、**8→8**（FSM）、**1→1**（策略 4001–4010）、**0→1**（FSM）。
   投影的 5→9 未达成，差 1，原因已定位到 seed 6047（见 §4）。
4. **修复不是纯观测改动**：归属变化经 `Gain`/`OppGain`/`HandleBuffScored` 反馈进对手 FSM，
   因此跨版本轨迹只在**首次归属变化之前**可比。逐 tick A/B：4/50 完全逐位一致，
   36/50 只有描述性字段 `last_contact_role` 变化，10/50 发生行为分叉。
5. **奖励污染得到实测确认**：4 个 seed（6022/6037/6040/6048）`total_reward` 恰好上升 **+1.5**
   （−0.5 误罚 → +1.0 正确奖励），seed 6043 上升 **+0.5**（误罚消失）。
6. **split v3 已预注册**：`development_v3` = 7001–7020（默认）、`final_holdout_v3` = 8001–8050
   （唯一盲验）；4001–4010、5001–5020、6001–6050 全部降级为"已揭示、仅分析"，
   未加 `--analysis-only` 直接拒绝，结果写 `gate_evidence_eligible: false`。
7. **未做**：没有训练、没有跑新一轮盲验、没有宣布任何门槛通过。
   门槛的掉台条件仍然失败（28 > 21、8 > 7），单改这一处不足以过门槛——这与动手前对用户的说明一致。

## 1. AC 达成情况

| AC | 状态 | 证据 |
|---|---|---|
| AC1 只改 `FinalizeBlockContacts`，无其他 Sim.Core 语义改动 | ✅ | `git diff src/Sim.Core/` 仅 1 个函数；`evidence/fix-delta.json` 记录修复前后代码 |
| AC2 覆盖三类情形的回归测试；build 0 错误、test 全绿 | ✅ | `src/Sim.Tests/BlockAttributionTests.cs`；`evidence/regression.txt`（6 警告/0 错误，388/388，新增 6/6） |
| AC3 6 份 legacy `replays/*.json` 逐位 PASS | ✅ | `evidence/regression.txt` 6×"reproduces the recorded match bit-for-bit" |
| AC4 已揭示集重放与投影逐项对照，差异已解释 | ✅ | `evidence/fix-delta.json`、`fix-delta.txt`、`trace-ab.txt`、`match-ab.txt`；§4 解释 5→9 vs 5→8 |
| AC5 split v3 注册、互斥、默认与唯一盲验 | ✅ | `controllers/score_block_rl/splits.py`；`evidence/selftest.txt`（"6 named splits"、"default split is development_v3"、"v2/legacy holdouts are revealed and can never be blind again"） |
| AC6 已揭示留出集要求 `--analysis-only` 且标记不可作门槛证据 | ✅ | `evidence/selftest.txt` "CLI guard: revealed holdouts are analysis-only"；`evidence/fix-delta.json` 的 `analysis_only_flags` |
| AC7 `selftest.py` 全绿、README 更新 | ✅ | `evidence/selftest.txt` 31 passed / 0 failed / 8 skipped（8 项因未给 `--train-dir` 而 skipped，非通过）；`README.md` 数据划分与评测章节 |
| AC8 报告含对照、回归、限制与诚实边界 | ✅ | 本文件 |

**负向对照（关键）**：把 `src/Sim.Core/Physics.cs` stash 掉后重建，6 个新测试中 2 个失败，
失败信息就是缺陷本身：

```
SeveralRecordsFromOneRobot_AreAttributedToThatRobot  [FAIL]  'simultaneous' != 'us'
MujocoSinglePusher_PushingTheBlockOffStage_ScoresForUs [FAIL]
  a lone pusher was not credited; block offs seen: [[score] 双方同时接触增益块并将其推出台外, 按规则不计分 | ...]
```

## 2. 改动内容

```csharp
// 修复前（缺陷）：判据是"记录条数"
o.LastContactRole = last.Count == 1 ? last[0].Role : "simultaneous";

// 修复后：判据是"不同角色数"
var roles = last.Select(c => c.Role).Distinct().ToList();
o.LastContactRole = roles.Count == 1 ? roles[0] : "simultaneous";
```

- MuJoCo 后端每 tick 10 个子步且**不去重几何体对**，单台机器人可留下多条同角色记录；
  "记录条数"把"接触点个数"读成了"参与方数量"。
- legacy 2D 路径每机器人每 tick 恰好写入 1 条记录（`MarkBlockContact` 每个
  `RobotBlockContact` 调用一次），`Distinct()` 在其上是空操作 → 6 份 legacy 回放逐位不变。
- 仍是纯函数，无新增随机源，同 seed + 同动作序列仍逐位可复现。

新增测试 `src/Sim.Tests/BlockAttributionTests.cs`（6 个）：

| 测试 | 断言 |
|---|---|
| `SingleContactRecord_KeepsItsRole` | 单条记录 → 该角色（防回归） |
| `SeveralRecordsFromOneRobot_AreAttributedToThatRobot` | 同角色 2/3/4 条 → 该角色（**修复点**） |
| `BothRobotsAtTheSameInstant_RemainSimultaneous` | 真双角色并列 → `"simultaneous"`（保持不计分） |
| `OnlyTheLargestContactTimeDecides_AnEarlierTieIsIgnored` | 较早时刻并列不影响 |
| `EmptyContactSet_LeavesThePreviousRoleUntouched` | 空接触集早退，不覆盖既有判定 |
| `MujocoSinglePusher_PushingTheBlockOffStage_ScoresForUs` | MuJoCo 端到端：单个推手把增益块推出台沿必须判给我方 `BlockScore`，且不得出现"同时接触"不计分 |

## 3. 已揭示集实测增量（仅分析，非门槛证据）

两次重放都用 `--analysis-only`，冻结模型与场景哈希见 `evidence/fix-delta.json`。

### 3.1 `final_holdout_v2`（6001–6050，50 seed）

| 指标 | 策略 修复前 | 策略 修复后 | FSM 修复前 | FSM 修复后 |
|---|---|---|---|---|
| `total_locked_target_us_block_scores` | 5 | **8** | 8 | **8** |
| `episodes_with_locked_target_us_block_score` | 5 | **8** | 8 | 8 |
| `total_us_block_score_events` | 5 | **8** | 9 | 9 |
| `total_them_block_score_events` | 6 | **12** | 6 | **7** |
| `total_target_block_offs` | 5 | **0** | 0 | 0 |
| `total_unowned_block_offs` | 10 | **0** | 1 | **0** |
| `total_us_drops` | 28 | **28** | 21 | **21** |
| `no_score_block_episodes` | 5 | 5 | 5 | 5 |
| `final_score_us_sum` / `_them_sum` | 123 / 77 | **129 / 100** | 66 / 67 | 66 / **76** |

### 3.2 `legacy_final_holdout`（4001–4010，10 seed）

| 指标 | 策略 修复前 | 策略 修复后 | FSM 修复前 | FSM 修复后 |
|---|---|---|---|---|
| `total_locked_target_us_block_scores` | 1 | **1** | 0 | **1** |
| `total_unowned_block_offs` | 2 | **0** | 1 | **0** |
| `total_target_block_offs` | 0 | 0 | 1 | **0** |
| `total_us_drops` | 8 | **8** | 7 | **7** |
| `total_them_block_score_events` | 2 | **4** | 0 | 0 |
| `final_score_us_sum` / `_them_sum` | 18 / 14 | 18 / **20** | 14 / 9 | **17** / 9 |

### 3.3 与诊断投影的对照

| 单元 | 投影 | 实测 | 一致 |
|---|---|---|---|
| 6001–6050 策略 锁定目标 | 5 → 9 | 5 → **8** | ✗（差 1，原因见 §4） |
| 6001–6050 FSM 锁定目标 | 8 → 8 | 8 → **8** | ✅ |
| 4001–4010 策略 锁定目标 | 1 → 1 | 1 → **1** | ✅ |
| 4001–4010 FSM 锁定目标 | 0 → 1 | 0 → **1** | ✅ |
| 四格 `unowned_block_offs` | → 0 | 10→0 / 1→0 / 2→0 / 1→0 | ✅ |
| 四格 `us_drops` | 不变 | 28 / 21 / 8 / 7 逐个不变 | ✅ |

**唯一未达成项**：策略 6001–6050 锁定目标 5→8 而非 5→9。逐 seed 净变化是
`+4 −1`：6022、6037、6040、6048 各 +1（与投影一致），但 **6047 丢掉了原本的 1 分**。

## 4. 为什么是 8 而不是 9：修复会经裁判计分反馈进 FSM

seed 6047 的逐 tick A/B（`evidence/dump_seed_diff.py 6047`，原始轨迹摘录）：

```
pre  row177 tick=329 them=(-0.4, 0) us=(1, 0.6904) roles=[simultaneous, simultaneous, ''] events=[]
post row177 tick=329 them=(-0.4, 0) us=(1, 0.6904) roles=[them, them, '']           events=[]

pre  row178 tick=330 them=(-0.4, 0) ... roles=[simultaneous, ...]
     events=[('BlockOff','simultaneous','增益块')]
post row178 tick=330 them=( 0.0000, 0) ... roles=[them, ...]
     events=[('BlockScore','','增益块'), ('Fsm','','')]      <-- 多出一个 Fsm 事件

pre  row179 tick=331 them=(0, 2)      <-- 对手继续原动作
post row179 tick=331 them=(-0.4, 0)   <-- 对手 FSM 改动作
```

机制：出界事件由 `BlockOff` 变成 `BlockScore` 后，`MatchEngine.ObjFallCheckAll` 追加执行
`Gain(pusher, 3, "block_buff")` 与 `_fsm.HandleBuffScored(pusher)`；后者发出 `Fsm` 事件并改变
对手 FSM 的行为，从 tick 331 起对手动作从 `(0, 2)` 变成 `(−0.4, 0)`。本 seed 中这一分叉使
块最终没有离开台面（`target_out` True→False、`policy_ticks` 1189→2248、
`done_reason` 变为"比赛时间结束(手动模式)"），于是丢掉原本的 1 分。

**因此**：归属修复不是纯观测/纯计分改动，它同时改变 FSM 输入与后续轨迹。
修复前后只能比较到"首次归属变化"之前；把投影的 9 当作结果是不诚实的，
本任务以实测的 **8** 为准。

### 4.1 逐 tick 轨迹 A/B 全景（`evidence/trace-ab.txt`）

用诊断任务留下的**修复前**轨迹（`%TEMP%\score-block-rl-diagnose-20260926`）与本次
重放的**修复后**轨迹逐 seed 对比 50 个 seed：

| 类别 | seed 数 | 说明 |
|---|---|---|
| 完全逐位一致（连 `last_contact_role` 都没变） | 4 | 6006、6018、6036、6038（均为 `no_score_block`，策略窗口从未打开） |
| 仅描述性字段 `last_contact_role` 变化，机器人/块状态与事件逐位一致 | 36 | 证明修复在"出界未归属"之外**完全惰性** |
| 发生行为分叉 | 10 | 见下 |

10 个分叉 seed 中：

- **7 个**（6011、6022、6029、6031、6037、6040、6048）首次分叉**就是**出界事件本身：
  `BlockOff{reason=simultaneous}` → `BlockScore`，同一 tick、同一块。
  例：seed 6037 tick 317，seed 6048 tick 260，seed 6022 tick 338。
- **3 个**（6043、6047、6049）首次分叉是对手被下达的动作 `(v, w)` 变化，
  紧跟在其后/其上的就是归属事件与 `HandleBuffScored` 的 `Fsm` 事件（§4 已逐 tick 验证 6047）。

对 4 个"完全逐位一致"的 seed，`rl-env` 的计数器覆盖不到（它们没有策略窗口），
因此另做**完整比赛 A/B**（`evidence/match-ab.txt`，修复前 DLL 单独构建到 `%TEMP%`）：

| seed | 差异行数 | 差异内容 |
|---|---|---|
| 6018 | 0 | 逐字节一致 |
| 6038 | 0 | 逐字节一致 |
| 6006 | 4 | `[38] BlockOff [我方] 双方同时接触增益块…不计分` → `[38] BlockScore [对手] 增益块被推下擂台! 对手 +3`；比分 0:3 → 0:6 |
| 6036 | 4 | 同上（事件号同为 `[44]`）；比分 0:3 → 0:6 |

两例的事件序号、事件总数（50）与相邻事件完全相同，只有该事件本身被重新归属——
即这两场比赛**没有轨迹分叉**，只是同一个出界事件从"不计分"变成"对手 +3"。
这解释了 §3.1 中 `final_score_them_sum` 在 6006/6036 上出现、而
`them_block_score_events` 却未变的表面矛盾：`us_block_score_events` /
`them_block_score_events` / `unowned_block_offs` 三个计数器只在 `step()` 内累加
（`src/Sim.Cli/RlEnvCommand.cs` 275–277 行），比赛里 FSM 驱动的部分（含全部
`no_score_block` episode）对它们不可见。

### 4.2 奖励污染的实测确认

上游诊断指出：归属缺陷让成功推出界被罚 `NotOursPenalty = −0.5`，而正确行为应是
`TargetReward = +1.0`（单事件方向性错误 1.5）。本次重放逐 seed 实测：

| seed | `total_reward` 修复前 → 修复后 | 差值 | 机制 |
|---|---|---|---|
| 6022 | −0.452472 → 1.047528 | **+1.500000** | 锁定目标由 `BlockOff` 变 `BlockScore`：−0.5 罚 → +1.0 奖 |
| 6037 | −0.439777 → 1.060223 | **+1.500000** | 同上 |
| 6040 | −0.447516 → 1.052484 | **+1.500000** | 同上 |
| 6048 | −1.444780 → 0.055220 | **+1.500000** | 同上（该 seed 另有我方掉台，奖励仍为 +1.5） |
| 6043 | −0.457654 → 0.042346 | **+0.500000** | 目标被归给对手 → `TargetLost` 但不再是 `TargetBlockOff`，−0.5 罚消失 |

这与 `EdgeShapingScale` 鼓励把块推向台沿的方向一致：修复前同一个"成功出界"事件
给出与塑形项相反的符号。**这意味着已有模型是在被污染的奖励下训练的**，
下一轮必须先修好归属再重训，否则策略仍带着"推出去会被罚"的错误激励。

## 5. split v3 预注册与已揭示集隔离

`controllers/score_block_rl/splits.py`（唯一来源）：

| split | seeds | 用途 |
|---|---|---|
| `legacy_development` | 3001–3010 | 已揭示，仅对照 |
| `legacy_final_holdout` | 4001–4010 | 已揭示；`--final-holdout` 只指它 |
| `development_v2` | 5001–5020 | 已揭示，仅分析 |
| `final_holdout_v2` | 6001–6050 | 已揭示，仅分析 |
| **`development_v3`** | **7001–7020** | **本轮选模开发集（默认 split）** |
| **`final_holdout_v3`** | **8001–8050** | **本轮唯一盲验集（需冻结记录）** |
| `exploratory` | 自定义 `--seeds` | 探索，永不作为门槛证据 |

- `SPLIT_VERSION = "score-block-split-v3"`；`BLIND_SPLITS == (FINAL_HOLDOUT_V3,)`。
- `REVEALED_SEEDS` 覆盖 42 + 1000–1999 + 20260925 + 3001–3010 + 4001–4010 + 5001–5020 + 6001–6050；
  任何 `--seeds` 复用上述号段在路由阶段即被拒绝（selftest 6 条互斥用例）。
- `REVEALED_HOLDOUT_SPLITS = (legacy_final_holdout, final_holdout_v2)`：
  必须显式 `--analysis-only`，否则 `parser.error`；结果写
  `analysis_only: true`、`gate_evidence_eligible: false`、`is_revealed_holdout: true`；
  `--analysis-only` 用在非已揭示 split 上同样被拒（避免它变成免检开关）。
- `--select-candidate` / `--freeze` 作用域迁到 `development_v3`；
  `--require-freeze`、`blind_run_index`、`new_round_blind_gate_passed` 只对 `final_holdout_v3` 生效；
  freeze 记录的 `split_version`/`final_holdout_split`/`final_holdout_seeds` 全部指向 v3
  （v2 时代的冻结记录会因 `split_version_matches` 与 `final_holdout_split_matches` 双双不匹配而被拒绝）。
- `train.py` 的 `run-config.json` 明示 6 个 split 及其用途标签；`REVEALED_HOLDOUT_SPLITS` 标为
  "revealed holdout; analysis only"。
- 规范同步：`.trellis/spec/sim/index.md` 的预注册契约已从 v2 更新为 v3，并写明"归属判定按不同角色数"
  以及"修复会反馈进 FSM，故不是纯观测改动"。

## 6. 回归结果（`evidence/regression.txt`）

| 检查 | 结果 |
|---|---|
| `dotnet build RobotSimulator.sln -m:1 --no-incremental` | 0 错误 / 6 警告 |
| `dotnet test RobotSimulator.sln -m:1 --no-restore` | **388 通过 / 0 失败 / 0 跳过**（382 + 新增 6） |
| `replay-check` × 6（`replays/*.json`） | **6/6 逐位 PASS** |
| `selftest.py` | **31 passed / 0 failed / 8 skipped**（skipped 为未提供 `--train-dir` 的产物检查，未假装通过） |
| 新增测试（修复 stash 后） | 6 中 2 失败（负向对照，见 §1） |

6 条警告全部来自本次未触碰的文件（`BatchCommandTests.cs` 5 条 CS8601、
`SensorCalibrationImportTests.cs` 1 条 xUnit1013），**无新增警告**。

`Sim.Core` 的行为改动只有 1 行（`git diff src/Sim.Core/Physics.cs`），
其余为注释；`git diff --stat` 为 7 个文件 226 插入 / 87 删除，其中
Python 与文档占绝大部分。

## 7. 限制与诚实边界

1. **已揭示集结果不是门槛证据**。6001–6050、4001–4010 只能用于分析与对照；
   两次重放都带 `--analysis-only` 且结果标 `gate_evidence_eligible=false`。
   本报告不宣布任何门槛通过。
2. **单改这一处不足以过门槛**，这与用户在动手前的判断一致：门槛的掉台条件仍然失败——
   6001–6050 策略 28 vs FSM 21；4001–4010 策略 8 vs FSM 7。
   （描述性观察，非门槛判定：修复后"锁定目标得分不低于 FSM"这一条在两组上都已不再是短板，
   6001–6050 为 8 vs 8、4001–4010 为 1 vs 1；掉台是唯一剩余阻塞。）
3. **不要引用投影的 9**。投影假设轨迹冻结，而修复会经 `HandleBuffScored` / `Gain` 反馈进对手 FSM，
   实测端到端结果是 8。5→8 与 5→9 的差由 seed 6047 解释（§4）。
4. **修复前后轨迹只在首次归属变化前可比**。跨版本比较必须限定在该点之前；本任务用
   50 个 seed 的逐 tick A/B 给出了逐 seed 的分叉点（`evidence/trace-ab.txt`）。
5. **`rl-env` 计数器覆盖不到 FSM 驱动的比赛片段**。`us_block_score_events` /
   `them_block_score_events` / `unowned_block_offs` 只在 `step()` 内累加，
   因此 `no_score_block` episode 与策略窗口之外的块事件对它们不可见；
   §4.1 的完整比赛 A/B 是必要的补充，不能只靠这三个计数器解释比分变化。
6. **`final_score_us_sum` / `final_score_them_sum` 不是门槛证据**，且比事件计数移动更多：
   增益块 +3、减益块由推方送对手 +6、掉台/读秒/消极各 +1，都会进总分。
7. **端到端 MuJoCo 测试只覆盖一种几何**（单个推手、对手在远端）。
   多点同角色归属的一般性由纯函数测试覆盖（6 类用例）。
8. **没有重训**。现有模型是在被污染的奖励下训练出来的（§4.2），
   修复只把环境改对；策略本身仍是旧激励的产物。下一轮必须重新训练后才能谈门槛。
9. **只有 legacy 2D 回放保证逐位不变**；MuJoCo 侧此前录制的 `rl-env` episode
   归属结论会改变，这是修正而非回归，但不应用于逐位对比。
10. **本任务未改**奖励常量、观测维度、评测门槛、默认 FSM、`fidelity.json`。

## 8. 下一步建议

1. **独立任务处理掉台缺口**：诊断已给出判别特征（速度 > 0.4 m/s 的掉台占 82% vs FSM 14%；
   `|w| > 0.2` 93% vs 29%；已刹车的 `cmd_v < −0.5` 79% vs 0%），
   目标应是"接近台沿时的速度/转角纪律"，与本次归属修复互不重叠。
2. **新任务：在修复后的环境上用 `development_v3`（7001–7020）重新训练并选模**，
   冻结候选后再开 `final_holdout_v3`（8001–8050）唯一一次盲验。
3. 训练前先确认奖励方向正确：修复后"把锁定目标推出界"必须给 +1.0，
   不再有 −0.5 的 `NotOurPenalty`；建议在训练日志里抽查该事件族的回报符号。

## 9. 证据清单

| 文件 | 内容 |
|---|---|
| `evidence/fix-delta.json` | 机器可读的修复前后四格对照、判定标志、源码/产物哈希 |
| `evidence/fix-delta.txt` | `compare_fix.py` 输出：四格汇总 + 逐 seed 变化明细 |
| `evidence/trace-ab.txt` | 50 个 seed 的逐 tick A/B（修复前 vs 修复后轨迹） |
| `evidence/match-ab.txt` | 4 个 `no_score_block` seed 的完整比赛 A/B（含 6006/6036 的逐事件证据） |
| `evidence/regression.txt` | 全量构建/测试/回放/selftest 原始输出与负向对照 |
| `evidence/selftest.txt` | `selftest.py` 全量检查输出（31 passed / 8 skipped） |
| `evidence/compare_fix.py` | 生成 `fix-delta.txt` 的只读对比脚本 |
| `evidence/diff_traces.py` | 生成 `trace-ab.txt` 的逐 tick 轨迹对比脚本 |
| `evidence/dump_seed_diff.py` | 单个 seed 的分叉点上下文转储（§4 的 6047 证据） |
| `evidence/make_evidence.py` | 生成 `fix-delta.json` 的脚本 |

未入库的中间产物：`%TEMP%\score-block-attribution-fix\`（两次重放 JSON、修复后轨迹）、
`%TEMP%\score-block-prefix-cli\`（为完整比赛 A/B 单独构建的修复前 CLI）、
`%TEMP%\score-block-rl-diagnose-20260926\`（诊断任务留下的修复前轨迹，本任务复用）。
