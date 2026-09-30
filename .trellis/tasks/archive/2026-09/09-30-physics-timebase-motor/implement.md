# 执行计划

前置阅读：`prd.md`（需求与验收）、`design.md`（决策①-⑨与披露清单）、`.trellis/spec/sim/index.md`（Sim 层铁律与物理后端契约；本任务要追加一条调校教训）。

环境（本机实测可用，全文命令以此为准）：

```bash
DOTNET="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"   # 8.0.425
py -3.12 -V    # Python 3.12.10
# 若还原失败再加 NUGET_PACKAGES="C:/Users/Neco/.nuget/packages"（写档实测：不设也能跑）
# cmd.exe 下把 "$DOTNET" 写成带引号的完整路径 "C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"
```

## 基线（**动手前先复跑一遍存底**，写档时实测值）

```bash
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1
# 写档实测: 已通过! - 失败: 0，通过: 496，已跳过: 1，总计: 497，持续时间: 9 s
#   唯一 Skip = MujocoScoreEdgeGuardTests.OfficialSeed42_ScoresRealBuffWithoutRepeatedUsDrops

for f in replays/*.json; do "$DOTNET" run --project src/Sim.Cli -- replay-check "$f"; done
# 写档实测: 6/6 "PASS: replay reproduces the recorded match bit-for-bit."
#   (seed-42.json: scores 4:49 (expected 4:49) events 752/752)

"$DOTNET" run --project src/Sim.Cli -- match \
  --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario scenarios/wushu-ring-2026-mujoco-v2.json --events \
  > tmp/timebase-motor-baseline-scan.txt 2>&1
# 写档实测(批 0 = 慢动作时基 0.02 s/tick): 墙钟 12 s
#   翻覆(Incapacitated 行) 11 | 自动重启 9 | QACC 0 | 跑满场 10/11 | 我方胜 6 平 0 负 5
#   seed=42 为唯一非满场: done=恢复次数超限 → 停车
```

扫描指标抽取（把本脚本存 `tmp/scan-metrics.py`，三轮扫描共用；写档时已实测）：

```python
import io, re, sys
p = sys.argv[1]
lines = io.open(p, encoding='utf-8', errors='replace').read().splitlines()
seeds = [l for l in lines if l.startswith('seed=')]
pat = re.compile(r'score 我方 ([\d.]+) : ([\d.]+) 对手')
wins = draws = 0
for l in seeds:
    m = pat.search(l)
    if m:
        u, t = float(m.group(1)), float(m.group(2))
        wins += u > t
        draws += u == t
print(f'文件: {p}')
print(f'翻覆(Incapacitated 行): {sum("Incapacitated" in l for l in lines)}')
print(f'自动重启(真实重启 行): {sum("真实重启" in l for l in lines)}')
print(f'QACC 警告: {sum("QACC" in l for l in lines)}')
print(f'跑满场(done=比赛时间结束): {sum("done=比赛时间结束" in l for l in seeds)} / {len(seeds)}')
print(f'我方胜={wins} 平={draws} 负={len(seeds)-wins-draws}')
```

```bash
python tmp/scan-metrics.py tmp/timebase-motor-after-batch1.txt
```

## 批 1：时基修复与单一真值化（先行）

1. **常量三件套**（`src/Sim.Mujoco/MujocoModel.cs:14-15`）：

   ```csharp
   internal const double MjcTimestep = 0.002;              // 唯一真值
   internal const double SubstepSeconds = MjcTimestep;     // 子步 = 真值
   internal const int SubstepsPerTick = 25;                // 0.05 / 0.002
   ```

   注释写明：`25 × 0.002 == 0.05` 与 `MujocoPhysicsBackend.cs:102-104` 的 tick 时长强绑定；任何改动必须先改 `MjcTimestep`。允许 `(int)(0.05 / MjcTimestep)` 推导（写档实测 double 商 = 25.0，精确）。

2. **MJCF 去字面量**：`Header`（`MujocoModel.cs:151-156`）里 `timestep="0.002"` 改为 `N(MjcTimestep)`；确认 `N()`（`:382`）仍输出 `"0.002"` ⇒ **XML 字节不变**。

3. **`contactTime` 修正**（`src/Sim.Mujoco/MujocoPhysicsBackend.cs:127`）：`var contactTime = (i + 1) * MujocoModel.MjcTimestep;`（i = 0..24 ⇒ 0.002…0.05）。

