# MuJoCo 双物理后端:验证报告(2026-09-25,当前最终源码)

> 本报告只声明工程验证;不宣称真机准确,不晋升 `fidelity.json`。
> 所有数据在本轮于当前工作区源码重新实测;临时产物位于 `%TEMP%`,回放与截图不入库。

## 1. 结论

- 旧基线完整保持:352/352 测试通过,`replays/seed-42.json` 逐位复现,旧模式 batch 并行度 1/4 逐行一致,旧序列化不新增字段。
- 新 `mujoco` 模式端到端可用:CLI 录制/复现/拒绝负例、batch 1/4/32 并行一致与失败隔离、Godot parity 与真实渲染全部通过。
- **场景差异(如实报告)**:同一 seed 42 全场对抗,旧模式比分 4:49,新模式 0:0——内置 FSM 在新模式下整场无法完成"倒车登台",循环于 台沿→围栏。物理引擎本身可登 6 cm 台(专项测试通过),差异来自 FSM 登台机动与未标定轮驱动参数(轮半径 0.065 m、轮驱动力上限 0.3)的组合。详见 §4。
- 未完成:AC6 的干净 Windows x64 环境验证(本机 framework-dependent 发布不足以满足文字要求);design.md 要求的传感器×MuJoCo 碰撞几何针对性测试未实现(见 §5)。

## 2. 验证矩阵(本轮实测)

| 项目 | 结果 | 证据 |
|---|---|---|
| 全套测试 | 352/352 通过(旧基线 344 + 新增 MuJoCo 集成 7 + 协议 5 + 快照三维姿态等) | `dotnet test RobotSimulator.sln`,还原状态已修复(上次 self-contained 发布失败残留的 `net8.0/win-x64` 目标,经无 RID 还原恢复) |
| 旧回放 | `replay-check replays/seed-42.json` PASS:比分 4:49 逐位、事件 752/752;其余旧回放全部 PASS | CLI 直跑,exit 0 |
| 旧回放豁免项 | `replays/rotated-seed42.json` FAIL——**分支上先前已存在的陈旧基线,与本任务无关**:main(859a8f5)逐位 PASS;特性分支 HEAD(694ef5c,不含本任务未提交改动)同样 FAIL;根因是分支的反僵局铲刃微调提交(4d53942,有意默认 amp=0.006)之后该回放未随 5bbcd3e 再生成;向其场景注入 `antiStallBladeAmp=0` 后在分支 HEAD 逐位 PASS(16:8、340/340)。处置留给反僵局工作,本任务不改该文件 | git worktree 双端对照 + 补丁复测 |
| 旧 batch 1v4 | seeds 1-4、并行度 1/4 输出逐行一致(剔除 createdAt);4/4 `completed` | `%TEMP%\robot-sim-legacy-p1.jsonl` / `-p4.jsonl` |
| 新回放复现 | 30 s(600 ticks)、30 事件,CLI `replay-check` PASS 逐位;真实 Godot 4.7.2 Mono headless `--parity-check` PASS(600/600 ticks、30/30 事件指纹) | `%TEMP%\rs-mj-fresh2.json` |
| 新 batch 1v4 | seeds 1-4、5 s、并行度 1/4 逐行一致,4/4 `completed`,行含 `physicsBackend=mujoco` 与 `physicsModelSha256` | `%TEMP%\robot-sim-mj-stab-p1/-p4.jsonl` |
| 32 worker 压力 | 32 seeds × 5 s、并行度 32 → 32/32 `completed`、每行 100 ticks、rc=0 | `%TEMP%\robot-sim-mj-32.jsonl` |
| 失败隔离 | 缺控制器 4 worker → 4/4 `failed`、无伪比分/指纹、rc=1;坏模式(`physics.backend=invalid-mode`)预检报 `unsupported backend`、rc=2、零 JSONL | `%TEMP%\robot-sim-mj-noctl.jsonl`;`%TEMP%\robot-simulator-mujoco-bad-mode.json` |
| 回放负例 | 篡改模型哈希 / 伪造原生版本 → `replay physics identity mismatch`(recorded vs current 明示)、rc=1 | `%TEMP%\robot-simulator-mujoco-tampered-replay.json`、`-bad-version-replay.json` |
| Godot 渲染 | Forward+ 真实渲染:1280×720 ×3 tick + 1920×1080 + 旧模式回放 1920×1080 + 实况模式 160 帧 PNG 序列(20 fps)。自检:两车/三块/台面/HUD 完整,无程序集弹窗;实况序列中车辆位移/旋转、计时推进、事件流滚动(动态证据)。回放模式 `--write-movie` 序列为暂停画面(回放默认不自动播放),不可作为动态证据 | 截图为临时文件,已按惯例清理;复现命令见 §6 |

