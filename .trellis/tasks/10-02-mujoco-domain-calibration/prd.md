# PRD：MuJoCo 域校准（转向权限 / 推击 / 登台回台）

- 任务：`.trellis/tasks/10-02-mujoco-domain-calibration`；分支 `test/score-block-ppo-checkpoint-round`
- 日期：2026-10-02；负责人 neco
- 前序：`10-01-rl-desktop-controller`（RL 接桌面）、`10-01-mbri-fsm-port`（mbri 移植 + 能力修复）

## 1. 背景（数字均为本轮实测）

桌面验收「我方 RL（外部策略）/ 对手 FSM（内置）」时暴露：**MuJoCo 场景下两侧控制器都不可用**。

| 实测项 | 数字 | 出处 |
|---|---|---|
| 内置 FSM 在 MuJoCo 的比赛形态 | SCORE_BLOCK 占 **2119/2400 tick（88%）**，全场位移 ~1.05 m；**双方推块得分 0** | 双方 FSM seed42 + 逐 tick diag |
| 两车对顶死锁 | 两车自相对两侧夹同一增益块 75 s 不动（我方 0.30 m / 对手 0.45 m，间距恒 0.74 m） | 同上 |
| 原地转向达成率 | 命令 ~0.9–1.4 rad/s，实际 **0.025–0.105 rad/s（2.8%/7.5%）** | 遥测探针（干净台面） |
| 限制器定位 | v1：补偿 ×4→×10 使 0.236→**0.989 rad/s（4.2×）**后 duty 饱和；v2：早已饱和，满 duty 仅 **0.274 rad/s** | 补偿扫描 |
| 质量敏感性 | 质量 ×10 ⇒ 偏航率 ÷10 ⇒ 限制是**与质量无关的驱动力矩 ÷ 惯量**，非地面摩擦 | 质量扫描 |
| 已排除项 | 滑动摩擦降档（0.6：更差且**上不了台**）、condim 3/4/6、滚动摩擦 0.002→1.0（无改善/变差） | 接触参数扫描 |
| mbri 在 MuJoCo | 整场卡走道（ADC_APPROACH 超时→SAFE_STOP→重臂 循环），**从未上台** | head-them-mbri MuJoCo 事件日志 |

结论：非 FSM 逻辑缺陷，而是 **MuJoCo 域本身（转向权限/推击/登台）与控制器能力不匹配**；`docs/ARCHITECTURE.md:56-57` 亦自述"模型参数为未标定工程初值；新模式不得宣称真机保真度"。

## 2. 用户已拍板的边界

1. **RL 侧**：模型改完后**重新训练**（现有 `.sim_runs` checkpoint 失配可接受）。
2. **模型版本**：**直接改现有 v1/v2**（不新建 v3）；旧 MuJoCo 回放与旧 checkpoint 失配，作为已知代价披露。

## 3. 目标与验收标准

**目标**：让「我方 RL / 对手内置 FSM」在 MuJoCo（v1 训练场景及其评测线）成为**可用的对局线**——两侧都能正常移动、转身、推块。

| # | 验收标准 | 判定口径 |
|---|---|---|
| R1 | **转向权限达标**：v2（真车场景）满 duty 原地转向 ≥ **2.0 rad/s**；v1（训练场景）同达标 | 遥测探针的稳态偏航率（同口径前后对比） |
| R2 | **推击可用**：无对手干扰下单侧把台上 0.3 kg 增益块推离原位 ≥ **0.3 m** | 探针 + MuJoCo 场景实测 |
| R3 | **内置 FSM 端到端得分**：MuJoCo 双方 FSM 一场（seed42）出现 **≥1 次「增益块被推下」** | `match --scenario scenarios/wushu-ring-2026-mujoco.json --events` |
| R4 | **mbri 登台/回台**：MuJoCo 下 mbri 能倒车登台，且回台率与 legacy 同量级（11 种子 head-them-mbri 对照） | 逐 tick 在台跳变口径（同 10-01 report-capability §2.2：回台率=(mounts−1)/falls） |

> **R4 判据修正（执行期披露，2026-10-02）**：原表述"在台时间 ≥ 60%"系规划期口径
> 误植——把 10-01 任务的**回台率** 60–68%（(mounts−1)/falls）误当作"在台时间比例"。
> 在台时间在有活跃对手的对局里两侧都会互相打下台，该指标不构成 mbri 能力判据；
> 修正为与 mbri 任务验收同口径的"登台可用 + 回台率对照 legacy"。
| R5 | **legacy 与默认路径逐位不变** | 全量 `dotnet test` exit=0；`replay-check replays/seed-42.json` PASS；未注入 `WheelContactOptions` 的 MJCF 逐字节不变 |
| R6 | **模型身份影响显式披露** | 报告/spec 写明模型哈希变化对 Mujoco 回放与 RL checkpoint 的影响；`fidelity.json` 不因此晋升 |

## 4. 约束

- 改动只落在 `Sim.Mujoco` 模型/驱动层（必要时含 `Sim.Protocol` 车辆档案既有字段值）；**不得**为"看起来能跑"改 Sim.Core 的裁判/计分/确定性契约。
- 数值必须**可解释、有锚点**：优先真车实测锚点（`MOTOR_TURN_CALIBRATION`：90°/0.65 s、135°/1.0 s、180°/1.2 s ⇒ ~2.4 rad/s）；无锚点的取值必须显式标注为工程初值并披露。
- 每项改动都要有**前/后同口径测量**，禁止凭观感。
- 不新增场景字段/协议字段；注入点保持 Sim.Mujoco 内部构造器（与 `MotorDriveOptions` 同例）。

## 5. 范围外

- RL 重训本身（模型定稿后另行启动）。
- mbri/builtin 默认控制器切换决策（`10-01-mbri-fsm-port` 决策包）。
- 特权视觉 → yolo-bridge 保真轮。
- 真机 k/b 再标定（需实体机器人）。
