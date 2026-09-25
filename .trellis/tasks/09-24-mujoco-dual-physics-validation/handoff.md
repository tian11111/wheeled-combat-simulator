# MuJoCo 双物理后端：2026-09-25 交接

> **2026-09-25 第二轮（trellis-continue）已完成的增量**：
> 修复被 self-contained 发布失败污染的 `obj/project.assets.json`（无 RID 还原）；在最终源码上重跑全套 352/352、
> `replays/seed-42.json` 逐位 PASS、旧 batch 1v4 一致；新回放复现/篡改拒绝/版本拒绝/p1=p4/32 worker/坏模式预检/缺控制器隔离全部通过；
> 长稳 32×120s×2 双模式全 `completed`、重复指纹一致，性能/内存可复测记录入报告；真实 Godot headless parity PASS +
> 双分辨率静帧 + 实况动态 PNG 序列 + 旧模式回放渲染（自检通过，visual-judge 子代理因供应商不可用改自检）；
> 发现并定位 `replays/rotated-seed42.json` 失败为**分支陈旧基线**（反僵局提交后未再生，main PASS / 注入 amp=0 PASS，与本任务无关）；
> 发现 **FSM 在新模式整场无法登台**（seed 42：旧 4:49 vs 新 0:0，物理可登台但 FSM 倒车登台机动失败）——已如实写入报告；
> 新增 `validation-report.md`；更新 `docs/ARCHITECTURE.md`、`docs/CLI.md`、`godot/README.md`、`.trellis/spec/sim/index.md`。
> 仍未完成：AC6 干净机验证（保持 ⚠️）；design.md 要求的传感器×MuJoCo 针对性测试缺失（报告 §5）。未提交任何改动。

## 结论与边界

任务仍为 `in_progress`，**尚未完成 PRD 全部 AC**；不要归档、宣称真机标定完成，或把本交接当作质量门禁通过。用户此轮只要求写入 Trellis 以便更换 agent；未授权本轮提交或推送。工作区有大量未提交改动，尤其须保留原有 `godot/src/ArenaVisualizer.cs` 去装饰修改、`.trellis/spec/frontend/component-guidelines.md`、`.learnings/` 与另一个规划中的 Trellis 任务。不要用 reset/checkout 清理。

本任务已经接入可选 `mujoco` 场景：`Sim.Protocol` 加性物理身份/三维快照，`Sim.Core` 物理后端接口，`Sim.Mujoco` 官方 3.14.0 原生运行时与 MJCF，`Sim.Hosting` 共用创建入口，CLI/Godot 走同一后端。旧模式仍为默认。官方 DLL 和许可位于 `src/Sim.Mujoco/runtimes/win-x64/native/`；DLL SHA-256 为 `da487aed0d534fc52b1612a09571f9f2836b8abb9f46567ab5868620ca0e7418`。模型参数（如轮半径 0.065 m、轮驱动力上限 0.3）是未标定工程初值。

## 已取得的验证证据

| 项目 | 当前结果 | 证据/限制 |
|---|---|---|
| 旧基线 | 本轮较早阶段全套原有 344 项测试、`replays/seed-42.json` replay-check 通过；旧/新 4 seed 在并行度 1/4 下逐行一致 | **新增测试以后尚未重跑全套**；勿据此勾选 AC1/最终质量门禁 |
| 原生物理 | `MujocoIntegrationTests` 最新一次 7/7 通过：直行/转向、推块接触、两车迎面不穿透、推块下台、倒车登 6 cm 台、车辆连续掉台、真实重启 | 新增的车车、推块出界、掉台测试在 `src/Sim.Tests/MujocoIntegrationTests.cs`；尚未做更宽的长期数值稳定性检查 |
| Godot parity | 用真实 Godot 4.7.2 Mono 安装文件运行新 20-tick 回放，CLI/Godot parity `PASS`：比分 0:0、ticks 20/20、events 10/10 | WinGet `godot.exe` 符号链接会把 Mono 错误引到 `.../WinGet/Links/GodotSharp/Api/Debug`，产生用户截图中的 `.NET assemblies not found`；必须用下面的真实 EXE。旧临时回放因模型哈希改变被正确拒绝 |
| 实际渲染 | Forward+ 真实渲染 1280×720 与 1920×1080 截图可见两车、三块、台面及 HUD，未出现程序集弹窗 | 临时截图：`%TEMP%\robot-simulator-mujoco-godot-capture.png`、`%TEMP%\robot-simulator-mujoco-godot-1920.png`；1920 截图通过临时独立 Godot 用户配置完成，已清理该工作区配置。尚无动态翻转的逐帧视觉证据；启动时有非致命证书/Texture RID 警告 |
| 并行/隔离 | 发布版 MuJoCo batch：32 seed × 5 s、并行度 32 → 32/32 `completed`，每行 100 ticks；缺失控制器的 32 worker → 32/32 `controller_start_failed`、exit 1，无伪造比分/指纹 | 临时输出 `%TEMP%\robot-simulator-bench-mujoco.jsonl`、`%TEMP%\robot-simulator-mujoco-32-fail.jsonl`；需在最终版复跑并核对资源释放/长时压力 |
| 性能对照 | 同机单次 32 seed × 5 s、并行度 32：旧模式 0.2663 s/峰值工作集 65.88 MiB；MuJoCo 0.3803 s/84.01 MiB，均 32/32 完成、每行 100 ticks；合计模拟时间各 160 s | 仅是本机单次冷/热状态未严格控制的工程观测，不能当性能承诺；按 160 s/墙钟，约 601× 与 421× 聚合实时倍率。复测时记录硬件、次数、冷/热状态 |
| 发布与负例 | `win-x64 --self-contained false` Release 发布成功，包内 DLL/许可/第三方声明齐全，发布包短场次和回放成功；缺 DLL、篡改 DLL、坏 replay 身份、未知模式、非法 tick 均清楚失败且预检无伪成功 JSONL；无 DLL 时旧模式仍可运行 | **未在干净 Windows x64 机器实测**。`--self-contained true` 因 NuGet `NU1301` 无法访问服务索引，在运行时包还原阶段失败；记录见 `.learnings/ERRORS.md`。不能勾选 AC6 的干净环境部分 |

