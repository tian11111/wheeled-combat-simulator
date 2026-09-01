# 设计：设置界面与运行参数配置

## Data Boundary

`DesktopSettings`、参数元数据和 `SettingsValidator` 保持 Godot-free；`SettingsStore` 只在桌面壳用 `user://wushu-ring-settings.json` 做版本化原子读写。桌面偏好、pending Scenario 参数和运行中的 MatchEngine 三者分离。

## UI / Apply Semantics

- 设置面板用临时 draft 编辑，支持应用、取消、恢复默认和字段级错误。
- 分辨率/窗口模式/UI scale 应用到 Godot `Window`，即时可见；沿用 1280×720 CanvasItem stretch，不复制第二套坐标系。
- 现有 `SimParameters` 全量白名单分为常用/高级，所有项目显示单位、默认值、范围和实验性标识；自动/默认值省略对应 Scenario 字段。
- 仿真参数和 controller profile 标记“下一场生效”；Main 在新 live session/reset 时克隆 Scenario 并创建新 driver，运行中/回放中不就地修改。

## Validation

纯测试覆盖全量键、未知键、边界/非有限值、配置损坏/版本回退、保存 round-trip 和 pending application；Godot smoke 覆盖打开/关闭、模态输入、显示即时应用和回到原模式。
