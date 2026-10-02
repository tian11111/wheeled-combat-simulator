# Implement：MuJoCo 域校准

> 纪律：**每步先测后改**、改完即测；任何一步让 R5（legacy 逐位 + 默认 MJCF 逐字节）变红，
> 立刻 `git checkout -- src/Sim.Mujoco` 回滚该步再继续。顺序按"先测量后动手、先训练场景后真车场景"。

## 步骤

- [x] **0. 冻结基线数字**（已完成，落证据）：88% SCORE_BLOCK / 0 推块得分 / 原地转向 2.8%–7.5% /
      补偿扫描 / 质量扫描 / 接触参数扫描。产物 `tmp/mbri-hunt/*`（不入库）→ 摘要进本任务 `evidence/`。

- [x] **1. 测量手段**（已完成）
      - `WheelContactOptions` 注入点（condim/摩擦三元组，默认中性 ⇒ MJCF 逐字节不变）
      - 遥测探针 `src/Sim.Tests/MujocoYawProbeTests.cs`（逐 tick ctrl / qvel / 实际偏航 / 位置 / 接触对）
      - 验证：`dotnet test --filter "FullyQualifiedName~MujocoMotorModel|FullyQualifiedName~MujocoTimebase"` 全绿
      - 回滚点：删探针文件 + `git checkout -- src/Sim.Mujoco`
      - 备注：探针最终**转正**为 `MujocoDomainCalibrationTests`（断言用例），临时探针文件已删（见步骤 8）

- [x] **2. 限制器定位**（目的已达成，手段替代披露）：原计划"efc_force 逐轮力读数"未实施；
      以**质量缩放扫描**（×0.5/×2 ⇒ 偏航 ∝1/mass）+ 摩擦扫描（f3..f16 双峰）定位为
      "驱动力矩 ÷（各向同性接触的横向刷矩）受限、存在最优摩擦点"——过约束滑移转向的
      特征。efc_force 读取保留为后续保真度轮的可选手段（`MujocoContacts.VisitWithPos`
      已提供接触位置遍历）。

- [x] **3. v1（训练场景）转向权限标定** —— **达标 2.598 rad/s**（≥2.0；锚点 MOTOR_TURN_CALIBRATION ~2.4）
      - 定值：摩擦 f5（每模型默认 v1=5.0）+ 补偿 4→10；登台冒烟通过（双方 mount，见 report §3）
      - 验证：`MujocoDomainCalibrationTests.InPlaceTurn_V1…`（断言 ≥2.0）；`MujocoMotorModelTests` 14/14 不破

- [x] **4. v2（真车场景）转向权限标定** —— **未达 ≥2.0，实测上限 1.185 rad/s（已披露）**
      - 定值：摩擦 f6（每模型默认 v2=6.0）+ 补偿 10；直线极速断言（0.30–0.40 m/s）不变
      - 未达标根因（见 report §2）：各向同性接触摩擦表达不了胎纹各向异性，1.0–1.35 rad/s
        为该建模方式实测天花板；用例 `InPlaceTurn_V2…StaysInMeasuredCeilingBand` 钉上限带，
        **不得用无锚点参数硬凑**，出路（接触各向异性/轮胎模型）属后续保真度轮

- [x] **5. 推击能力（R2）** —— **实测 1.201 m ≥ 0.3 m**
      - `MujocoDomainCalibrationTests.Push_SingleRobotFullDuty…`（增益块 (1.35,1.35)，2 s 满 duty 直推）

- [x] **6. mbri 在 MuJoCo 登台/回台（R4，判据执行期修正）** —— **达标（修正口径）**
      - 原"在台时间 ≥60%"系规划期把 10-01 的**回台率** 60–68% 误植为在台时间比例
        （prd.md 已内标注修正）；修正口径 = 倒车登台可用 + 回台率对照 legacy
      - 实测（11 种子 head-them-mbri）：开局登台 11/11；回台率中位 0.50（legacy 0.67）；
        残例（台角/围栏卡位）与 legacy 同类（legacy 2/11 vs mujoco 4/11 个种子 0%）
      - 校准前对照：mbri 整场卡走道从未上台 → 校准后恢复

- [x] **7. 端到端验收（R3 + R4）**
      - v1 seed42：**BlockScore 事件 t=368（+3）**，比分 20:0（基线 0 推块得分、1:2）
      - v2 seed42：6:18 / 掉台 7+3 / 无 BlockScore（观察项，随 report §3 披露）
      - RL 线：交接链路由 `MatchRunnerExhibitionTests`（外部 EchoController rlguard 实跑）
        与 `DesktopLiveDriverTests` 覆盖，全绿；既有 checkpoint 失配属预期（用户已拍板重训）

- [x] **8. 门禁与披露（R5 + R6）**
      - `dotnet test` 全量 **699/699（1 跳过=非 Windows 守卫）**；`replay-check replays/seed-42.json` **PASS 逐位**
      - 模型哈希重钉：v1 `4d7b1810…`（测试注释含三段变更史）；v2 `6ce51b19…`（无钉值）
      - 其余 MuJoCo 域钉值重录（entry tick 281→369 / 11 维观测样本 / 翻覆 seed 155→5 /
        对手上台场景修正），重录前逐一复核非真回归 —— 见 report §5
      - 披露：`evidence/report.md`（哈希影响/RL checkpoint 重训/fidelity 不晋升/锚点清单/出生踢跳残余）

## 验证命令速查

```bash
DOTNET=/c/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe
$DOTNET test src/Sim.Tests -v q --nologo
$DOTNET src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll replay-check replays/seed-42.json
$DOTNET test src/Sim.Tests --filter "FullyQualifiedName~MujocoYawProbe" -v q --nologo   # 遥测
$DOTNET src/Sim.Cli/bin/Debug/net8.0/Sim.Cli.dll match --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json --events
```

## 评审门

- 步骤 3/4 完成后各做一次**独立复看**（同口径重测 + 反例检查：登台/直线是否被破坏）。
- 步骤 7 通过后方可进入 8；8 绿才算任务完成（届时更新 spec 并在 journal 记录）。
