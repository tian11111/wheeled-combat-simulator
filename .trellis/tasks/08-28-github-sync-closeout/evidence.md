# GitHub 同步收尾证据

同步前复核：

- 分支：`main`，未新开分支。
- `origin`：`https://github.com/tian11111/wheeled-combat-simulator.git`。
- 本地 HEAD（同步前）：`9beeaf9a01af841b94f2c1219319927abce5fd31`。
- `origin/main`（同步前）：`cd1b4c48f27abb68d6526f8c1facbf3ef30266ac`。
- `git rev-list --left-right --count origin/main...HEAD`：`0 7`，远端是本地历史的严格祖先；无分叉，不需要 force push。

前三项收尾证据已分别归档：传感器漂移报告与回放、无真实 telemetry 阻断报告、Godot 真实窗口截图/日志。

## 推送结果

- 普通命令：`git push origin main`。
- 结果：`cd1b4c4..86bf623  main -> main`，未使用 force push。
- 推送后远端校验：`git ls-remote origin refs/heads/main` 为 `86bf6234626b9d9a540ba4b5d20ca26520ca73c1`。
- 当时本地 HEAD 同为 `86bf6234626b9d9a540ba4b5d20ca26520ca73c1`，`git rev-list --left-right --count origin/main...HEAD` 为 `0 0`。
- 收尾记录提交完成后，会再次执行普通 `git push origin main` 并重复 ref/工作区校验，以同步本文件本身。
