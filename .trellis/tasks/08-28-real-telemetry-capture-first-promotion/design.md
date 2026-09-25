# 设计:真机遥测采集与首轮保真度晋升

> 2026-09-25:开放决策已确认(记录见 §1)。本文件固化技术方案;执行顺序见
> [implement.md](./implement.md)。不重写 `Sim.Calibration` 标定算法、不改
> `Sim.Core`、不动 holdout 门槛——这些是 PRD 明确的 Out of Scope。

## 1. 开放决策(已确认):混合式采集

PRD Open Decision(新建实时采集程序 vs 离线归一化既有车端日志)确认为**混合式,
先盘点再补**:

1. **路径①(优先)**:车端已有日志/记录 → 离线归一化转换器(任务内新增的工具
   程序,不入运行时)→ `telemetry-v1` JSON → `calibrate`。
2. **路径②(补缺)**:盘点发现字段/频率不足的试验类型 → 写**最小**采集辅助
   (脚本/表单级,不涉及车载计算机通信链路改造)→ 人工执行采集 → 归一化。
3. 盘点以**一份真实日志样本**为输入,对照 §3 采集卡的字段/频率需求逐项评估;
   评估结论(够/缺/受影响参数)写入 `evidence/field-mapping.md`(R3 的字段缺口
   报告就是它的第一节)。

影响范围:不做车载计算机通信链路改造、不做实时时间同步工程;若盘点发现
时间同步无法满足 `frames[].t` 严格递增 + SI 契约,该试验类型转入路径②,
并在报告与缺口清单中如实标注数据来源为"人工记录归一化"。

## 2. 归一化转换器(路径①的工具)

- **形态**:独立控制台工具(建议 `tools/telemetry-import/` 或任务内脚本目录),
  输入车端原始日志 + 一份人填的 `field-mapping.json`(字段→telemetry-v1 映射、
  单位换算、坐标/符号约定),输出 `telemetry-v1` JSON 草稿;**人工核对后**才
  进入 `calibrate`。
- **边界**:转换器只做格式/单位/时间轴整理,不做任何物理推断(不从无关传感器
  推参数、不插值补齐缺失帧——缺帧的 trial 按无效列出,回到源数据修正或剔除,
  见 PRD R3)。
- **确定性**:同输入 + 同映射 → 逐字节一致输出;转换器记录源文件 SHA-256、
  映射文件 SHA-256 与自身版本,写入输出 `capture.notes`,满足 R1 可追溯性。
- **验收**:转换器加最小往返测试(样例片段 → 归一化 → `Telemetry.Validate`
  通过/按预期拒绝);随任务提交,不引入第三方依赖。

## 3. 六类试验采集卡(按 PRD R2 最小样本矩阵)

通用规则(全部试验适用):每 trial 唯一 ID;`set` 在采集时就固定标 `fit`/
`holdout`,同段数据永不复用;单位 SI(m/s/rad);帧间隔 ≤0.05 s 或按各卡要求;
`capture.source="real"`。

| # | kind | 目的参数 | 采集卡要点 | 最小量(硬门禁) |
|---|---|---|---|---|
| 1 | `lateral_coast` | `vehicle.latFrictionK` | 给横向初速后零指令滑行,记 `{t, robot{x,y,th}, command{v,w}}`;建议 3 档初速 × 每档 ≥2 trial | fit≥2、holdout≥2 个有效 trial;每 trial ≥4 对相邻衰减对 |
| 2 | `angular_coast` | `vehicle.angDamping` | 给初角速度后零指令自转衰减,同上 | 同上 |
| 3 | `block_push` | `BLOCK_MU_K` | 推块后不再触碰,记 `{t, block{x,y}}`;初速覆盖 0.5–2 m/s 多档 | fit/holdout 分离,各 ≥4 对 |
| 4 | `collision` | `COLLISION_RESTITUTION` | 撞墙/对撞;记录接触法线 `normal`(或 `wall` 方向)+ 撞前/后 `{vx,vy}` | 每组 ≥3 次有效入射(法向分量 >0.05 且反向);fit/holdout 分离 |
| 5 | `stall` | `STALL_SPEED` | 指令非零,记实测速度 + `stalled:bool` 标签 | fit ≥6、holdout ≥6,正/负标签齐全 |
| 6 | `mount` | 验证 `MOUNT_V_MIN`/`MOUNT_ANGLE_MAX`(不拟合) | 以不同 `vn`/`vt` 攻 6cm 台沿,记 `outcome:bool`;速度桶 `<0.3/0.3–0.5/0.5–0.75/0.75–1.0/≥1.0` m/s,角度桶(atan(vt/vn))`≤10°/10–15°/15–20°/20–25°/>25°` | holdout ≥12 且成败都有;≥3 桶有样本,每桶 ≥2 |

