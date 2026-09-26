# 设计：MBri 控制器接入契约

## 边界与数据流

`Sim.Cli match --controller-us` 继续使用现有逐帧 JSONL：Observation → 单独的 MBri 示例适配进程 → `RobotController.update(..., now=obs.t)` → 左右轮命令转 `{v,w,requestId}`。示例放在 `controllers/`，通过明确的 MBri checkout 路径导入车端代码；不把 MBri 代码复制到模拟器，也不改变 `Sim.Core` 的 IO 边界。

## 适配契约

| 输入/输出 | 转换与模式 | 可信边界 |
| --- | --- | --- |
| 灰度 `gF/gB/gL/gR` | 按场景 0–1000 钳位后映射车端 ADC 0–10000；缺通道报错 | 数值接口匹配，尚未证明光学等效 |
| 六路低有效 GPIO IR | 官方场景无等价六路通道；默认标为 unavailable。smoke/oracle 模式可按已命名的距离/铲下通道合成，并记录阈值及极性 | 合成值不得标为真机传感器 |
| MBri `detections` | 默认无真实检测。显式 oracle 模式可将 `objects` 包装为 MBri 格式，并标记来源为 simulation truth；不伪称 confidence 是 YOLO 置信度 | oracle 结果不用于真机可迁移性结论 |
| 左右轮命令 `L/R` | 由显式标定系数 `k` (m/s 每命令单位)、实测轮距 `b` (m) 与方向约定计算 `v=(L+R)k/2`、`w=(R-L)k/b`，再走既有车辆限幅 | 无 `k`/`b` 时拒绝物理动作映射；只可做零动作协议检查 |
| 时间 | `now=obs.t`，校验 `tick` 单调与 0.05 s 步长；重复/跳帧按协议故障显式报告 | 与 headless 处理墙钟解耦 |

适配器启动参数至少明确 MBri checkout、`k`、`b`、传感器模式及用于合成 IR 的阈值；运行时在 stderr 首行输出参数与 `calibrated`/`estimated` 标记。只允许动作 JSONL 上 stdout。示例文档同时给出“协议 smoke/oracle”与“已标定运动映射”两种运行口径；前者不参与性能/保真度评估。

## MANUAL 与事件

CLI 在开赛前 Arm；第一个外部动作让该角色进入 Manual。适配器负责车端发令/登台/索敌等决策，内置 FSM 不再替该角色作决策。裁判仍按物理状态产出掉台、方块计分、比赛结束等事件；没有触发事件时，单场的我方事件计数可以为零。先用固定 seed/场景对 `--events` 的 Arm、FSM、裁判事件分类实测；只有裁判事件确实被吞掉才修改事件投影。控制器自身状态转移可写单独的诊断日志，不伪装成裁判事件，不改 EventKind 语义。

## 兼容与失败

维持现有 Observation/Action JSON 形状、request-id、零动作/fault 规则；缺原生 MuJoCo、MBri checkout、系数或必需通道时给出定位清楚的错误。官方场景和 legacy 回放不变；任何事件层修复先验证同 seed/动作序列的规则事件与比分逐位一致。标定结果按车体/日期独立保存，不写入 `fidelity.json`。
