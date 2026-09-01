# 设计：自定义小车控制器接入

## Contract

MVP 使用外部命令/脚本，不嵌入编辑器。保留既有 JSONL：observation → `{v,w,requestId}`；request-id、有限值、timeout、zero-action、fault、每场独立进程和进程树回收语义逐位沿用。

## Shared Bridge

把现有 `PythonBridge` 放入最小 `Sim.Controller` 类库，CLI 与 Godot 引用同一实现；Process/线程仍在 IO 边界，`Sim.Core` 只保留 `IControllerAdapter`/`MatchEngine`。CLI 旧 match/replay-record/batch 先回归后再接桌面。

## Desktop Runtime

因为 bridge 的 `Decide` 是同步截止时间等待，桌面 live match 使用 `DesktopLiveDriver` 后台固定步长循环：driver 独占 engine、bridges 和命令队列，主线程只投递裁判命令和消费有界 snapshot/status 队列。启动、故障、重置、回放切换和关闭均在 finally/取消路径释放 bridges；回放绝不启动 controller。

## UI

设置面板支持 us/them 各自选择内置 FSM 或 external，保存命令/脚本文本和 timeout，发令前显示预检/连接状态。路径和命令属于 user settings，不进入 Scenario/replay/fingerprint；外部程序风险由 UI 明示，不声称沙箱隔离。
