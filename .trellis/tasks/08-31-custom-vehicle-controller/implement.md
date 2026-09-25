# 实施清单:控制器发令前预检(缩范围后)

> 原全量接入已由归档任务 `08-31-glass-settings-custom-controller`(提交 `1053e8d`)交付;
> 本清单只剩预检缺口。按序执行,每项以验证收尾。

1. [ ] 在 `DesktopLiveDriver`(或其旁路助手)实现一次性探针:启动 → 一帧握手 → 回收,
   失败矩阵文案与既有启动故障对齐;不触碰 `Sim.Core`。
2. [ ] `SettingsPanel` 控制器分区加"预检"按钮与结果区(每角色),预检中禁用重复预检与
   应用,预检与 Arm 互斥。
3. [ ] HUD 控制器状态条复用既有显示路径呈现预检结果(不新增数据源)。
4. [ ] EchoController 夹具扩展坏行/慢响应/立即退出变体,覆盖成功 + 全部失败路径;
   断言预检后无孤儿进程。
5. [ ] 全套 `dotnet test`、既有 Godot smoke/parity、`git diff --check` 全部通过;
   CLI batch/replay 行为逐位不变。

## Stop Conditions

预检引入 UI 主线程阻塞、孤儿进程、或正式比赛启动路径行为变化时,停止扩展,先修复/回滚。
