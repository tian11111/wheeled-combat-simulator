# Design — 归属判定修复与 split v3 预注册

## 1. 行为改动（唯一一处）

`src/Sim.Core/Physics.cs` `PhysicsWorld.FinalizeBlockContacts`：

```csharp
// 现状（缺陷）：判据是"记录条数"
o.LastContactRole = last.Count == 1 ? last[0].Role : "simultaneous";

// 修复：判据是"不同角色数"
var roles = last.Select(c => c.Role).Distinct().ToList();
o.LastContactRole = roles.Count == 1 ? roles[0] : "simultaneous";
```

理由与安全性：

- MuJoCo 后端每 tick 10 个子步、**不做几何对去重**，单台机器人有多个几何体，
  因此同一子步可产生多条**同角色**记录；"记录条数"于是混同了"接触点个数"与"参与方数量"。
- 语义目标是"**谁**最后有效接触了这个块"。并列在 max 接触时刻的接触点若属于同一方，
  参与方仍只有一方，判定应为该方；只有出现两个不同角色才是真·同时。
- 判定仍是纯函数，无新增随机源，确定性契约不变。
- legacy 2D 路径每机器人每 tick 恰好写入 1 条记录（`MarkBlockContact` 每个
  `RobotBlockContact` 调用一次），`Distinct()` 在其上是空操作 → 既有回放逐位不变。
- `roles[0]` 的取值只可能来自 `{"us","them"}`，`Distinct()` 保留首次出现顺序，
  因此结果是确定且与记录顺序无关（单一不同角色时该角色唯一）。

## 2. 测试设计

### 2.1 单元测试（新增，直接测纯函数）

`PhysicsWorld.FinalizeBlockContacts(List<BlockRuntime>)` 是 public static，
`BlockRuntime` 的 `ContactThisStep` 是 public，可直接构造。新增测试覆盖：

| 用例 | `ContactThisStep` | 期望 `LastContactRole` |
| --- | --- | --- |
| 单机器人单条 | `[("us", 0.05)]` | `"us"` |
| **单机器人多条同角色（修复点）** | `[("us", 0.03), ("us", 0.05), ("us", 0.05)]` | `"us"` |
| 对手对称（修复点） | `[("them", 0.05), ("them", 0.05), ("them", 0.05)]` | `"them"` |
| 真·双方同时 | `[("us", 0.05), ("them", 0.05)]` | `"simultaneous"` |
| 只有较早时刻并列、max 时刻单条 | `[("us", 0.03), ("them", 0.03), ("us", 0.05)]` | `"us"` |
| 空接触集不覆盖既有判定 | `[]`（先置 `LastContactRole="us"`） | 仍为 `"us"`（`continue` 早退） |

同时验证 `us`/`them` 两条真实角色字符串（`RoleNames.Us` / `RoleNames.Them`）在
`MatchEngine.ObjFallCheckAll` 中仍能被识别，避免字符串漂移。

### 2.2 端到端测试（新增，MuJoCo）

在既有 `NativeMode_PushesBlockOffStage` 的几何上（Block[0] 于 (2.85,1.9)、我方于 (2.2,1.9)、
对手在远端 (1.9,1.1)），跑满 160 tick 后断言：块越过台沿**且**裁判给出我方
`EventKind.BlockScore`（修复前该场景会给出 `BlockOff {reason:"simultaneous"}`）。
这条测试正是诊断所发现缺陷的端到端回归。
若该断言在 MuJoCo 上不稳定（例如块在某个子步同时被两个几何体接触但角色仍为 `us`），
保留它并如实记录观测，不得为了让它通过而放宽归属判定。

## 3. split v3 预注册（`controllers/score_block_rl/splits.py`）

```python
SPLIT_VERSION = "score-block-split-v3"

DEVELOPMENT_V3 = "development_v3"      # 7001-7020
FINAL_HOLDOUT_V3 = "final_holdout_v3"  # 8001-8050

SPLIT_SEEDS = {
    LEGACY_DEVELOPMENT: range(3001, 3011),
    LEGACY_FINAL_HOLDOUT: range(4001, 4011),
    DEVELOPMENT_V2: range(5001, 5021),
    FINAL_HOLDOUT_V2: range(6001, 6051),
    DEVELOPMENT_V3: range(7001, 7021),
    FINAL_HOLDOUT_V3: range(8001, 8051),
}
BLIND_SPLITS = (FINAL_HOLDOUT_V3,)
REVEALED_HOLDOUT_SPLITS = (LEGACY_FINAL_HOLDOUT, FINAL_HOLDOUT_V2)
HISTORICAL_SPLIT_SEEDS = legacy_development | legacy_final_holdout | development_v2 | final_holdout_v2
REVEALED_SEEDS = TRAIN_EPISODE_SEEDS | HISTORICAL_SPLIT_SEEDS
```

