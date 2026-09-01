# 实施清单：设置界面与运行参数配置

1. 建立配置 DTO、参数元数据、默认值投影和纯验证测试。
2. 实现 `user://` 存储、原子写入、损坏/未知版本回退和诊断日志。
3. 实现程序化 `SettingsPanel`：显示设置、常用/高级仿真参数、controller profile 区、应用/取消/默认/错误状态。
4. 在 Main 接入 display immediate 与 simulation/controller pending 分流；新会话/reset 才应用 pending Scenario。
5. 接入 UI scale 和双分辨率/全屏证据；确认回放与布局编辑状态不被设置覆盖。
6. 更新使用文档，运行设置/协议/回放/核心回归和 `git diff --check`。

## Rollback

优先回滚 SettingsPanel/Main wiring；保留纯配置测试和文件格式兼容逻辑，不改核心仿真。