## 3. 性能与内存(可复测记录)

- 环境:Intel i9-14900HX(24C/32T)、31.7 GB RAM、Windows 11 家庭中文版、NVIDIA RTX 5070 Ti Laptop(仅 Godot 渲染用);.NET SDK 8.0.425(临时目录安装),Debug 构建,`-m:1`。
- 协议:`batch --seeds 1..32 --duration <s> --parallelism 32`;1 次预热丢弃后连续 3 次测量(短场)/2 次重复(长场);wall time 为 bash `date +%s%N` 差值(仅批处理进程,不含预热);峰值内存为 PowerShell 50 ms 轮询 `PeakWorkingSet64` 的最大值(该计数器单调,采样先于进程启动 2 s 开始,单次采样即真实峰值)。
- 冷/热状态:预热后(热);CLI 启动开销(约 0.15-0.2 s)包含在 wall time 内。

| 场景 | 旧模式 | MuJoCo 模式 |
|---|---|---|
| 32×5 s 墙钟(3 次) | 269 / 249 / 253 ms(中位 253) | 379 / 377 / 387 ms(中位 379) |
| 32×5 s 聚合实时倍率(160 s 模拟) | ≈632× | ≈422× |
| 32×5 s 峰值工作集(3 次) | 49.9-62.6 MiB | 86.3-90.7 MiB |
| 32×120 s 墙钟(2 次) | 1405 / 1573 ms | 3792 / 3848 ms |
| 32×120 s 聚合实时倍率(3840 s 模拟) | ≈2546× | ≈1004× |
| 32×120 s 峰值工作集(2 次) | 381.4 / 389.3 MiB | 422.4 / 422.6 MiB |

资源释放/长时稳定性:两次独立的 32×120 s 重复中,MuJoCo 模式峰值工作集几乎相同(422.36 vs 422.64 MiB),且每场独立创建/销毁 `mjModel/mjData` 后无跨场增长;两次重复逐 seed `resultFingerprint` 完全一致(长场确定性);新旧模式指纹零交集;4/4 长场文件 32 行全 `completed`、2400 ticks、doneReason=`比赛时间结束`。上表数值为工程观测,不是性能承诺。

## 4. 场景差异报告(R5)

### 4.1 内置 FSM 无法在新模式登台(显著,未修复)

- 复现:`dotnet run --project src/Sim.Cli -- match --seed 42 --duration 30 --scenario scenarios/wushu-ring-2026-mujoco.json --events`。两车全程 MOUNT_RING:姿态确认→摆正完成→倒车登台(780/800)→倒车超时失败→前冲找墙→冲满时限(围栏)→换面重试,循环至终场;比分 0:0、方块未被触碰。
- 同 seed 同时长旧模式:比分 4:49,FSM 正常登台/推块/得分(旧基线回放为证)。
- 物理能力对照:专项测试 `NativeMode_MountsTheSixCentimetreStageContinuously` 用直接驱动指令在 MuJoCo 中稳定登上 6 cm 台面,无穿透、无位置跳跃。即:动力学可登台,失败的是 FSM 的倒车登台机动(为旧二维物理的手绘模型调校)与未标定车轮参数(轮半径 0.065 m、轮驱动力上限 0.3)在新接触模型下的组合。
- 判定:不属于 AC2 违规(AC2 要求的可重复登台/掉台/推块场景由直接驱动测试提供);属于必须报告的模式差异。修复路径是真机遥测标定(既有任务 08-28)或为新模式单独调校 FSM,本任务不改 FSM、不放宽测试掩盖。
- 传感器一致性边界(设计如此,R3):首轮传感器仍为解析模型——`SensorSampler` 消费车体平面投影 (x,y,th) 与 `_field` 台面/围栏几何,不做 MuJoCo raycast。已知不一致:①车体俯仰/侧倾不改变传感器安装位姿(按平放车体解析计算);②台沿/悬空判定用台面矩形而非 MuJoCo 接触;③方块对距离传感器是 2D 圆形投影,不反映 3D 姿态。design.md 预期的针对性差异测试未实现(见 §5),因此上述清单未经测试固化,仅作边界声明。