- `resolve_selection()` 默认 split → `DEVELOPMENT_V3`。
- `SPLIT_USAGE` 对 v2/legacy 全部写明 "already revealed; analysis only"。
- `is_blind_holdout` 现在**只**对 `final_holdout_v3` 为真；`--final-holdout` 仍指向
  `legacy_final_holdout`（历史语义），但它已不再被标为 blind。
- 保持 `MIN_CUSTOM_SEEDS = 10`；`development_v3` 20 个 seed、`final_holdout_v3` 50 个，满足下限。

互斥性核对（新号段与既有全部不交）：

| 集合 | 号段 |
| --- | --- |
| 训练池 | 42、1000–1999、20260925 |
| 已揭示 | 3001–3010、4001–4010、5001–5020、6001–6050 |
| **新增** | **7001–7020、8001–8050** |

## 4. 评测入口作用域迁移（`evaluate.py`）

| 位置 | 改动 |
| --- | --- |
| `--select-candidate` / `--freeze` | 作用域 `DEVELOPMENT_V2` → `DEVELOPMENT_V3` |
| `--require-freeze` | 仅对 `FINAL_HOLDOUT_V3` 有效 |
| `verify_freeze` | `final_holdout_split` 期望值 → `FINAL_HOLDOUT_V3` |
| freeze record | `development_split`/`final_holdout_split`/`final_holdout_seeds`/`note` 全部 v3 |
| `blind_run_index` / `new_round_blind_gate_passed` | 仅在 `FINAL_HOLDOUT_V3` 时写入 |
| **新增守卫** | split ∈ `REVEALED_HOLDOUT_SPLITS` 时必须显式 `--analysis-only`；输出写 `analysis_only: true`、`gate_evidence_eligible: false` |
| help/docstring | split 列表与默认值更新为 v3 |

`gate_evidence_eligible` 同时出现在顶层与每个 model 行，便于下游机器判定；
`ac4_claim_eligible` 恒为 `false`（保持既有契约）。

## 5. 其他调用点

- `train.py`：`FINAL_HOLDOUT_V2` → `FINAL_HOLDOUT_V3`（仅用于 run-config 的 split 说明标签）。
- `selftest.py`：
  - `EXPECTED_SPLIT_SEEDS` 增加 v3；
  - `SPLIT_VERSION == "score-block-split-v3"`；
  - 默认 split 断言 → `development_v3`（7001–7020）；
  - blind split 断言 → `final_holdout_v3`（50 seed）；
  - 新增：v2/legacy 留出集 `is_blind_holdout` 为假、且在 `REVEALED_SEEDS` 中；
  - 探索性自定义 seed 示例 `range(7001,7011)` → 改为未被占用的号段（`9001–9011`）；
  - "unknown split" 用例 `development_v3` → `development_v4`；
  - freeze/sweep 断言 → v3 字段。
- `README.md`：split 表加 v3 并把 v2 标为已揭示；补 `--analysis-only` 用法说明；
  补"归属修复"一节指向本任务报告。
- `diagnose.py` 的 `EXCERPT_SAMPLES` 与默认 `--split` 保持指向已揭示集（分析用途），无需改；
  但其默认值 `final_holdout_v2` 仍可通过 `resolve_selection` 解析（split 仍在注册表中）。

## 6. 验证计划

1. `dotnet build`（0 错误）；`dotnet test -m:1 --no-restore`（全绿，含新增测试）。
2. `replay-check replays/*.json` × 6 → 逐位 PASS（legacy 路径应不变）。
3. `selftest.py`（带 `--train-dir` 时可跑产物检查；不带时产物项 skipped）。
4. **修复真实增量验证**：用已揭示的 6001–6050 / 4001–4010 重放两个冻结模型
   （`--analysis-only`），比对 `locked_target_us_block_scores`、`unowned_block_offs`，
   与诊断投影 5 → 9 / 10 → 0 对照；同时确认 `us_drops`、`policy_ticks`、`total_reward` **不变**
   （归属只在出界 tick 决定计分，不反馈进物理）。
5. `diagnose.py default-off-check` 复跑，确认 rl-env 默认路径仍未变。

## 7. 风险与回退

| 风险 | 缓解 |
| --- | --- |
| MuJoCo 端到端断言不稳定 | 单元测试为硬性门槛；端到端失败则如实记录，不放宽判定 |
| split v3 变更漏改调用点 | `selftest.py` 含 `SPLIT_VERSION`、默认 split、freeze 字段三处断言 |
| 已揭示集被误当盲验 | `BLIND_SPLITS` 只含 v3 + `--analysis-only` 守卫 + `gate_evidence_eligible=false` |
| 改动影响 legacy 确定性 | `replay-check` 6 份逐位 PASS 作为硬门槛 |

回退：`git checkout -- src/Sim.Core/Physics.cs src/Sim.Tests/<新测试> controllers/score_block_rl/*.py`。