采集顺序建议:1→2(同一组滑行场次)→3→4→5→6(登台最后,便于利用前面调好的
速度控制);每类当场记 `evidence/raw-hash.tsv`(原始文件 SHA-256)。

## 4. 证据链与目录约定(R1/R7)

```
telemetry/data/<vehicle>-<date>/        ← 原始数据(git 忽略,不入库)
telemetry/data/<vehicle>-<date>/raw-hash.tsv
任务目录 evidence/
  field-mapping.md     ← 车端日志字段盘点 + 缺口报告(R3 第一节)
  field-mapping.json   ← 转换器输入映射(含单位换算/符号约定)
  experiment-plan.md   ← 本轮采集方案(基于 §3 采集卡具体化)
  commands.md          ← 实际执行的归一化/标定命令 + 工具版本/提交号
  review.md            ← 人工审核结论(R5)+ 晋升/保持决定
  regression.md        ← R6 回归结果
  gaps.md              ← 未晋升项缺口 + 下一轮最小补采方案(R5/AC4)
calibration/            ← 标定报告与候选场景(git 忽略;摘要入任务目录)
```

- 提交的摘要/报告不含敏感设备标识,但保留 vehicle 别名 + 日期 + 哈希,可从
  本地原始数据复现(R7)。
- `fidelity.json` 仅在人工评审后经 `--update-fidelity --force` 变更,`lastCalibration`
  自动记录 telemetry SHA-256(CalibrateCommand 既有行为)。

## 5. 晋升与回归流程(R4/R5/R6)

1. 归一化产物 → `Telemetry.Validate` 全过(无效 trial 列出/剔除,留痕)。
2. `calibrate --input <telemetry-v1> --vehicle-id <id> --base-scenario scenarios/wushu-ring-2026.json --out calibration/<...>.json --emit-scenario scenarios/calibrated-<vehicle>.json`
   → 报告逐项列出覆盖/holdout/资格/失败原因。
3. **人工评审** `evidence/review.md`:原始证据、异常值处理、报告结论、候选参数
   是否接受。mount 仅验证:>10% 误判或覆盖不足 → 如实记"当前边界未获真机验证"
   或"模型不足",保持 `hand_drawn`。
4. 仅对 eligible 子系统执行 `--update-fidelity --force`;部分晋升允许。
5. 回归门禁(R6,全过才提交晋升):`dotnet test`、`replay-check replays/seed-42.json`
   (及其他旧回放)、Godot `--parity-check`、候选场景 smoke(`match --scenario
   scenarios/calibrated-<vehicle>.json --seed 42`)。失败 → 修复或回退候选参数,
   重新生成报告;**不提交晋升**。

## 6. 与其他任务/契约的边界

- 遵守 `.trellis/spec/sim/index.md` 标定契约:校验失败不出报告;拟合器与内核共享
  模型常数;合成数据永不晋升;mount verify-only。
- 传感器判定模型(MBri)是**另一条证据线**,不在本任务范围(已有
  `sensor-calibration import` 与本地 `calibration/mbri-summer-*` 报告可作参考)。
- MuJoCo 新模式登台差异由登台适配任务负责;本任务的 mount 验证对象是内核
  轴对齐门控(`MOUNT_V_MIN`/`MOUNT_ANGLE_MAX`),与三维后端无关。
- 真实数据到位后,执行体应换新分支;本分支规划工件先行提交不阻塞。
