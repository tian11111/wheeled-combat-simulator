# 界面优化验证记录

## 完成内容

- HUD 根据可见逻辑视口和用户缩放定位比赛、回放、编辑及事件区域；提高比分、计时、控制器提示的可读性，统一语义色，长文案换行。
- 事件区支持滚动；用户停留在底部时跟随新事件，查看历史时保留当前位置。滚轮和镜头拖动分别经 Godot 输入管线验证。
- 设置页调整视觉对比、留白、焦点与禁用样式，保持七个页签、双层滚动、数据契约和现有操作。
- 更新 `.trellis/spec/frontend/quality-guidelines.md` 中 HUD 缩放与事件区输入契约。

## 构建与测试

- Godot .NET 工程：`dotnet build godot/GodotSim.csproj --no-restore -m:1`，0 警告、0 错误；使用本机 portable .NET 8 SDK。
- 完整 `Sim.Tests` 首次沙箱内运行：847 通过、1 跳过、1 失败；失败为 `SettingsBundleTests.TrainConfigCli_PythonArgparse_ExplicitFlagsOverrideConfigFile` 内 Python `TemporaryDirectory` 写入 `train-config.json` 时 Windows `PermissionError / WinError 5`。
- 同一失败项沙箱外单独复跑通过；完整测试集沙箱外重跑：848 通过、1 跳过、0 失败。
- 设置/编辑相关筛选测试：89 通过；`dotnet format whitespace ... --verify-no-changes` 和 `git diff --check` 通过。
- Godot `--parity-check`：3:9、2400/2400 tick、211/211 事件通过；`--camera-smoke`、`--edit-smoke` 通过。

## 真实渲染和交互证据

- 使用隔离 `APPDATA` 和 `DOTNET_ROOT`，分别在 Forward+ 和 GL Compatibility 真渲染生成 1280×720、1920×1080 截图；另检查 1280×720 @ 1.4、640×360 @ 1.4、回放及编辑界面。
- 设置七个页签在 1280×720 @ 1.4 的标题、页签及页脚可见；用程序输入分别验证 1280×720 和 640×360 下比赛页滚轮至末尾，其他页通过控件赋值确认可滚到末尾。
- 事件面板程序输入验证：滚轮改变事件列表位置而不改变相机距离；左右拖动可操作相机，从场景拖入事件区释放后不会继续拖动。
- 运行中比赛验证事件更新：滚动到历史位置后新事件到来保持位置；停留底部时新事件自动跟随。默认设置焦点下 Enter 应用后仍为发令准备，Space 未误触比赛；F10/Esc 开关正常。
- 常规长告警完整可见；极长告警进入左侧紧凑滚动区，程序滚动后可见文案末尾和设置按钮。修复了临界长度因两种布局宽度不一致而逐帧切换的问题；35 档提示长度的稳定性检查通过。
- 截图及临时测试脚本位于 git 忽略目录 `tmp/ui-polish-validation-20261105/`、`tmp/ui-polish-settings-qa-20261105/` 和 `godot/tmp/`，未覆盖历史截图或真实用户设置。

## 限制

- 640×360 @ 1.4 的内容可滚动，但物理屏幕上的文字仍偏小；要保证更大物理字号需要另行评估整体缩放策略。
- 输入验证使用 Godot `Input.ParseInputEvent` 注入；工具未能枚举隔离启动的窗口，未完成实体鼠标/键盘人工操作。
- 真实渲染日志有系统根证书读取错误；兼容渲染器提示部分 Forward+ 特效不可用，且缺失本地 GLB 时回退 primitive。这些检查仍成功退出，视觉证据不得等同于真实模型的验收。
- 以上验证在提交前完成；任务尚未归档，保留已有 `.mimosa/` 及其他无关工作区内容。
