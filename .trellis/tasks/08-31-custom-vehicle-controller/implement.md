# 实施清单：自定义小车控制器接入

1. 抽取共享 bridge，保持 CLI 的协议、fault、timeout 和每场生命周期测试通过。
2. 新增桌面 driver 的 immutable snapshot/status 与 command channel；先覆盖 built-in/no-controller 路径不变。
3. 接入 Arm、Pause/Resume、Restart、Reset、Replay/Close 的有序命令和取消回收。
4. 设置面板接入双方 external profile 与启动前预检；启动失败/坏行/错配/超时/退出显示角色错误并零动作回退。
5. 验证多个连续新场次不复用旧 bridge，窗口关闭不留子进程；CLI batch/replay 和 Godot parity 不回归。
6. 更新协议/桌面使用说明并运行专项集成、全量测试、smoke 和 `git diff --check`。

## Stop Conditions

出现 UI 主线程阻塞、孤儿进程、旧 CLI 结果变化或 replay controller 被错误启动时，停止扩展设置界面，先修复/回滚 driver 集成。
