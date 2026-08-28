# GitHub 同步收尾证据

同步前复核：

- 分支：`main`，未新开分支。
- `origin`：`https://github.com/tian11111/wheeled-combat-simulator.git`。
- 本地 HEAD（同步前）：`9beeaf9a01af841b94f2c1219319927abce5fd31`。
- `origin/main`（同步前）：`cd1b4c48f27abb68d6526f8c1facbf3ef30266ac`。
- `git rev-list --left-right --count origin/main...HEAD`：`0 7`，远端是本地历史的严格祖先；无分叉，不需要 force push。

前三项收尾证据已分别归档：传感器漂移报告与回放、无真实 telemetry 阻断报告、Godot 真实窗口截图/日志。最终提交前还要把本文件补充为推送后的远端 ref、ahead/behind 和工作区状态。