4. **注释级修复**：`MujocoPhysicsBackend.cs:16-25` 的 dt 叙述、`MujocoModel.cs:44-51` 的"2.5kg"（场景已是 3.5kg，`scenarios/wushu-ring-2026-mujoco-v2.json:38,61`）改正。

5. **新测试**（建议 `src/Sim.Tests/MujocoTimebaseTests.cs`）：
   - `SubstepsPerTick * MjcTimestep == 0.05`（IEEE 精确，`Assert.Equal` 直比）；
   - 生成 MJCF 含 `timestep="0.002"` 且来自常量（改常量则 XML 跟随——以断言"XML 子串 == N(MjcTimestep)"钉住，不做反射）；
   - 批 1 后 v1 模型哈希不变（复用现有守卫 `MujocoVehicleMeshTests.cs:138-144` 即可，不新增重复断言）；
   - 归属语义回归：构造同 tick 内 maxT 相同的 us/them 接触 → `simultaneous`（可复用 `BlockAttributionTests.cs:51-57` 的口径，或加一条末子步 = 0.05 的断言）。

6. **断言重标定（先跑后改）**：
   - `IncapacitatedTests`：v1 seed 19 在 1:1 时基下不一定翻覆。处理顺序：① 直接跑，若 `MujocoMatch_FlippedRobotStopsInIncapacitatedState` 仍过 → 只更新注释里的实测描述；② 若红 → 用 `tmp/flipscan` 式扫描选出新翻覆 seed：把 `tmp/flipscan/Program.cs:12` 的场景路径换成 `scenarios/wushu-ring-2026-mujoco.json`（v1，与 `IncapacitatedTests.cs:15-20` 同场景）、`:14` 的 seed 列表扩到 1..40，`"$DOTNET" run --project tmp/flipscan`，从"大倾角次数 > 0"的种子里选出满足测试门的（翻覆 tick > 0、`INCAPACITATED` 宣告在 30 tick 内、事件 1-2 次；用测试本身复核），把 `IncapacitatedTests.cs:17` 的 seed 与注释一起更新，**不改 30 tick 门**。
   - `MujocoScoreEdgeGuardTests.OfficialSeed42_*`：跑一次去掉 Skip 的版本看实测；过则解除 Skip（保留断言原文），不过则保留 Skip 并把理由改成批 1 后的真实原因（如仍为 FSM 登台语义）。两个方向都必须在 `report.md` 留实测数据。
   - `SearchTurnCompensationTests`：记录模式不变，重测后把 `SearchTurnCompensationTests.cs:91-95,117` 的参考值/注释更新为批 1 实测（comp=1/4 的 `seconds`/`finalError`）。**不重立硬门**（候选 2/4/6/8 重扫属可选，不阻塞验收）。
   - `MujocoVehicleMeshTests`：批 1 应保持全绿；若哈希守卫意外变红 → 立即停下检查 `N(MjcTimestep)` 是否真的产出 `"0.002"`（不允许用"更新常量"掩盖）。
   - `TrainingResetPerformanceTests`：**门不改**，重跑确认通过并记录实测 p95。

7. **spec 回写**（`.trellis/spec/sim/index.md`"模型调校教训"节追加）：
   - "物理子步常量与 MJCF `option timestep` 必须同源（`MjcTimestep` 单一真值），`SubstepsPerTick × MjcTimestep == tickSeconds` 有测试钉住；09-29 只改 XML 字面量导致 0.02 s/tick 慢动作的教训。"

8. **批 1 验证**（全绿才进扫描）：

   ```bash
   "$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1
   for f in replays/*.json; do "$DOTNET" run --project src/Sim.Cli -- replay-check "$f"; done
   "$DOTNET" run --project src/Sim.Cli -- replay-check replays/seed-42.json   # 逐位 PASS 硬门
   ```

9. **批 1 扫描（第一轮，时基归因）**：

   ```bash
   "$DOTNET" run --project src/Sim.Cli -- match \
     --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario scenarios/wushu-ring-2026-mujoco-v2.json --events \
     > tmp/timebase-motor-after-batch1.txt 2>&1
   python tmp/scan-metrics.py tmp/timebase-motor-after-batch1.txt
   grep -c QACC tmp/timebase-motor-after-batch1.txt    # 期望 0
   ```

   - 记录墙钟耗时（`date +%s` 前后差）与上面全部指标，填入 `report.md` 对比表"批 1 后"行。
   - 与"批 0"行的差异**只归因于时基**；不得把批 2 的因子混进解释。

## 批 2：电机扭矩级建模

