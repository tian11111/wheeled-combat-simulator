# MBri 传感器数据审计

## Inventory

- 根目录：`D:/project/robocup/MBri/data`
- CSV 总数：187
- 主要原始族：四路灰度 `t,front,rear,left,right`；前头 ADC `t,left,right,diff,valid`；铲下 ADC `t,left,right,valid`；另有巡台、重登台、视觉、主状态机和电机日志。
- 已存模型：`gray_model.csv`、`gray_model_summary.csv`、`front_adc_model.csv`、`front_adc_summary.csv`、`shovel_model.csv`。

## Meaning Of The Three Models

### Gray

`MBri/gray.py::GrayRiskModel` 对四路 ADC 做奇数窗口中值滤波，再按每路 `edge_reference` / `center_reference` 归一化为 zone，四路中值形成 `zone_score`；`white_enter/clear` 是逐路白边迟滞阈值。

这不是空间灰度地图。原始 CSV 没有 `x/y/th`，不能构造模拟器 `GrayGridMap(width,height,bounds,values)`。

### Front ADC

`front_adc_model.csv` 是两个模拟红外通道的绝对差阈值（left-right）和最小信号。当前 MBri `ir.py::IrDirectionModel` 使用归一化比值阈值，生产 `config.py` 采用 `ratio=0.20`、`signal_min=800`，与模型 CSV 的 `diff_low=-75.1`、`diff_high=63.5`、`signal_min=296` 不是同一运行模型。

### Shovel

`shovel_model.csv` 用两路 ADC 的滚动中值、`min>enter` 判悬空和 `max<clear` 判收回。生产状态机见 `MBri/shovel_guard.py`。

## Source Drift Evidence

已存模型与 2026-08-27 使用当前原始目录重算的结果：

| Model | Stored | Recomputed from current directory | Current MBri config |
|---|---|---|---|
| Gray near-edge | enter 0.50 | enter 0.35 | enter 0.35 |
| Gray front refs | edge 494, center 817, white-enter 1560 | edge 496, center 893, white-enter 1667 | edge 494, center 817, white-enter 1560 |
| Front ADC | diff -75.1/63.5, signal 296 | same | ratio 0.20, signal 800 |
| Shovel | enter 668.5, clear 1358.8 | enter 991.8, clear 1358.5 | enter 1134.1, clear 1317.5 |

原因不是简单的“取最新文件”：目录混有不同日期/批次，生产配置还包含后续真机行为调参。缺少车辆 id、传感器批次和采集会话 manifest 时，自动合并会制造虚假精度。

## Simulator Boundary

- `Sim.Core.FieldModel` 支持 `SetGrayMap(GrayGridMap)`，但地图需要场地坐标和网格；当前数据不满足。
- `SensorSampler` 目前输出规范化灰度约 0..1000、红外约 0..1，并由 `SimParameters` 的全局阈值驱动内置 FSM。
- `VehicleProfile.SensorProfile` 描述几何、类型、范围和噪声，不描述 MBri 的滤波、逐路参考或状态机迟滞。
- 回放头已冻结 vehicles、parameters 和 fieldGray 引用。直接改变默认传感器输出会改变 FSM 事件序列。

## Recommendation

先做离线 `sensor-calibration-v1` 导入和原始日志回放门禁，不在同一任务中改变运行时传感器。产物只有在数据批次明确、模型与回放指标达标后才成为后续 `sensor-response-runtime-profile` 任务的输入。

这样保留真实数据价值，同时避免把混合批次阈值、场地灰度、传感器电气响应和模拟器规则耦合在一起。
