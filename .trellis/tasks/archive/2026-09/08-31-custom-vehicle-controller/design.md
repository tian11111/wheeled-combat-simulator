# 设计:控制器发令前预检(缩范围后)

## Contract

预检 = 一次性探针:用与正式比赛完全相同的 `Sim.Controller.ExternalControllerBridge`
语义启动命令进程 → 发送一帧 Observation → 在 profile 的 timeout 内等待一个合法
`{v,w,requestId}` 响应 → 立即回收进程。不发明新协议、不新增参数键、不改
`docs/CONTROLLER_PROTOCOL.md` 的任何语义;预检结果不属于比赛数据。

## Boundary

- 预检是桌面壳能力:入口在 `SettingsPanel` 控制器分区(每角色一个"预检"按钮),
  执行经 `DesktopLiveDriver` 已有的进程/IO 边界模式(后台执行、UI 只收结果),
  不进入 `Sim.Core`,不触碰运行中的 `MatchEngine`/回放。
- 命令文本取自当前设置页输入框(未保存也允许预检);profile 属用户偏好,预检
  本身不写盘。
- 失败矩阵与 fault 文案对齐既有语义:命令缺失/不可执行、启动即退、坏 JSONL、
  request-id 错配、超时、握手后进程退出 —— 均映射为可定位到角色的结果文案,
  复用 `DesktopLiveDriver` 启动故障的措辞风格。

## Process Hygiene

- 探针进程持有独立生命周期:finally/取消/窗口关闭路径一律 Kill + Dispose;
- 预检进行中禁用该角色的再次预检与"应用";预检与 Arm 互斥(预检未结束不允许发令)。
- 不改变正式比赛的启动路径:预检通过不缓存"免检"状态,比赛仍按既有逻辑启动。

## Testing

- 失败矩阵用 EchoController 夹具扩展(坏行/慢响应/立即退出变体)做自动化覆盖;
- 预检后无孤儿进程(进程树断言);预检不影响既有全套测试与 parity(逐位不变)。