1. **电机常量**（`src/Sim.Mujoco/MujocoModel.cs:22-28` 替换，带真值注释）：

   ```csharp
   /// 2342 减速电机真值: 120 rpm → ω_noload = 4π rad/s; 输出扭矩 1.72 N·m。
   internal const double MjcStallTorque = 1.72;                        // forcerange=±此值
   internal const double MjcNoLoadSpeed = 12.566370614359172;          // ctrlrange=±此值 (=4π)
   internal const double MjcServoKv = MjcStallTorque / MjcNoLoadSpeed; // = 0.13687325105903
   ```

   `Actuators`（`MujocoModel.cs:206-223`）改用新常量；旧名 `WheelForceLimit/WheelAngularSpeedLimit/WheelServoKv` 删除（避免双真值）。

2. **`SetControls` duty 口径**（`MujocoPhysicsBackend.cs:157-177`，详见 design 决策④）：原地转向补偿（`:164-167`）之后，

   ```csharp
   var dutyLeft  = Math.Clamp((cmdV - cmdW * halfTrack) / maxSpeed, -1, 1);
   var dutyRight = Math.Clamp((cmdV + cmdW * halfTrack) / maxSpeed, -1, 1);
   controls[offset]     = dutyLeft  * MujocoModel.MjcNoLoadSpeed;
   controls[offset + 1] = dutyRight * MujocoModel.MjcNoLoadSpeed;
   controls[offset + 2] = controls[offset];
   controls[offset + 3] = controls[offset + 1];
   ```

   `maxSpeed = robot.Vehicle.MaxSpeed`；轮半径不再出现在该换算里（duty 分母口径），但 `RadiusFor` 的其它消费者不动。**不许**写成单标量 `|cmdV|/MaxSpeed`（会把原地转向压成 duty=0，design 决策④反例）。

3. **电池选项接口（默认禁用）**：`internal readonly record struct MotorDriveOptions { public bool Enabled { get; init; } /* 默认 false */ ... }`，只在 `SetControls` 的 duty 折算处留调用点；`Enabled == false` 时表达式与第 2 步逐位一致。不新增场景字段、不改协议；注释写明"启用需要实测电压-电流曲线，未标定前禁止填数"。

4. **新单测**（建议 `src/Sim.Tests/MujocoMotorModelTests.cs`）：
   - `MjcServoKv == MjcStallTorque / MjcNoLoadSpeed`（三位有效数字 0.1369）、`MjcNoLoadSpeed == 4π`（1e-12）、`MjcStallTorque == 1.72`；
   - MJCF 文本断言（`MujocoModel.Generate(...).Xml` 可用，测试已 InternalsVisibleTo）：`kv="0.13687325105903"`、`ctrlrange="-12.566370614359172 12.566370614359172"`、`forcerange="-1.72 1.72"`——三个字符串写档实测于 .NET 8 的 `"R"` 格式（`tmp/fmtprobe`），与 `N()` 输出一致；
   - duty 恒等：`ω=0` 时起步扭矩 = duty × 1.72（纯函数口径即可，或断言 `kv × (duty × ω_noload) == duty × τ_stall`，1e-12）；
   - 直线 duty=1 → `ctrl == ω_noload`；原地转向（cmdV=0, cmdW=2）→ 两轮 duty 反号、`|duty| > 0`；
   - 电池接口默认禁用：默认生成 MJCF 与启用分支前逐位一致（同字符串/同哈希）。
   - **可选**（若愿意加 struct 偏移读取 `mjModel.actuator_gainprm/ctrlrange/forcerange` 则做编译后断言；不做也不算缺口，文本断言已覆盖生成面）。

5. **行为检查（集成级，可选但建议）**：平坦地面满 duty 直线跑 N tick，实测 `|V| ≤ 0.41 m/s` 且显著高于 0.3（真车极速 0.408 的上界）。若测，记录实测值进 report。

6. **哈希与回放**（design 决策⑥）：模型哈希必变 ⇒
   - 重录：`"$DOTNET" run --project src/Sim.Cli -- replay-record --seed 42 --scenario scenarios/wushu-ring-2026-mujoco.json --out tmp/mujoco-seed42-recorded-0930.json`，用该文件头部值更新 `src/Sim.Tests/MujocoVehicleMeshTests.cs:21` 常量 + 注释（来源文件 + 日期）；
   - 跑 `replay-check` 全部 `replays/*.json`（legacy 必须仍逐位 PASS）；
   - `tmp/mujoco-seed42-replay.json`（旧 mujoco 身份）应被正确拒绝——把它作为"门禁生效"证据记录，不做修复。

