# telemetry/ — 真机遥测实验与导出规范

本目录定义**如何采集真机数据**并喂给离线标定工具
(`dotnet run --project src/Sim.Cli -- calibrate`)。
导出的原始数据放入 `telemetry/data/`(不入库);模板见
`template.telemetry-v1.json`。

## 通用要求

- 单位固定 SI: 长度 **米(m)**、时间 **秒(s)**、角度 **弧度(rad)**,`units` 必须声明一致。
- 每个 trial 有唯一 `id`、`kind`(类型)、`set`(`fit` 拟合集 / `holdout` 留出验证集)。
- `frames[].t` 严格递增; 缺失/NaN 字段会被入口校验拒绝,不会进入拟合。
- 晋升保真度**只认** `capture.source: "real"` 且留出集误差达标的子系统;
  合成数据(如仓库 fixtures)只能验证工具,永不升级 fidelity。
- 每个参数至少需要**两类独立试验来源**(拟合集 + 从未参与拟合的留出集)。

## 各试验类型的最低采集量

| kind | 目的参数 | 每 trial 内容 | 最少可用样本 / 建议 |
| --- | --- | --- | --- |
| `lateral_coast` | `vehicle.latFrictionK` | 给定横向初速后零指令滑行, 每 ≤0.05s 记录 `{t, robot{x,y,th}, command{v,w}}` | ≥4 个相邻衰减对, 建议 ≥3 条不同初速; fit≥2, holdout≥2 |
| `angular_coast` | `vehicle.angDamping` | 给定初角速度后零指令自转衰减, 同上 | 同上 |
| `block_push` | `BLOCK_MU_K` | 推块后记录块轨迹 `{t, block{x,y}}`, 不再触碰 | ≥4 对, 初速 0.5–2 m/s 多档; fit+holdout 分开 |
| `collision` | `COLLISION_RESTITUTION` | 撞墙/对撞, 提供接触法线 `normal`(或 `wall` 方向)与撞击前后 `{vx,vy}`(自车, 可选对手) | ≥3 个有效入射(法向分量>0.05 且反向); fit+holdout 分开 |
| `stall` | `STALL_SPEED` | 指令非零同时记录实测速度与**是否堵转标签** `stalled:bool` | ≥6 个正反标签齐全的样本; holdout 同样 ≥6 |
| `mount` | (验证 `MOUNT_V_MIN`/`MOUNT_ANGLE_MAX`) | 以不同入射法向速度 `vn`、切向速度 `vt` 攻台沿, 记录结果 `outcome:bool` | 留出集 ≥12 且成败都有; 覆盖 ≥3 个速度×角度桶, 每桶 ≥2 次 |

## mount 试验设计要点(唯一"验证不拟合"的子系统)

模拟器门控是确定性的: `上台 ⇔ vn > MOUNT_V_MIN 且 |vt| < vn·tan(MOUNT_ANGLE_MAX)`。
标定器不拟合这两个门限, 只用真机成败结果**验证**它们是否分得开:

- 速度桶: `<0.3 / 0.3–0.5 / 0.5–0.75 / 0.75–1.0 / ≥1.0` m/s;
  角度桶(atan(vt/vn)): `≤10° / 10–15° / 15–20° / 20–25° / >25°`。
- 留出误判率 > 10%, 或覆盖不足/成败缺类 → 工具如实报告"模型不足",
  fidelity 的 `mount` 保持未标定, 不会假装标好。
- 若长期分不开(例如真机带铲/带重心转移, 6cm 台阶实际可斜穿),
  说明当前轴对齐门控模型不足, 需要新立项的模型改造(超出本工具职责)。

## 跑一次标定

```powershell
# 1) 合成自检(验证拟合器数值, 永不晋升):
dotnet run --project src/Sim.Cli -- calibrate `
  --input src/Sim.Tests/fixtures/telemetry-synthetic-v1.json `
  --out calibration/synthetic-report.json

# 2) 真机数据(放入 telemetry/data/):
dotnet run --project src/Sim.Cli -- calibrate `
  --input telemetry/data/robot-01-2026-09.json `
  --out calibration/robot-01-report.json `
  --emit-scenario scenarios/calibrated-robot-01.json

# 3) 人工复核报告后, 显式登记保真度(仅达标子系统):
dotnet run --project src/Sim.Cli -- calibrate `
  --input telemetry/data/robot-01-2026-09.json `
  --out calibration/robot-01-report.json --force `
  --update-fidelity

# 4) 回归门禁: 新场景必须与旧回放互不干扰
dotnet run --project src/Sim.Cli -- match --seed 42 --scenario scenarios/calibrated-robot-01.json --duration 3
dotnet run --project src/Sim.Cli -- replay-check src/Sim.Tests/fixtures/godot-parity-seed42.json
```

`calibrate` 报告含: 输入 SHA-256、每参数**拟合集/留出集**双列指标、
晋升条件评估与失败原因、推荐 patch(车辆+参数)。报告内容指纹
(`contentSha256`)排除生成时间, 同一输入重跑逐字节一致。
