# MuJoCo 域标定契约（domain-calib-v1）

> 来源：任务 10-02-mujoco-domain-calibration。改轮-地接触参数、原地转向补偿、
> MuJoCo 模型几何/执行器、或任何"MuJoCo 域行为钉值"测试之前，先读本文件。

## 1. Scope / Trigger

- 触发：`Sim.Mujoco` 中 `WheelContactOptions` / `DefaultInPlaceTurnCompensation` /
  `MujocoModel` 几何与执行器常量的任何改动；任何依赖 MuJoCo 轨迹的测试钉值重录。
- 与 legacy 的边界：全部定值只落在 `Sim.Mujoco`（模型/驱动层），legacy 物理零改动。
  每轮收尾必须 `replay-check replays/seed-42.json` 逐位 PASS。

## 2. 定值与锚点等级（改值前先确认等级）

| 量 | 值 | 锚点等级 | 位置 |
|---|---|---|---|
| 轮-地滑动摩擦 | v1 **5.0** / v2 **6.0**（condim 3 / spin 0.02 / roll 0.002） | **工程初值**（扫描锚定：v1 峰 f5=2.54、v2 峰 f6=1.17 rad/s） | `MotorDriveOptions.WheelContactOptions.Resolved(isV2)` |
| 原地转向补偿 | **10.0**（原 4.0） | 工程初值（duty 饱和所需） | `MujocoPhysicsBackend.DefaultInPlaceTurnCompensation` |
| 电机真值 | τ_stall 1.72 / ω_noload 4π / kv 0.136873 | **真车锚点**（2342 datasheet，不得改） | `MujocoModel.Mjc*` |
| v2 几何/质量 | 轮径 0.0325 / 轮距 0.229 / 整车 3.5 kg 等 | **真车锚点**（mesh 实测，不得改） | `MujocoModel.*V2` |
| v2 转向上限 | 稳态偏航带 **[1.0, 1.35] rad/s** | 实测天花板（钉在用例里） | `MujocoDomainCalibrationTests` |

- **物理机理**：四轮固定朝向滑移转向是过约束系统（μ→∞ 横向锁死转不动、μ→0 空转），
  稳态偏航 = 差速驱动力矩 vs 四轮横向刷矩的平衡，质量缩放 ∝1/mass。真车 2.4 rad/s
  （90°/0.65 s）依赖胎纹各向异性，MuJoCo 各向同性摩擦表达不了 ⇒ **v2 到不了 2.0 是
  建模缺口不是调参问题**；禁止用无锚点参数（扭矩/摩擦越界值）把 v2 "调"过 2.0 硬凑。
  出路在接触模型层（各向异性/轮胎模型），属保真度轮。
- 注入语义：`WheelContactOptions.Condon==0`（default）＝"未设置 ⇒ 取每模型标定默认"；
  显式注入（Condim≠0）全三元组生效（探针/试验用）。C# `default` 对 struct 是全零，
  禁止用属性初始值表达默认。

## 3. 模型身份连带（每次 MJCF 字节变化都会触发）

- `ModelSha256 = SHA256(xml ‖ assets)`；v1 变 → `MujocoVehicleMeshTests.V1ModelSha256`
  重钉（常量注释保留三段变更史）；v2 变 → 无钉值但 replay header 身份变。
- 既有 MuJoCo 回放被 `CreateForReplay` 身份门禁拒绝、RL checkpoint 观测分布失效
  ——属预期代价，政策（用户 2026-10-02 拍板）：**直接改现有 v1/v2，不新增版本**；
  改模型 ⇒ RL 重训。
- `fidelity.json` **不因标定晋升**：工程初值标定不是真机保真度证据。

## 4. MuJoCo 域钉值重录纪律

模型/参数变化 ⇒ 轨迹变 ⇒ 下列钉值按新默认重录，但**重录前逐一复核"是轨迹平移
还是真回归"**（诊断程序放 `tmp/`，不入库）：

| 测试 | 钉什么 |
|---|---|
| `MujocoVehicleMeshTests.V1Scenario_KeepsTheRecordedModelHash` | v1 哈希 |
| `ScoreBlockExhibitionTests.Preroll_Seed42…` / `MatchRunnerExhibition…` / `DesktopLiveDriver…` | score_block 入场 tick（2026-10-02 起 369） |
| `ScoreBlockExhibitionTests.HandoffObservation…` | 11 维交接观测样本（±1e-4） |
| `IncapacitatedTests.MujocoMatch_FlippedRobotStops…` | 会翻覆的种子（当前 seed 5；摩擦越高越稳，需重扫 1..170） |
| `SearchClassificationFlowTests.*` | 场景口径：**目标必须在台上**（FindTargetFor 契约=只追台上目标）；台下目标旧通过依赖出生踢跳伪影 |
| `MujocoDomainCalibrationTests` | v1 偏航 ≥2.0 / v2 上限带 / 推块 ≥0.3 m |

- 临时探针的归宿：**转正**成有断言的测量用例（ITestOutputHelper 输出实测值）或删除，
  不留扫描型无断言测试。

## 5. 已知残余（勿当 bug 重查）

- 出生踢跳：MuJoCo 出生接触解算给零指令车体小冲量（当前 ~0.45 m 蠕爬；旧摩擦 1.5
  时代曾 4 rad/s 自旋 + 0.78 m 漂移并使一处测试伪通过）。
- v2 seed42 双 FSM 无推块得分、掉台 7+3 偏多（观察项，v2 保真度轮输入）。
- mbri REMOUNT 台角/围栏卡位残例（legacy 2/11、mujoco 4/11 种子回台率 0%，
  同类残余，归 10-01-mbri-fsm-port 清单）。

## 6. 环境陷阱（Windows 专有）

- CLI 控制台输出重定向后是 GBK：grep 前先 `iconv -f GBK -t UTF-8 -c`。
- Git Bash 管道会把 git 输出 **CRLF 化**：`git show X | grep -c $'\r'` 判 blob 行尾是
  假象；验 blob 用 `git cat-file blob <rev>:<path> | od -c`。本仓库 blob 以 LF 存储
  （个别历史文件例外，如 `MujocoVehicleMeshTests.cs` 为 CRLF）——改文件前先 `od -c`
  看原状，**保持每个文件自己的行尾**，否则 diff 全文件翻转污染 blame。