7. **断言重标定**：重复批 1 步骤 6 的清单（IncapacitatedTests 可能再次变化 → 再跑一次 flipscan；SearchTurnCompensation 参考值更新；得分守卫按实测记录）。**每处改动在 report 写明旧值/新值/因果。**

8. **批 2 验证**：

   ```bash
   "$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1
   for f in replays/*.json; do "$DOTNET" run --project src/Sim.Cli -- replay-check "$f"; done
   ```

9. **批 2 扫描（第二轮，电机归因）**：

   ```bash
   "$DOTNET" run --project src/Sim.Cli -- match \
     --seeds 1,2,3,4,5,6,7,8,9,10,42 --scenario scenarios/wushu-ring-2026-mujoco-v2.json --events \
     > tmp/timebase-motor-after-batch2.txt 2>&1
   python tmp/scan-metrics.py tmp/timebase-motor-after-batch2.txt
   ```

10. **报告**：`report.md` 写结论先行 + 三行对比表 + 每条断言重标定的因果 + spec 更新 + 未覆盖披露（RL 吞吐套件、桌面目检、真机对照）。

## 报告对比表模板（三行必填）

| 轮次 | 日志 | 墙钟 | 翻覆(Incapacitated 行) | 自动重启 | QACC | 跑满场 | 比分/胜负 |
|---|---|---|---|---|---|---|---|
| 批 0（写档基线，0.02 s/tick） | `tmp/timebase-motor-baseline-scan.txt` | 12 s | 11 | 9 | 0 | 10/11 | 6 胜 0 平 5 负 |
| 批 1 后（0.05 s/tick，工程执行器） | `tmp/timebase-motor-after-batch1.txt` | 实测 | 实测 | 实测 | 期望 0 | 实测 | 实测 |
| 批 2 后（0.05 s/tick，真车电机） | `tmp/timebase-motor-after-batch2.txt` | 实测 | 实测 | 实测 | 实测 | 实测 | 实测 |

## 验证矩阵（全绿才算完成）

```bash
DOTNET="C:/Users/Neco/AppData/Local/Programs/robot-simulator-dotnet/dotnet.exe"

# 1) 全量单测（批 1/批 2 各跑一次；写档基线 496 通过 + 1 Skip）
"$DOTNET" test src/Sim.Tests/Sim.Tests.csproj -m:1

# 2) legacy 身份与行为不变（6 个 tracked 回放全 PASS）
for f in replays/*.json; do "$DOTNET" run --project src/Sim.Cli -- replay-check "$f"; done

# 3) 两轮行为扫描 + 指标
python tmp/scan-metrics.py tmp/timebase-motor-after-batch1.txt
python tmp/scan-metrics.py tmp/timebase-motor-after-batch2.txt

# 4) 差异卫生
git diff --check
```

## 硬约束

- **单一真值**：`MjcTimestep` 是唯一物理步长来源；MJCF 与 C# 只能引用它。`SubstepsPerTick × MjcTimestep == 0.05` 有测试。
- **批 1 不改 MJCF 字节**（v1 哈希守卫必须自然通过）；批 2 改哈希必须走"重录 + 更新守卫"流程，不用"放宽断言"过关。
- **断言不许弱化**：`TrainingResetPerformanceTests` 门、`IncapacitatedTests` 的 30 tick 门、得分守卫语义都不得改；Skip/放宽必须有实测依据并写明因果。
- **duty 必须按轮计算**（原地转向不许被清零）；电池接口默认禁用且不改协议/场景。
- **不做**：坡道几何、FSM 特权收敛、RL 观测特权、电池压降启用、FSM 速度档/场景 maxSpeed 改动、RL 重训、`fidelity.json` 晋升、旧 fixture/旧回放/基线数字"修绿"。
- **RL 吞吐门不在本任务执行**：如实披露为未覆盖；不得用"没跑"伪装通过。

## 交付

- 代码：`src/Sim.Mujoco/MujocoModel.cs`、`src/Sim.Mujoco/MujocoPhysicsBackend.cs`、`src/Sim.Tests/` 新增时基/电机测试与重标定断言。
- 文档：`.trellis/spec/sim/index.md` 教训追加。
- 报告：本任务 `report.md`（三行对比表、断言重标定因果、披露与未覆盖项）。
- 产物（gitignored，按需留档）：`tmp/timebase-motor-*.txt`、`tmp/scan-metrics.py`、`tmp/mujoco-seed42-recorded-0930.json`。