## 下一位 agent 的执行顺序

1. 先读 `prd.md`、`design.md`、`implement.md`、`implement.jsonl`/`check.jsonl` 与 `.trellis/spec/sim/index.md`；确认 `git status`，保留全部既有改动。当前分支 `feat/godot-3d-visual`，本任务尚无 commit/push。
2. 在**当前最终源码**上恢复/运行全套 .NET 测试、旧 `replays/seed-42.json`、旧重启回放、旧 batch 固定指纹、新回放和 1/4/32 worker 对比。临时 SDK 为 `%TEMP%\robot-simulator-dotnet-sdk\dotnet.exe`；设置 `DOTNET_CLI_HOME` 为临时目录，`NUGET_PACKAGES=C:\Users\Neco\.nuget\packages`，构建用 `-m:1`。上一次自包含发布失败可能改变 `obj/project.assets.json`，因此先检查还原状态；不要把旧的 344 项结果写成最终全套测试。
3. 用真实 Godot 路径重跑 `--headless --path godot -- --parity-check <新回放>`，并检查实际渲染的块翻转/车辆俯仰侧倾、旧快照及布局编辑。真实 EXE：`C:\Users\Neco\AppData\Local\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`。不要用 WinGet `Links\godot.exe` 判断 Mono 是否可用。
4. 补充可复现的长时稳定性/资源释放和场景差异报告，核查解析传感器与 MuJoCo 碰撞几何的已知不一致；将上表单次性能数值升级为可复测记录。更新 `docs/ARCHITECTURE.md`、`docs/CLI.md`、`godot/README.md` 的模式、启动、部署与保真度边界，但**不要晋升 `fidelity.json`**。
5. 若要关闭 AC6，优先在获准联网或预置 runtime packs 后完成 self-contained 发布，并在真正干净的 Windows x64 环境加载原生库；只有本机 framework-dependent 发布不足以满足文字要求。无法取得干净机时保留 AC6 未完成并如实交付阶段结果。
6. 按 `trellis-check` 复核所有 AC、`git diff --check`、全套测试与截图。确认没有混入用户的独立修改后，再依据用户新的明确授权决定提交/推送；本交接不执行提交。

## 文件和临时产物

- 新场景：`scenarios/wushu-ring-2026-mujoco.json`；新原生层：`src/Sim.Mujoco/`；装配入口：`src/Sim.Hosting/`。
- 新测试：`src/Sim.Tests/MujocoIntegrationTests.cs`、`src/Sim.Tests/MujocoProtocolTests.cs`，以及 `SnapshotViewTests.cs` 的三维姿态覆盖。
- 锁定/ABI：`research/mujoco-windows-lock.md`、`research/abi-probe.c`；临时新回放 `%TEMP%\robot-simulator-godot-parity-fresh.json` 可能随清理消失，消失时须按当前模型重新录制，不可复用旧模型哈希回放。
- 新模式 `physicsModelSha256` 依场景/MJCF 内容而变；不同场景的 hash 不同是预期，不能把一个临时回放的 hash 当全局常量。