### 4.2 其余观察

- 回放回放模式在 Godot 中默认暂停在 seek 位置,`--write-movie` 序列为静止画面;动态视觉证据须用实况模式(`--auto-arm`)采集。
- 启动时非致命 stderr:根证书读取失败、退出时 Texture RID 泄漏警告(仅渲染端,不影响 parity/比分);与交接记录一致。
- 新模式 `physicsModelSha256` 随场景/MJCF 内容变化:同场景同哈希、跨场景不同哈希为预期。

## 5. 验收标准状态

| AC | 状态 | 说明 |
|---|---|---|
| AC1/R1 旧行为隔离 | ✅ | 352 测试、旧回放(种子 42 契约文件逐位;`rotated-seed42.json` 为分支先前遗留的陈旧基线,见 §2 豁免行)、旧 batch 1v4、旧序列化无新字段(旧 batch 行无 `physicsBackend`) |
| AC2/R2 可重复场景 | ✅ | 直行/转向、车车不穿透、推块出界、6 cm 登台、掉台/下台:7 项集成测试;无非有限状态/穿透/跳跃(测试断言) |
| AC3/R2-R3 跨端一致 | ✅ | CLI↔Godot parity PASS(600/600、30/30);真实重启语义由 `NativeMode_RealRestartResetsOnlyTargetAndPreservesClock` 锁定 |
| AC4/R3 Godot 渲染 | ✅ | 双分辨率真实渲染 + 三维姿态显示;旧快照回放加载正常(4:6 画面);布局编辑逻辑由既有测试与编辑器冒烟覆盖 |
| AC5/R4 复现与并行 | ✅ | 同机复现、篡改/版本拒绝、1/4/32 一致、32 worker 压力与失败隔离 |
| AC6/R4 发布与干净环境 | ⚠️ 部分 | win-x64 framework-dependent 发布成功且含锁定 DLL/许可;缺 DLL/坏场景预检清晰失败;**干净机器加载未实测**;self-contained 发布因 NU1301(离线还原)未完成 |
| AC7/R5 对照报告 | ✅(本文件) | 吞吐/实时倍率/峰值内存/场景差异;`fidelity.json` 未晋升、未标定等级不变 |

## 6. 复现命令

```bash
# 全套测试(还原必须不带 RID;构建单线程)
dotnet restore RobotSimulator.sln && dotnet test RobotSimulator.sln -m:1
# 旧基线
dotnet run --project src/Sim.Cli -- replay-check replays/seed-42.json
# 新模式录制/复现/负例
dotnet run --project src/Sim.Cli -- replay-record --seed 42 --duration 30 \
  --scenario scenarios/wushu-ring-2026-mujoco.json --out <replay.json>
dotnet run --project src/Sim.Cli -- replay-check <replay.json>
# batch 并行与压力(32 = BatchCommand.MaxParallelism 上限)
dotnet run --project src/Sim.Cli -- batch --scenario scenarios/wushu-ring-2026-mujoco.json \
  --seeds 1,...,32 --duration 120 --parallelism 32 --out <out.jsonl>
# Godot(必须用真实 Mono EXE,勿用 WinGet Links 符号链接)
Godot_v4.7.2-stable_mono_win64.exe --headless --path godot -- --parity-check <replay.json>
# 实况动态证据(独立用户目录避免污染本机设置)
APPDATA=<tmp-qa-dir> Godot_...exe --resolution 1280x720 --path godot \
  --write-movie <dir>/frame.png --fixed-fps 20 --quit-after 160 -- \
  --scenario-path scenarios/wushu-ring-2026-mujoco.json --auto-arm
```
