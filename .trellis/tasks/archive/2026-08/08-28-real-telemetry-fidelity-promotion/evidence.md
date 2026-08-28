# 真机遥测保真度门禁证据

检查日期：2026-08-28

## 输入清单

- `telemetry/data/` 仅包含 `.gitkeep`，没有可供 `telemetry-v1` 校验的 JSON 输入。
- 仓库内唯一的遥测夹具是 `src/Sim.Tests/fixtures/telemetry-synthetic-v1.json`，其 `capture.source` 为 `synthetic`，只能验证拟合器，不能触发 fidelity 晋升。
- 因此不存在 `capture.source=real` 的车辆、会话、日期、SHA-256 或 fit/holdout 分组可登记。

## 缺失门禁项

没有任何真实试验覆盖以下类型：

| kind | 缺失的核心证据 |
| --- | --- |
| `lateral_coast` | 横向速度衰减与位姿序列，fit + holdout |
| `angular_coast` | 角速度衰减与位姿序列，fit + holdout |
| `block_push` | 能量块轨迹，fit + holdout |
| `collision` | 接触法线及碰撞前后速度，fit + holdout |
| `stall` | 非零指令、实测速度和 `stalled` 标签，fit + holdout |
| `mount` | `vn`、`vt`、成败结果，留出集需同时覆盖成功/失败及速度×角度桶 |

因此本次没有可报告的真实 fit 样本、留出样本、RMSE、准确率、误判率或覆盖率；四个物理晋升项均保持阻断。

## 结论

`fidelity.json` 保持原状：`friction`、`collision`、`stall` 为 `uncalibrated`，`mount` 为 `hand_drawn`。没有写入 profile、场景参数或 fidelity 晋升记录，也没有用合成数据填补真实证据。

收到真实数据后，必须先通过 telemetry-v1 的 SI 单位、时间单调性、字段完整性、试验类型、样本量和 fit/holdout 独立性校验，再逐子系统运行现有 `calibrate` 门禁；只有 `source=real` 且留出指标达标的项才能人工复核后晋升。

## 工具链回归

对 `src/Sim.Tests/fixtures/telemetry-synthetic-v1.json` 运行 `calibrate`：拟合器数值输出正常，但报告 `source=synthetic`，`eligibility.friction/collision/stall/mount` 均为 `false`，并明确提示不得晋升 fidelity。报告内容指纹为 `0e71236f0169fb721c1d739dd30410a1304c327dc5e23bbb26ad0982088e3e97`；仓库 `fidelity.json` 未改变。
